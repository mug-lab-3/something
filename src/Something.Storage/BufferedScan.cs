using System.Collections.Immutable;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>Owned compact observations. Keeps all persisted facts, but no raw records or DATA runs.
/// Complete only after successful enumeration. Cancellation before publication never writes a generation.</summary>
public sealed class BufferedScan
{
    private readonly List<StagedObservation> observations = [];
    private readonly HashSet<ulong> indices = [];
    private readonly StoredScan validation;
    private FolderAggregationResult? aggregation;
    private ImmutableArray<UnattributedSummary> diagnostics;
    private FolderGraph? completedGraph;

    public FolderAccountingSummary ReadAccountingDiagnostics(
        FileReference scope,
        IReadOnlyDictionary<FileReference, FileStorageTraits>? traits = null,
        CancellationToken cancellationToken = default
    )
    {
        if (aggregation is null || completedGraph is not { } graph)
            throw new InvalidOperationException("Buffered enumeration has not completed.");
        var resolver = new ScanDomainResolver(graph);
        return FolderAccountingDiagnostics.Analyze(
            graph,
            scope,
            observations
                .Where(o => o.File is not null)
                .Select(o => resolver.Resolve(o.File!, cancellationToken)),
            traits,
            cancellationToken
        );
    }

    public BufferedScan(ScanDescriptor descriptor)
    {
        ScanStore.ValidateDescriptor(descriptor);
        Descriptor = descriptor;
        validation = new(Guid.Empty, descriptor, StoredScanState.Running, null);
    }

    public ScanDescriptor Descriptor { get; }
    public int Count => observations.Count;

    /// <summary>Conservative heuristic for SQLite rows, names, streams, indexes and page slack.
    /// Uses metadata lengths, never file content sizes. Not an exact serialized size or RAM limit.</summary>
    public ulong EstimateDatabaseBytes(CancellationToken token = default)
    {
        if (aggregation is null)
            throw new InvalidOperationException("Estimate after completing buffered aggregation.");
        ulong bytes = 64 * 1024;
        checked
        {
            foreach (var item in observations)
            {
                token.ThrowIfCancellationRequested();
                bytes += 256;
                if (item.File is not { } file)
                    continue;
                foreach (var name in file.Names)
                {
                    bytes += 384 + (ulong)name.Name.Length * 2;
                    if (file.IsDirectory)
                        bytes += 256 + (ulong)name.Name.Length * 2;
                }
                foreach (var stream in file.Streams)
                    bytes += 320 + (ulong)stream.Name.Length * 2;
                if (file.IsDirectory)
                    bytes += 512;
            }
            return (bytes + bytes / 2 + 16383) / 16384 * 16384;
        }
    }

    internal IReadOnlyList<StagedObservation> Observations => observations;
    public FolderAggregationResult Aggregation =>
        aggregation
        ?? throw new InvalidOperationException("Buffered enumeration has not completed.");
    public ImmutableArray<UnattributedSummary> Diagnostics =>
        aggregation is not null
            ? diagnostics
            : throw new InvalidOperationException("Buffered enumeration has not completed.");

    public void Add(MftScanEntry entry, CancellationToken cancellationToken = default) =>
        Add(StagedObservation.FromEntry(entry, cancellationToken), cancellationToken);

    public void Add(StagedObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (aggregation is not null)
            throw new InvalidOperationException("Completed observations are immutable.");
        ScanStore.ValidateObservation(observation, validation);
        if (!indices.Add(observation.RecordIndex))
            throw new InvalidDataException("Duplicate buffered record identity.");
        observations.Add(observation);
    }

    public FolderAggregationResult Complete(
        CancellationToken cancellationToken = default,
        Action<ScanWorkProgress>? progress = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (aggregation is not null)
            return aggregation;
        var reporter = new ScanWorkReporter(progress);
        reporter.Start(ScanWorkStage.CollectingFolders, observations.Count);
        var folders = new List<FolderObservation>();
        long collected = 0;
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.File is { IsDirectory: true, LinkSetComplete: true } file)
                folders.Add(
                    new(
                        file.Reference,
                        [
                            .. file
                                .Names.Where(n => n.Namespace != FileNameNamespace.Dos)
                                .Select(n => n.Parent)
                                .Distinct(),
                        ]
                    )
                );
            reporter.Advance(++collected);
        }
        var root =
            folders.SingleOrDefault(f => f.Reference.RecordIndex == 5)
            ?? throw new InvalidDataException("A complete root observation is required.");
        reporter.Start(ScanWorkStage.BuildingHierarchy);
        var graph = FolderGraph.Build(root.Reference, folders, cancellationToken);
        var resolver = new ScanDomainResolver(graph);
        var collector = new UnattributedDiagnostics(graph);
        CapacityAmount observed = default;
        reporter.Start(ScanWorkStage.AggregatingFiles, observations.Count);
        var result = new FolderAggregator(graph).Aggregate(
            ResolvedFiles(),
            cancellationToken,
            collector.Add,
            (completed, total) =>
            {
                if (completed == 0)
                    reporter.Start(ScanWorkStage.RollingUpFolders, total);
                else
                    reporter.Advance(completed);
            }
        );
        reporter.Start(ScanWorkStage.ValidatingAggregation);
        if (!result.Folders.TryGetValue(root.Reference, out var rootSample))
            throw new InvalidDataException("A valid root hierarchy is required for publication.");
        if (
            observed
            != rootSample.SubtreeData + result.Metadata.KnownData + result.Unattributed.KnownData
        )
            throw new InvalidDataException("Buffered aggregation did not conserve observed DATA.");
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics = collector.Snapshot();
        CapacityAmount diagnosticCapacity = default;
        ulong diagnosticResolved = 0,
            diagnosticUnresolved = 0;
        foreach (var group in diagnostics)
        {
            diagnosticCapacity += group.KnownData;
            diagnosticResolved = checked(diagnosticResolved + group.ResolvedCount);
            diagnosticUnresolved = checked(diagnosticUnresolved + group.UnresolvedCount);
        }
        if (
            diagnosticCapacity != result.Unattributed.KnownData
            || diagnosticResolved != result.Unattributed.ResolvedCount
            || diagnosticUnresolved != result.Unattributed.UnresolvedCount
        )
            throw new InvalidDataException(
                "Unattributed diagnostics do not match the capacity bucket."
            );
        aggregation = result with
        {
            HasUnknownAttribution =
                result.HasUnknownAttribution
                || observations.Any(o => o.Kind == MftScanEntryKind.Unresolved && o.File is null),
        };
        completedGraph = graph;
        indices.Clear();
        indices.TrimExcess();
        return aggregation;

        IEnumerable<FileCapacityObservation> ResolvedFiles()
        {
            long completed = 0;
            foreach (var item in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.File is { } file)
                {
                    foreach (var stream in file.Streams)
                        observed += new CapacityAmount(
                            (UInt128)stream.LogicalBytes,
                            (UInt128)stream.AllocatedBytes
                        );
                    yield return resolver.Resolve(file, cancellationToken);
                }
                reporter.Advance(++completed);
            }
        }
    }
}
