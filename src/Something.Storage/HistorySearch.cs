using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;

namespace Something.Storage;

public sealed partial class ScanHistoryArchive
{
    /// <summary>Searches the retained scans of one volume completed within the range. The result
    /// leases every scan of the range until it is disposed. Memory grows only through
    /// <paramref name="reserveMemoryGrowth"/>; spilling to disk remains the last resort.</summary>
    public HistorySearchResult Search(
        string volumeKey,
        HistorySearchCriteria criteria,
        CancellationToken token = default,
        ulong? memoryBytes = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        ArgumentNullException.ThrowIfNull(criteria);
        criteria.Validate();
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (!HasDatabaseFiles)
                return Unsearched(HistorySearchStatus.NoScans, []);
            using var store = OpenDatabase(token, memoryBytes);
            long start = criteria.StartUtc.UtcTicks,
                end = criteria.EndUtc.UtcTicks;
            var scans = store
                .HistoryContext.HistorySnapshots.AsNoTracking()
                .Where(row =>
                    row.VolumeKey == volumeKey
                    && !row.Retired
                    && row.CompletedUtcTicks >= start
                    && row.CompletedUtcTicks <= end
                )
                .OrderBy(row => row.CompletedUtcTicks)
                .ThenBy(row => row.Id)
                .AsEnumerable()
                .Select(ToEntry)
                .ToImmutableArray();
            if (scans.IsEmpty)
                return Unsearched(HistorySearchStatus.NoScans, scans);
            if (scans.Length == 1 && criteria.UsesChanges)
                return Unsearched(HistorySearchStatus.ChangesNeedTwoScans, scans);
            var release = AcquireLeases([.. scans.Select(scan => scan.Id)]);
            try
            {
                int[] generations = store.ReadGenerationNumbers([.. scans.Select(s => s.Id)]);
                var context = (DuckScanDbContext)store.HistoryContext;
                var tree = DuckMemoryRecovery.Run(
                    context,
                    _ =>
                    {
                        using var transaction = context.Database.BeginTransaction();
                        var result = new HistorySearchEngine(
                            store,
                            criteria,
                            generations,
                            token
                        ).Run();
                        transaction.Commit();
                        return result;
                    },
                    reserveMemoryGrowth,
                    null,
                    token
                );
                return new(
                    volumeKey,
                    criteria,
                    HistorySearchStatus.Completed,
                    scans,
                    [.. generations],
                    tree,
                    release
                );
            }
            catch
            {
                release();
                throw;
            }
        }

        HistorySearchResult Unsearched(
            HistorySearchStatus status,
            ImmutableArray<ScanHistoryEntry> scans
        ) => new(volumeKey, criteria, status, scans, [], new(), () => { });
    }

    /// <summary>Sizes of one search item in every scan of its search, oldest first.
    /// A scan without the item reports NotObserved; it is never a zero size.</summary>
    public ImmutableArray<SearchSizeObservation> ReadSearchSeries(
        HistorySearchResult result,
        SearchItem item,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(item);
        if (result.Status != HistorySearchStatus.Completed)
            throw new InvalidOperationException("The search did not complete.");
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            using var transaction = store.HistoryContext.Database.BeginTransaction();
            var generations = result.Generations;
            var observations = new SearchSizeObservation?[generations.Length];
            var allocated = Rows(SearchSizeKind.Allocated);
            var logical = Rows(SearchSizeKind.Logical);
            for (int index = 0; index < generations.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (allocated.TryGetValue(index, out var a))
                {
                    logical.TryGetValue(index, out var l);
                    observations[index] = new(
                        index,
                        a.Parent,
                        a.Name,
                        a.Size is null || l.Size is null
                            ? CapacityObservationState.Unresolved
                            : CapacityObservationState.Known,
                        a.Size,
                        l.Size
                    );
                }
                else
                    observations[index] = new(
                        index,
                        null,
                        null,
                        CapacityObservationState.NotObserved,
                        null,
                        null
                    );
            }
            transaction.Commit();
            return [.. observations.Select(o => o!)];

            Dictionary<int, SearchSeriesRow> Rows(SearchSizeKind kind)
            {
                var rows = new Dictionary<int, SearchSeriesRow>();
                foreach (
                    var row in store.ReadSearchSeries(generations, kind, [item.Reference], token)
                )
                    if (HistorySearchEngine.SameItem(item, row))
                        rows.TryAdd(row.ScanIndex, row);
                return rows;
            }
        }
    }

    private Action AcquireLeases(IReadOnlyList<Guid> ids)
    {
        var held = new List<(Guid Id, FileStream Lease)>();
        try
        {
            foreach (var id in ids)
            {
                var lease = new FileStream(
                    LeasePath(id),
                    FileMode.OpenOrCreate,
                    FileAccess.Read,
                    FileShare.Read
                );
                held.Add((id, lease));
                readers[id] = readers.GetValueOrDefault(id) + 1;
            }
        }
        catch
        {
            Release();
            throw;
        }
        return Release;

        void Release()
        {
            lock (sync)
            {
                foreach (var (id, lease) in held)
                {
                    lease.Dispose();
                    if (--readers[id] == 0)
                        readers.Remove(id);
                }
                held.Clear();
            }
        }
    }
}

