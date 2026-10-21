using System.Collections.Immutable;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Application.Scanning;

/// <summary>A buffered scan after aggregation: its observations, which no longer change, and the
/// folder aggregation verified to conserve every observed DATA byte. Only a completed scan can be
/// published.</summary>
public sealed class CompletedScan
{
    private readonly FolderGraph graph;

    internal CompletedScan(
        ScanDescriptor descriptor,
        IReadOnlyList<StagedObservation> observations,
        FolderAggregationResult aggregation,
        ImmutableArray<UnattributedSummary> diagnostics,
        FolderGraph graph
    )
    {
        Descriptor = descriptor;
        Observations = observations;
        Aggregation = aggregation;
        Diagnostics = diagnostics;
        this.graph = graph;
        RootReference = graph.Root;
    }

    public ScanDescriptor Descriptor { get; }

    /// <summary>Observations in the order they were added.</summary>
    public IReadOnlyList<StagedObservation> Observations { get; }
    public FolderAggregationResult Aggregation { get; }

    /// <summary>Capacity that could not be attributed to a folder, grouped by reason.</summary>
    public ImmutableArray<UnattributedSummary> Diagnostics { get; }

    /// <summary>The volume root of the scan.</summary>
    public FileReference RootReference { get; }

    public FolderCapacitySample Root => Aggregation.Folders[RootReference];

    public FolderAccountingSummary ReadAccountingDiagnostics(
        FileReference scope,
        IReadOnlyDictionary<FileReference, FileStorageTraits>? traits = null,
        CancellationToken cancellationToken = default
    )
    {
        var resolver = new ScanDomainResolver(graph);
        return FolderAccountingDiagnostics.Analyze(
            graph,
            scope,
            Observations
                .Where(o => o.File is not null)
                .Select(o => resolver.Resolve(o.File!, cancellationToken)),
            traits,
            cancellationToken
        );
    }

    /// <summary>Conservative heuristic for database rows, names, streams, indexes and page slack.
    /// Uses metadata lengths, never file content sizes. Not an exact serialized size or RAM limit.</summary>
    public ulong EstimateDatabaseBytes(CancellationToken token = default)
    {
        ulong bytes = 64 * 1024;
        checked
        {
            foreach (var item in Observations)
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
}
