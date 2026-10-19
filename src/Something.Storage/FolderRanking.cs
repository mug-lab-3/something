using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>Paths are observed names for display, not paths to open or follow.</summary>
public enum FolderRankingMetric
{
    PerVisibleNameUnnamedData,
    SingleCountAllData,
}

/// <summary>Depth is absolute: volume root is zero. A leaf has no observed valid child directories.</summary>
public sealed record FolderRankingFilter(int? Depth = null, bool LeafOnly = false);

public sealed record RankedFolder(string RelativePath, FolderCapacitySample Sample)
{
    public FolderRankingMetric Metric { get; init; }
    public CapacityAmount Data =>
        Metric == FolderRankingMetric.SingleCountAllData
            ? Sample.SubtreeData
            : Sample.Entries?.SubtreeData
                ?? throw new InvalidOperationException(
                    "This generation has no per-name aggregation."
                );
}

public sealed partial class ScanStore
{
    /// <summary>All matching ordinary folders, ordered by capacity; no top-N truncation.</summary>
    public ImmutableArray<RankedFolder> ReadFolderAnalysis(
        Guid scanId,
        FolderRankingFilter filter,
        CancellationToken cancellationToken = default,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes
    ) => ReadRankedFolders(scanId, int.MaxValue, cancellationToken, filter: filter, sort: sort);

    /// <summary>Defaults to unnamed DATA per visible name. Excludes root and NTFS metadata; nested rows overlap.</summary>
    public ImmutableArray<RankedFolder> ReadLargestFolders(
        Guid scanId,
        int count = 5,
        CancellationToken cancellationToken = default,
        FolderRankingMetric metric = FolderRankingMetric.PerVisibleNameUnnamedData,
        FolderRankingFilter? filter = null,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes
    )
    {
        if (count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(count));
        return ReadRankedFolders(scanId, count, cancellationToken, metric, filter, sort);
    }

    internal ImmutableArray<RankedFolder> ReadRankedFolders(
        Guid scanId,
        int count,
        CancellationToken cancellationToken,
        FolderRankingMetric metric = FolderRankingMetric.PerVisibleNameUnnamedData,
        FolderRankingFilter? filter = null,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes,
        FolderAggregationResult? aggregation = null
    )
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!Enum.IsDefined(metric))
            throw new ArgumentOutOfRangeException(nameof(metric));
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        filter ??= new();
        if (filter.Depth is < 1)
            throw new ArgumentOutOfRangeException(
                nameof(filter),
                "Depth must be at least 1; the volume root is excluded."
            );
        cancellationToken.ThrowIfCancellationRequested();
        aggregation ??= ReadAggregation(scanId, cancellationToken);
        if (
            metric == FolderRankingMetric.PerVisibleNameUnnamedData
            && aggregation.Folders.Values.Any(f => f.Entries is null)
        )
            throw new InvalidOperationException(
                "This historical generation has no per-name aggregation. Rescan or select SingleCountAllData."
            );
        var names = new Dictionary<ulong, List<FolderNameEntity>>();
        foreach (
            var name in context
                .FolderNames.AsNoTracking()
                .Where(n => n.ScanId == scanId)
                .AsEnumerable()
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong index = checked((ulong)name.RecordIndex);
            if (!names.TryGetValue(index, out var list))
                names.Add(index, list = []);
            list.Add(name);
        }
        var root = aggregation.Folders.Keys.Single(r => r.RecordIndex == 5);
        var graph = FolderGraph.Build(
            root,
            aggregation.Folders.Keys.Select(r => new FolderObservation(
                r,
                [
                    .. names
                        .GetValueOrDefault(r.RecordIndex, [])
                        .Where(n => n.Namespace != FileNameNamespace.Dos)
                        .Select(n =>
                            FileReference.Create(checked((ulong)n.ParentIndex), n.ParentSequence)
                        )
                        .Distinct(),
                ]
            )),
            cancellationToken
        );
        var resolver = new ScanDomainResolver(graph);
        var hasChildren = filter.LeafOnly
            ? graph
                .Nodes.Values.Where(n =>
                    n.Depth is not null && n.Parent is not null && n.Parent != n.Reference
                )
                .Select(n => n.Parent!.Value)
                .ToHashSet()
            : [];
        bool Ordinary(FolderCapacitySample sample) =>
            resolver
                .Resolve(
                    new(
                        sample.Reference,
                        true,
                        CapacityDomain.Ordinary,
                        CapacityResolution.Resolved,
                        [],
                        [],
                        true
                    ),
                    cancellationToken
                )
                .Domain == CapacityDomain.Ordinary;
        var largest = aggregation
            .Folders.Values.Where(s =>
                s.Reference != root
                && graph.Nodes[s.Reference].Depth is not null
                && Ordinary(s)
                && (filter.Depth is null || graph.Nodes[s.Reference].Depth == filter.Depth)
                && (!filter.LeafOnly || !hasChildren.Contains(s.Reference))
            )
            .OrderByDescending(s =>
            {
                var data =
                    metric == FolderRankingMetric.PerVisibleNameUnnamedData
                        ? s.Entries!.SubtreeData
                        : s.SubtreeData;
                return sort == FolderEntrySort.AllocatedBytes
                    ? data.AllocatedBytes
                    : data.LogicalBytes;
            })
            .ThenBy(s => s.Reference.RecordIndex)
            .Take(count)
            .ToArray();
        // Each ancestor's path is built once and shared, so all folders cost O(folders), not O(folders × depth).
        var paths = new Dictionary<FileReference, string> { [root] = "" };
        var pending = new Stack<FileReference>();
        var result = ImmutableArray.CreateBuilder<RankedFolder>();
        foreach (var sample in largest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = sample.Reference;
            while (!paths.ContainsKey(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Push(current);
                current = graph.Nodes[current].Parent!.Value;
            }
            while (pending.TryPop(out var child))
            {
                var parent = graph.Nodes[child].Parent!.Value;
                string name = DisplayName(names[child.RecordIndex], parent);
                paths[child] = parent == root ? name : paths[parent] + '\\' + name;
            }
            result.Add(new(paths[sample.Reference], sample) { Metric = metric });
        }
        return result.ToImmutable();
    }

    private static string DisplayName(List<FolderNameEntity> names, FileReference parent) =>
        names
            .Where(n =>
                n.Namespace != FileNameNamespace.Dos
                && (ulong)n.ParentIndex == parent.RecordIndex
                && n.ParentSequence == parent.SequenceNumber
            )
            .OrderBy(n =>
                n.Namespace == FileNameNamespace.Win32
                || n.Namespace == FileNameNamespace.Win32AndDos
                    ? 0
                    : 1
            )
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .First()
            .Name;
}
