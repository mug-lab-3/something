using System.Numerics;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Application;

public sealed partial class ScanSession
{
    private FileVisibilityOptions visibility = new();
    private VisibleScan? visibleScan;
    private readonly Dictionary<FileVisibilityOptions, VisibleScan> visibilityCache = [];
    private readonly Dictionary<FileVisibilityOptions, RankedFolder[]> analysisCache = [];
    private FolderVisibilityObservation[]? visibilityFolders;
    private FolderGraph? sourceFolderGraph;
    private readonly Dictionary<RootPageKey, FolderEntryPage> rootPageCache = [];

    private readonly record struct RootPageKey(
        FileVisibilityOptions Options,
        bool DirectoriesOnly,
        FolderEntrySort Sort,
        bool IncludeTotalCount,
        bool Descending
    );

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
            VisibleScan? projection = null;
            if (!options.IncludesAll && !visibilityCache.TryGetValue(options, out projection))
            {
                projection = await Task.Run(
                    () =>
                        new VisibleScan(
                            Root,
                            () => store.ReadFiles(ScanId, token),
                            options,
                            token,
                            ReadVisibilityFolders(token)
                        ),
                    token
                );
                token.ThrowIfCancellationRequested();
                visibilityCache.Add(options, projection);
            }
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
            foreach (var entry in store.ReadAllFolderEntries(ScanId, parent, token))
            {
                token.ThrowIfCancellationRequested();
                if (
                    !visibility.Includes(entry.StandardFileAttributes)
                    || projection.ExcludedFolders.Contains(entry.Reference)
                )
                    continue;
                if (
                    entry.IsDirectory
                    && projection.Aggregation.Folders.TryGetValue(entry.Reference, out var folder)
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

    private FolderVisibilityObservation[] ReadVisibilityFolders(CancellationToken token)
    {
        if (visibilityFolders is { } cached)
            return cached;
        var folders = store.ReadVisibilityFolders(ScanId, token).ToArray();
        token.ThrowIfCancellationRequested();
        return visibilityFolders = folders;
    }

    private RankedFolder[] ReadAnalysis(
        FolderRankingFilter filter,
        CancellationToken token,
        FolderEntrySort sort
    )
    {
        if (filter.Depth is < 1)
            throw new ArgumentOutOfRangeException(nameof(filter));
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        token.ThrowIfCancellationRequested();
        if (!analysisCache.TryGetValue(visibility, out var rows))
        {
            var all = new FileVisibilityOptions();
            if (!analysisCache.TryGetValue(all, out var source))
            {
                source = store.ReadFolderAnalysis(ScanId, new(), token).ToArray();
                token.ThrowIfCancellationRequested();
                analysisCache.Add(all, source);
            }
            if (visibleScan is { } projection)
            {
                rows = source
                    .Where(r => !projection.ExcludedFolders.Contains(r.Sample.Reference))
                    .Select(r =>
                    {
                        token.ThrowIfCancellationRequested();
                        return r with
                        {
                            Sample = projection.Aggregation.Folders[r.Sample.Reference],
                        };
                    })
                    .ToArray();
                token.ThrowIfCancellationRequested();
                analysisCache.Add(visibility, rows);
            }
            else
                rows = source;
        }
        // Depth and leaf membership use validated identities, never split display paths.
        var graph =
            filter.LeafOnly || filter.Depth is not null
                ? visibleScan?.Graph
                    ?? (
                        sourceFolderGraph ??= FolderGraph.Build(
                            Root,
                            ReadVisibilityFolders(token)
                                .Select(f => new FolderObservation(f.Reference, f.Parents)),
                            token
                        )
                    )
                : null;
        var parents = filter.LeafOnly
            ? rows.Select(r => graph!.Nodes[r.Sample.Reference].Parent!.Value).ToHashSet()
            : [];
        var result = rows.Where(r =>
                (!filter.LeafOnly || !parents.Contains(r.Sample.Reference))
                && (filter.Depth is null || graph!.Nodes[r.Sample.Reference].Depth == filter.Depth)
            )
            .OrderByDescending(r =>
                sort == FolderEntrySort.AllocatedBytes ? r.Data.AllocatedBytes : r.Data.LogicalBytes
            )
            .ThenBy(r => r.Sample.Reference.RecordIndex)
            .ToArray();
        token.ThrowIfCancellationRequested();
        return result;
    }

    private void ClearDisplayCaches()
    {
        visibilityCache.Clear();
        analysisCache.Clear();
        rootPageCache.Clear();
        visibleScan = null;
        visibilityFolders = null;
        sourceFolderGraph = null;
        cachedVisibleParent = null;
        cachedVisibleEntries = [];
        cachedVisibleOrder = null;
        orderedVisibleEntries = [];
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
