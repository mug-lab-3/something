using System.Numerics;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Application;

public sealed partial class ScanSession
{
    private FileVisibilityOptions visibility = new();
    private VisibleScan? visibleScan;
    private FileReference? cachedVisibleParent;
    private FolderEntry[] cachedVisibleEntries = [];
    private (bool DirectoriesOnly, FolderEntrySort Sort, bool Descending)? cachedVisibleOrder;
    private FolderEntry[] orderedVisibleEntries = [];
    private readonly FolderCapacitySample unfilteredRootSample;

    public async Task SetVisibilityAsync(FileVisibilityOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (options == visibility)
                return;
            var projection = options.IncludesAll
                ? null
                : await Task.Run(
                    () =>
                        new VisibleScan(Root, () => store.ReadFiles(ScanId, token), options, token),
                    token
                );
            token.ThrowIfCancellationRequested();
            visibleScan = projection;
            visibility = options;
            RootSample = projection?.Aggregation.Folders[Root] ?? unfilteredRootSample;
            cachedVisibleParent = null;
            cachedVisibleEntries = [];
            cachedVisibleOrder = null;
            orderedVisibleEntries = [];
        }
        finally
        {
            gate.Release();
        }
    }

    private FolderEntryPage ReadVisibleFolder(
        FileReference parent,
        int page,
        bool directoriesOnly,
        CancellationToken token,
        FolderEntrySort sort,
        bool includeTotalCount,
        bool descending
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        var projection = visibleScan!;
        if (projection.ExcludedFolders.Contains(parent))
            return new([], false, includeTotalCount ? 0 : null);
        if (cachedVisibleParent != parent)
        {
            var rows = new List<FolderEntry>();
            for (int sourcePage = 0; ; sourcePage = checked(sourcePage + 1))
            {
                token.ThrowIfCancellationRequested();
                var source = store.ReadFolderEntries(
                    ScanId,
                    parent,
                    sourcePage,
                    cancellationToken: token
                );
                foreach (var entry in source.Entries)
                {
                    if (
                        !visibility.Includes(entry.StandardFileAttributes)
                        || projection.ExcludedFolders.Contains(entry.Reference)
                    )
                        continue;
                    if (
                        entry.IsDirectory
                        && projection.Aggregation.Folders.TryGetValue(
                            entry.Reference,
                            out var folder
                        )
                    )
                        rows.Add(
                            entry with
                            {
                                Data = folder.Entries!.SubtreeData,
                                CapacityIncomplete =
                                    !folder.DirectoryCapacityObserved
                                    || folder.SubtreeUnobservedDirectoryCount != 0
                                    || folder.Entries.SubtreeUnresolvedCount != 0,
                            }
                        );
                    else
                        rows.Add(entry);
                }
                if (!source.HasMore)
                    break;
            }
            token.ThrowIfCancellationRequested();
            cachedVisibleEntries = rows.ToArray();
            cachedVisibleParent = parent;
            cachedVisibleOrder = null;
        }
        if (cachedVisibleOrder != (directoriesOnly, sort, descending))
        {
            var grouped = cachedVisibleEntries
                .Where(e => !directoriesOnly || e.IsDirectory && e.CanBrowse)
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Data is null);
            Func<FolderEntry, UInt128?> key = e =>
                sort == FolderEntrySort.AllocatedBytes
                    ? e.Data?.AllocatedBytes
                    : e.Data?.LogicalBytes;
            var sorted = descending ? grouped.ThenByDescending(key) : grouped.ThenBy(key);
            var ordered = sorted
                .ThenBy(e => e.Reference.RecordIndex)
                .ThenBy(e => e.NameOrdinal)
                .ToArray();
            token.ThrowIfCancellationRequested();
            orderedVisibleEntries = ordered;
            cachedVisibleOrder = (directoriesOnly, sort, descending);
        }
        var rowsPage = orderedVisibleEntries.Skip(checked(page * 250)).Take(251).ToArray();
        return new(
            [.. rowsPage.Take(250)],
            rowsPage.Length > 250,
            includeTotalCount ? orderedVisibleEntries.LongLength : null
        );
    }

    private RankedFolder[] ReadVisibleAnalysis(
        FolderRankingFilter filter,
        CancellationToken token,
        FolderEntrySort sort
    )
    {
        if (filter.Depth is < 1)
            throw new ArgumentOutOfRangeException(nameof(filter));
        // Read full paths using the same validated hierarchy as the unfiltered browser.
        var rows = store
            .ReadFolderAnalysis(ScanId, new(), token, sort)
            .Where(r => !visibleScan!.ExcludedFolders.Contains(r.Sample.Reference))
            .Select(r => r with { Sample = visibleScan!.Aggregation.Folders[r.Sample.Reference] })
            .ToArray();
        var graph = visibleScan!.Graph;
        var parents = filter.LeafOnly
            ? graph
                .Nodes.Values.Where(n =>
                    n.Depth is not null && n.Parent is not null && n.Parent != n.Reference
                )
                .Select(n => n.Parent!.Value)
                .ToHashSet()
            : [];
        return rows.Where(r =>
                (!filter.LeafOnly || !parents.Contains(r.Sample.Reference))
                && (filter.Depth is null || graph.Nodes[r.Sample.Reference].Depth == filter.Depth)
            )
            .OrderByDescending(r =>
                sort == FolderEntrySort.AllocatedBytes ? r.Data.AllocatedBytes : r.Data.LogicalBytes
            )
            .ThenBy(r => r.Sample.Reference.RecordIndex)
            .ToArray();
    }

    /// <summary>Recalculate both endpoints with the same read-time policy. Missing facts stay unknown.</summary>
    public static async Task<FolderChangeReport> ApplyVisibilityToComparisonAsync(
        FolderChangeReport report,
        ScanSession before,
        ScanSession after,
        FileVisibilityOptions options,
        CancellationToken token
    )
    {
        await before.SetVisibilityAsync(options, token).ConfigureAwait(false);
        await after.SetVisibilityAsync(options, token).ConfigureAwait(false);
        if (options.IncludesAll)
            return report;
        var old = await Read(before).ConfigureAwait(false);
        var current = await Read(after).ConfigureAwait(false);
        var rows = old
            .Keys.Union(current.Keys)
            .Select(reference =>
            {
                token.ThrowIfCancellationRequested();
                old.TryGetValue(reference, out var left);
                current.TryGetValue(reference, out var right);
                var status =
                    left is null ? FolderChangeStatus.NotObservedBefore
                    : right is null ? FolderChangeStatus.NotObservedAfter
                    : left.Bytes is null || right.Bytes is null
                        ? FolderChangeStatus.UncertainCapacity
                    : FolderChangeStatus.Comparable;
                return new FolderChange(
                    reference,
                    left?.Path,
                    right?.Path,
                    left?.Bytes,
                    right?.Bytes,
                    status == FolderChangeStatus.Comparable
                        ? BigInteger.CreateChecked(right!.Bytes!.Value)
                            - BigInteger.CreateChecked(left!.Bytes!.Value)
                        : null,
                    status
                );
            })
            .ToArray();
        return report with
        {
            Folders = [.. rows.Where(r => r.Reference.RecordIndex != 5)],
            Root = rows.FirstOrDefault(r => r.Reference == after.Root),
        };

        async Task<Dictionary<FileReference, ComparisonValue>> Read(ScanSession source)
        {
            var folders = await source.ReadFolderAnalysisAsync(new(), token).ConfigureAwait(false);
            var aggregation = source.visibleScan!.Aggregation;
            bool unknown =
                aggregation.HasUnknownAttribution || aggregation.Unattributed.UnresolvedCount != 0;
            UInt128? Bytes(FolderCapacitySample sample) =>
                !unknown
                && sample.DirectoryCapacityObserved
                && sample.SubtreeUnobservedDirectoryCount == 0
                && sample.Entries!.SubtreeUnresolvedCount == 0
                    ? sample.Entries.SubtreeData.AllocatedBytes
                    : null;
            var result = folders.ToDictionary(
                r => r.Sample.Reference,
                r => new ComparisonValue(r.RelativePath, Bytes(r.Sample))
            );
            result.Add(source.Root, new("", Bytes(source.RootSample)));
            return result;
        }
    }

    private sealed record ComparisonValue(string Path, UInt128? Bytes);
}