internal sealed class HistorySearchEngine(
    ScanStore store,
    HistorySearchCriteria criteria,
    IReadOnlyList<int> generations,
    CancellationToken token
)
{
    private sealed class Candidate(SearchItem item, bool merged)
    {
        internal SearchItem Item { get; set; } = item;
        internal bool Merged { get; } = merged;
        internal bool Uncertain { get; set; }
        internal bool UnknownAttributes { get; set; }
    }

    private readonly record struct FolderInfo(bool Valid, int Depth, string? Path, bool Excluded);

    private readonly Regex? regex =
        criteria.NameMode == NameMatchMode.RegularExpression
        && !string.IsNullOrEmpty(criteria.NamePattern)
            ? HistorySearchCriteria.CreateRegex(criteria.NamePattern)
            : null;
    private readonly string? baseFolder = string.IsNullOrEmpty(criteria.BaseFolder?.Trim('\\'))
        ? null
        : criteria.BaseFolder.Trim('\\') + '\\';
    private readonly bool applyVisibility =
        !criteria.Attributes.IsSpecified && !criteria.Visibility.IncludesAll;
    private readonly Dictionary<FileReference, SearchItem> folderSummaries = [];
    private readonly Dictionary<FileReference, SearchFolderLink> placement = [];
    private FileReference? root;
    private int Last => generations.Count - 1;

    internal SearchTree Run()
    {
        var candidates = new List<Candidate>();
        var group = new List<SearchSummaryRow>();
        foreach (var row in store.ReadSearchSummaries(generations, criteria.SizeKind, token))
        {
            if (group.Count != 0 && group[0].Reference != row.Reference)
                Consume();
            group.Add(row);
        }
        if (group.Count != 0)
            Consume();

        var passed = new List<Candidate>();
        long unknownAttributes = 0;
        foreach (
            var byScan in candidates.GroupBy(c => c.Item.LastObserved).OrderByDescending(g => g.Key)
        )
        {
            token.ThrowIfCancellationRequested();
            var links = store.ReadSearchFolderLinks(generations[byScan.Key], token);
            // One scan at a time: a stream set of every scan could hold every file many times.
            var namedStreams =
                criteria.Attributes.NamedStreams == AttributeRequirement.Any
                    ? null
                    : store.ReadSearchNamedStreams(generations[byScan.Key], token);
            var memo = new Dictionary<FileReference, FolderInfo>();
            foreach (var candidate in byScan)
            {
                token.ThrowIfCancellationRequested();
                var item = candidate.Item;
                var parent = Resolve(item.Parent, links, memo);
                if (!parent.Valid || !PathMatches(item, parent))
                    continue;
                if (namedStreams is not null)
                {
                    // An unresolved file has no recorded streams, so its streams are unknown.
                    if (!item.IsDirectory && item.Size is null)
                        candidate.UnknownAttributes = true;
                    else if (
                        !Requirement(
                            criteria.Attributes.NamedStreams,
                            namedStreams.Contains(item.Reference.RecordIndex)
                        )
                    )
                        continue;
                }
                if (
                    applyVisibility
                    && (
                        parent.Excluded
                        || !criteria.Visibility.Includes(item.StandardFileAttributes)
                    )
                )
                    continue;
                if (candidate.UnknownAttributes)
                {
                    unknownAttributes++;
                    continue;
                }
                Record(item.IsDirectory ? item.Reference : item.Parent, links, item);
                passed.Add(candidate);
            }
        }
        if (criteria.Trend != SizeTrend.Any)
            passed = ApplyTrend(passed);

        var tree = new SearchTree { Root = root ?? FileReference.Create(5, 5) };
        tree.UnknownAttributeCount = unknownAttributes;
        foreach (var candidate in passed)
        {
            token.ThrowIfCancellationRequested();
            var item = candidate.Item with
            {
                Matched = true,
                Uncertain = candidate.Uncertain,
                FollowsIdentity = candidate.Merged,
            };
            if (candidate.Uncertain)
                tree.UncertainCount++;
            if (item.IsDirectory)
            {
                Ensure(tree, item.Reference);
                var node = tree.Folders[item.Reference];
                tree.Folders[item.Reference] = item with { Parent = node.Parent, Name = node.Name };
                tree.MatchedFolders++;
            }
            else
            {
                Ensure(tree, item.Parent);
                if (!tree.Files.TryGetValue(item.Parent, out var files))
                    tree.Files.Add(item.Parent, files = []);
                files.Add(item);
                tree.MatchedFiles++;
            }
        }
        return tree;

        void Consume()
        {
            token.ThrowIfCancellationRequested();
            bool disjoint = true;
            for (int i = 1; i < group.Count; i++)
                disjoint &= group[i].FirstObserved > group[i - 1].LastObserved;
            // A single visible name that moved or was renamed is one item. Names whose scans
            // overlap are hard links: only the same parent and name continue an item.
            var items = disjoint
                ? [(Merge(group), true)]
                : group.Select(row => (ToItem(row), false)).ToArray();
            foreach (var (item, merged) in items)
            {
                if (
                    item.IsDirectory
                    && (
                        !folderSummaries.TryGetValue(item.Reference, out var known)
                        || known.LastObserved < item.LastObserved
                    )
                )
                    folderSummaries[item.Reference] = item;
                if (Evaluate(item, merged) is { } candidate)
                    candidates.Add(candidate);
            }
            group.Clear();
        }
    }

    // An item that follows its identity includes every name it had; a hard link name continues
    // only with the same parent and name.
    internal static bool SameItem(SearchItem item, SearchSeriesRow row) =>
        row.Reference == item.Reference
        && (
            item.IsDirectory
            || item.FollowsIdentity
            || row.Parent == item.Parent && row.Name == item.Name
        );

    private static SearchItem ToItem(SearchSummaryRow row) =>
        new(
            row.Reference,
            row.Parent,
            row.Name,
            row.IsDirectory,
            row.Attributes,
            row.FirstObserved,
            row.LastObserved,
            row.ObservedCount,
            row.FirstObserved == 0 ? row.FirstSize : null,
            row.LastSize,
            row.MinimumSize,
            row.MaximumSize,
            row.HasUnresolvedSize
        );

    private static SearchItem Merge(List<SearchSummaryRow> rows)
    {
        var first = rows[0];
        var last = rows[^1];
        UInt128? minimum = null,
            maximum = null;
        foreach (var row in rows)
        {
            if (row.MinimumSize is { } low && (minimum is null || low < minimum))
                minimum = low;
            if (row.MaximumSize is { } high && (maximum is null || high > maximum))
                maximum = high;
        }
        return new(
            first.Reference,
            last.Parent,
            last.Name,
            last.IsDirectory,
            last.Attributes,
            first.FirstObserved,
            last.LastObserved,
            rows.Sum(row => row.ObservedCount),
            first.FirstObserved == 0 ? first.FirstSize : null,
            last.LastSize,
            minimum,
            maximum,
            rows.Any(row => row.HasUnresolvedSize)
        );
    }

    // Conditions decided by the aggregated summary. Paths and named streams need the folders of
    // the item's last scan and are decided in Run.
    private Candidate? Evaluate(SearchItem item, bool merged)
    {
        if (
            criteria.Target == SearchTarget.Folders && !item.IsDirectory
            || criteria.Target == SearchTarget.Files && item.IsDirectory
            || !NameMatches(item.Name)
            || !criteria.Extensions.IsEmpty && (item.IsDirectory || !HasExtension(item.Name))
            || !PresenceMatches(item)
        )
            return null;
        var candidate = new Candidate(item, merged);
        var attributes = criteria.Attributes;
        if (attributes.UsesStandardAttributes)
        {
            if (item.StandardFileAttributes is not { } value)
                candidate.UnknownAttributes = true;
            else if (
                !Requirement(attributes.Hidden, (value & SearchAttributes.HiddenBit) != 0)
                || !Requirement(attributes.System, (value & SearchAttributes.SystemBit) != 0)
                || !Requirement(
                    attributes.Compressed,
                    (value & SearchAttributes.CompressedBit) != 0
                )
                || !Requirement(attributes.Sparse, (value & SearchAttributes.SparseBit) != 0)
            )
                return null;
        }
        if (criteria.MinimumSize is not null || criteria.MaximumSize is not null)
        {
            if (item.Size is not { } size)
                candidate.Uncertain = true;
            else if (size < criteria.MinimumSize || size > criteria.MaximumSize)
                return null;
        }
        if (criteria.MinimumDelta is not null || criteria.MaximumDelta is not null)
        {
            if (!item.InFirstScan)
                return null;
            if (item.Delta is not { } delta)
                candidate.Uncertain = true;
            else if (delta < criteria.MinimumDelta || delta > criteria.MaximumDelta)
                return null;
        }
        if (criteria.MinimumDeltaPercent is not null || criteria.MaximumDeltaPercent is not null)
        {
            // A rate needs a known, nonzero size in the first scan of the range.
            if (!item.InFirstScan || item.FirstSize is not { } first || first == 0)
                return null;
            if (item.Size is not { } last)
                candidate.Uncertain = true;
            else
            {
                double percent =
                    (double)((BigInteger)last - (BigInteger)first) / (double)first * 100;
                if (
                    percent < criteria.MinimumDeltaPercent
                    || percent > criteria.MaximumDeltaPercent
                )
                    return null;
            }
        }
        if (criteria.MinimumSwing is { } swing)
        {
            // Known sizes give a lower bound of the swing; unresolved sizes could only widen it.
            bool reached =
                item.MaximumKnownSize is { } high
                && item.MinimumKnownSize is { } low
                && high - low >= swing;
            if (!reached)
            {
                if (!item.HasUnresolvedSize)
                    return null;
                candidate.Uncertain = true;
            }
        }
        return candidate;
    }

    private bool PresenceMatches(SearchItem item) =>
        criteria.Presence switch
        {
            SearchPresence.InAllScans => item.ObservedCount == generations.Count,
            SearchPresence.Appeared => !item.InFirstScan && item.LastObserved == Last,
            SearchPresence.Disappeared => item.InFirstScan && item.LastObserved < Last,
            _ => true,
        };

    private static bool Requirement(AttributeRequirement requirement, bool value) =>
        requirement switch
        {
            AttributeRequirement.Set => value,
            AttributeRequirement.Clear => !value,
            _ => true,
        };

    private bool NameMatches(string name) =>
        string.IsNullOrEmpty(criteria.NamePattern)
        || criteria.NameMode switch
        {
            NameMatchMode.Wildcard => FileSystemName.MatchesSimpleExpression(
                criteria.NamePattern,
                name,
                ignoreCase: true
            ),
            NameMatchMode.RegularExpression => regex!.IsMatch(name),
            _ => name.Contains(criteria.NamePattern, StringComparison.OrdinalIgnoreCase),
        };

    private bool HasExtension(string name) =>
        criteria.Extensions.Any(extension =>
        {
            string suffix = "." + extension.TrimStart('.');
            return name.Length > suffix.Length
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        });

    private bool PathMatches(SearchItem item, FolderInfo parent)
    {
        int depth = parent.Depth + 1;
        if (depth < criteria.MinimumDepth || depth > criteria.MaximumDepth)
            return false;
        if (baseFolder is null)
            return true;
        string path = parent.Path!.Length == 0 ? item.Name : parent.Path + '\\' + item.Name;
        return path.Length > baseFolder.Length
            && path.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase);
    }

    // Depth, display path and visibility of a folder in one scan. A folder whose parents do not
    // reach the root in that scan is invalid; its items are not placed anywhere.
    private FolderInfo Resolve(
        FileReference folder,
        Dictionary<FileReference, SearchFolderLink> links,
        Dictionary<FileReference, FolderInfo> memo
    )
    {
        var pending = new Stack<FileReference>();
        var current = folder;
        FolderInfo known;
        while (true)
        {
            if (current.RecordIndex == 5)
            {
                root ??= current;
                known = new(true, 0, baseFolder is null ? null : "", false);
                break;
            }
            if (memo.TryGetValue(current, out known))
                break;
            if (!links.ContainsKey(current) || pending.Contains(current) || pending.Count > 4096)
            {
                known = new(false, 0, null, false);
                break;
            }
            pending.Push(current);
            current = links[current].Parent;
        }
        while (pending.TryPop(out var child))
        {
            if (known.Valid)
            {
                var link = links[child];
                bool excluded =
                    known.Excluded
                    || applyVisibility
                        && folderSummaries.TryGetValue(child, out var summary)
                        && !criteria.Visibility.Includes(summary.StandardFileAttributes);
                known = new(
                    true,
                    known.Depth + 1,
                    known.Path is null ? null
                        : known.Path.Length == 0 ? link.Name
                        : known.Path + '\\' + link.Name,
                    excluded
                );
            }
            memo[child] = known;
        }
        return known;
    }

    // Keeps the first observed location of each folder. Scans are visited newest first, so a
    // folder is placed where it was last seen; later placements stop at an existing folder.
    private void Record(
        FileReference folder,
        Dictionary<FileReference, SearchFolderLink> links,
        SearchItem item
    )
    {
        for (
            var current = folder;
            current.RecordIndex != 5 && !placement.ContainsKey(current);
            current = placement[current].Parent
        )
            placement[current] =
                current == item.Reference ? new(item.Parent, item.Name) : links[current];
    }

    private void Ensure(SearchTree tree, FileReference folder)
    {
        for (
            var current = folder;
            current.RecordIndex != 5 && !tree.Folders.ContainsKey(current);
            current = placement[current].Parent
        )
        {
            var link = placement[current];
            tree.Folders[current] = (
                folderSummaries.GetValueOrDefault(current)
                ?? new(
                    current,
                    link.Parent,
                    link.Name,
                    true,
                    null,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    true
                )
            ) with
            {
                Parent = link.Parent,
                Name = link.Name,
                Matched = false,
                Uncertain = false,
            };
            if (!tree.ChildFolders.TryGetValue(link.Parent, out var children))
                tree.ChildFolders.Add(link.Parent, children = []);
            children.Add(current);
        }
    }

    private List<Candidate> ApplyTrend(List<Candidate> passed)
    {
        var byReference = passed
            .GroupBy(c => c.Item.Reference)
            .ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<Candidate>();
        var series = new List<SearchSeriesRow>();
        foreach (
            var row in store.ReadSearchSeries(
                generations,
                criteria.SizeKind,
                byReference.Keys,
                token
            )
        )
        {
            if (series.Count != 0 && series[0].Reference != row.Reference)
                Decide();
            series.Add(row);
        }
        if (series.Count != 0)
            Decide();
        return result;

        void Decide()
        {
            token.ThrowIfCancellationRequested();
            foreach (var candidate in byReference[series[0].Reference])
            {
                var item = candidate.Item with { FollowsIdentity = candidate.Merged };
                bool increased = false,
                    decreased = false,
                    unknown = false,
                    unknownAtEdge = false;
                UInt128? previous = null;
                foreach (var row in series.Where(row => SameItem(item, row)))
                {
                    if (row.Size is not { } size)
                    {
                        unknown = true;
                        // Before the first known size; one after the last is detected below.
                        unknownAtEdge |= previous is null;
                        continue;
                    }
                    if (previous is { } before)
                    {
                        increased |= size > before;
                        decreased |= size < before;
                    }
                    previous = size;
                }
                unknownAtEdge |= series.LastOrDefault(row => SameItem(item, row)).Size is null;
                bool wanted = criteria.Trend == SizeTrend.Increasing;
                bool contrary = wanted ? decreased : increased;
                bool confirmed = wanted ? increased : decreased;
                // Known sizes in the wrong direction rule a trend out. Without a known step in the
                // wanted direction, an unknown size can only supply one before or after all known
                // sizes; between equal known sizes it would also need a step back.
                if (contrary || !confirmed && !unknownAtEdge)
                    continue;
                if (unknown)
                    candidate.Uncertain = true;
                result.Add(candidate);
            }
            series.Clear();
        }
    }
}
