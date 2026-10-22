using Something.Ntfs;
using Something.Statistics;

namespace Something.Application.Scanning;

/// <summary>Owned compact observations. Keeps all persisted facts, but no raw records or DATA runs.
/// Complete only after successful enumeration; only a <see cref="CompletedScan"/> can be published, so
/// cancellation before completion never writes a generation.</summary>
public sealed class BufferedScan
{
    // Ranges up to this many records track identities in a bitmap (8 MiB at most) instead of a
    // hash set (about 30 bytes per record); larger, unusual ranges keep the hash set.
    internal const ulong BitmapRecordLimit = 64UL << 20;
    internal const ulong InitialObservationLimit = 4UL << 20;

    private readonly List<StagedObservation> observations;
    private ulong[]? seen;
    private HashSet<ulong>? indices;
    private CompletedScan? completedScan;

    public BufferedScan(ScanDescriptor descriptor)
    {
        ScanDescriptor.Validate(descriptor);
        Descriptor = descriptor;
        ulong records = descriptor.Range.RecordCount;
        if (records <= BitmapRecordLimit)
        {
            seen = new ulong[(records + 63) / 64];
            // Observations never exceed the MFT range; reserving it avoids growth copies. The cap
            // bounds the reservation (32 MiB of references) when much of the MFT is free.
            observations = new((int)Math.Min(records, InitialObservationLimit));
        }
        else
        {
            indices = [];
            observations = [];
        }
    }

    public ScanDescriptor Descriptor { get; }
    public int Count => observations.Count;

    public void Add(MftScanEntry entry, CancellationToken cancellationToken = default) =>
        Add(StagedObservation.FromEntry(entry, cancellationToken), cancellationToken);

    public void Add(StagedObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completedScan is not null)
            throw new InvalidOperationException("Completed observations are immutable.");
        StagedObservation.Validate(observation, Descriptor.Range);
        if (!MarkSeen(observation.RecordIndex))
            throw new InvalidDataException("Duplicate buffered record identity.");
        observations.Add(observation);
    }

    // The index is validated to lie inside the descriptor range before it is marked.
    private bool MarkSeen(ulong recordIndex)
    {
        if (seen is null)
            return indices!.Add(recordIndex);
        ulong offset = recordIndex - Descriptor.Range.StartIndex;
        ref ulong word = ref seen[offset / 64];
        ulong bit = 1UL << (int)(offset % 64);
        if ((word & bit) != 0)
            return false;
        word |= bit;
        return true;
    }

    /// <summary>Aggregates the observations once; later calls return the same result. No observation
    /// can be added afterwards.</summary>
    public CompletedScan Complete(
        CancellationToken cancellationToken = default,
        Action<ScanWorkProgress>? progress = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completedScan is not null)
            return completedScan;
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
        var diagnostics = collector.Snapshot();
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
        var aggregation = result with
        {
            HasUnknownAttribution =
                result.HasUnknownAttribution
                || observations.Any(o => o.Kind == MftScanEntryKind.Unresolved && o.File is null),
        };
        seen = null;
        indices = null;
        return completedScan = new(Descriptor, observations, aggregation, diagnostics, graph);

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
