using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Something.Application.History;
using Something.Application.Search;
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
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
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
                using var threads = DuckDatabaseBootstrap.UseAggregationThreads(context);
                // No transaction: building the TEMP tables inside one made the aggregation 2.6
                // times slower (0.38 s to 1.0 s). The archive lock already orders publication and
                // pruning in this process, and the leases keep every scan of the range.
                var tree = DuckMemoryRecovery.Run(
                    context,
                    _ => new HistorySearchEngine(store, criteria, generations, token).Run(),
                    reserveMemoryGrowth,
                    null,
                    token,
                    accountMemoryLimit
                );
                return new StoredSearchResult(
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
        ) => new StoredSearchResult(volumeKey, criteria, status, scans, [], new(), () => { });
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
            var generations = (
                result as StoredSearchResult
                ?? throw new ArgumentException(
                    "The search belongs to another storage implementation.",
                    nameof(result)
                )
            ).Generations;
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

        /// <summary>The trend must be read per scan: folder sizes, or interleaved listings.</summary>
        internal bool NeedsTrend { get; set; }
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
        folderSizes = store.ReadSearchFolderSizes(generations, criteria.SizeKind, token);
        var candidates = new List<Candidate>();
        var group = new List<SearchSegmentRow>();
        string keep = Prefilter();
        foreach (var (first, last) in Partitions())
        {
            // Version rows of identities with several names arrive unordered; few identities
            // have them, so they are gathered here and folded per identity afterwards.
            var multiple = new Dictionary<FileReference, List<SearchSegmentRow>>();
            foreach (
                var row in store.ReadSearchIdentities(
                    generations,
                    criteria.SizeKind,
                    first,
                    last,
                    keep,
                    criteria.Trend != SizeTrend.Any,
                    token
                )
            )
            {
                if (row.Identity is { } identity)
                    Add(FromIdentity(identity), true, identity.Trend);
                else if (row.Segment is { } segment)
                {
                    if (!multiple.TryGetValue(segment.Reference, out var rows))
                        multiple.Add(segment.Reference, rows = []);
                    rows.Add(segment);
                }
            }
            foreach (var rows in multiple.Values)
            {
                group.AddRange(rows.OrderBy(row => row.FirstScan));
                Consume();
            }
        }

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
            passed = ApplyExactTrends(passed);

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
            // Rows arrive by first scan, so each name's rows stay in scan order.
            var names = new List<List<SearchSegmentRow>>();
            foreach (var row in group)
            {
                var name = names.Find(n => n[0].Parent == row.Parent && n[0].Name == row.Name);
                if (name is null)
                    names.Add(name = []);
                name.Add(row);
            }
            bool disjoint = true;
            for (int i = 1; i < names.Count; i++)
                disjoint &= names[i][0].FirstScan > names[i - 1].Max(row => row.LastScan);
            // A single visible name that moved or was renamed is one item. Names whose scans
            // overlap are hard links: only the same parent and name continue an item.
            var items = disjoint
                ? [(Fold(group), true)]
                : names.Select(rows => (Fold(rows), false)).ToArray();
            foreach (var ((item, trend), merged) in items)
                Add(item, merged, trend);
            group.Clear();
        }

        void Add(SearchItem item, bool merged, SearchTrendEvidence? trend)
        {
            if (
                item.IsDirectory
                && (
                    !folderSummaries.TryGetValue(item.Reference, out var known)
                    || known.LastObserved < item.LastObserved
                )
            )
                folderSummaries[item.Reference] = item;
            if (Evaluate(item, merged, trend) is { } candidate)
                candidates.Add(candidate);
        }
    }

    // A conservative SQL condition over the identity columns of ReadSearchIdentities. It only
    // drops rows that certainly fail; every kept row is still evaluated in full here, so unknown
    // values always pass and bounds that SQL cannot represent are left out.
    private string Prefilter()
    {
        var parts = new List<string>();
        int last = Last;
        parts.Add(
            criteria.Target switch
            {
                SearchTarget.Folders => "D",
                SearchTarget.Files => "NOT D",
                _ => "TRUE",
            }
        );
        parts.Add(
            criteria.Presence switch
            {
                SearchPresence.InAllScans => $"N={generations.Count}",
                SearchPresence.Appeared => $"F>0 AND L={last}",
                SearchPresence.Disappeared => $"F=0 AND L<{last}",
                _ => "TRUE",
            }
        );
        var attributes = criteria.Attributes;
        foreach (
            var (requirement, bit) in new[]
            {
                (attributes.Hidden, (uint)FileAttributes.Hidden),
                (attributes.System, (uint)FileAttributes.System),
                (attributes.Compressed, (uint)FileAttributes.Compressed),
                (attributes.Sparse, (uint)FileAttributes.SparseFile),
            }
        )
            if (requirement != AttributeRequirement.Any)
                parts.Add(
                    $"(A IS NULL OR (A&{bit}){(requirement == AttributeRequirement.Set ? "<>" : "=")}0)"
                );
        // File sizes are 64-bit signed in storage; folders are decided from their samples.
        if (criteria.MinimumSize is not null || criteria.MaximumSize is not null)
            parts.Add(
                $"(D OR SzL IS NULL OR ({Lower("SzL", criteria.MinimumSize)} AND {Upper("SzL", criteria.MaximumSize)}))"
            );
        if (criteria.MinimumDelta is not null || criteria.MaximumDelta is not null)
            parts.Add(
                $"(D OR (F=0 AND (F0 IS NULL OR SzL IS NULL OR ({Lower("SzL-F0", criteria.MinimumDelta)} AND {Upper("SzL-F0", criteria.MaximumDelta)}))))"
            );
        if (criteria.MinimumDeltaPercent is not null || criteria.MaximumDeltaPercent is not null)
        {
            // Widened slightly, so SQL rounding never drops a row that .NET would keep.
            string rate = "(SzL-F0)*100.0/F0";
            string low = criteria.MinimumDeltaPercent is { } minimum
                ? $"{rate}>={Widen(minimum, -1)}"
                : "TRUE";
            string high = criteria.MaximumDeltaPercent is { } maximum
                ? $"{rate}<={Widen(maximum, 1)}"
                : "TRUE";
            parts.Add($"(D OR (F=0 AND F0>0 AND (SzL IS NULL OR ({low} AND {high}))))");
        }
        if (criteria.MinimumSwing is { } swing)
            parts.Add($"(D OR U OR {Lower("Mx-Mn", (UInt128?)swing)})");
        if (criteria.Trend != SizeTrend.Any)
        {
            bool increasing = criteria.Trend == SizeTrend.Increasing;
            string contrary = increasing ? "Dec" : "Inc",
                confirmed = increasing ? "Inc" : "Dec";
            parts.Add($"(D OR NOT Cons OR (NOT {contrary} AND ({confirmed} OR Edge)))");
        }
        return string.Join(" AND ", parts);

        // Compared as BigInteger: truncating long.MinValue into an unsigned type would wrap.
        static string Lower<T>(string column, T? bound)
            where T : struct, INumber<T> =>
            bound is not { } value ? "TRUE"
            : BigInteger.CreateChecked(value) > long.MaxValue ? "FALSE"
            : BigInteger.CreateChecked(value) < long.MinValue ? "TRUE"
            : $"{column}>={long.CreateChecked(value)}";

        static string Upper<T>(string column, T? bound)
            where T : struct, INumber<T> =>
            bound is not { } value ? "TRUE"
            : BigInteger.CreateChecked(value) > long.MaxValue ? "TRUE"
            : BigInteger.CreateChecked(value) < long.MinValue ? "FALSE"
            : $"{column}<={long.CreateChecked(value)}";

        static string Widen(double value, int direction) =>
            (value + direction * (Math.Abs(value) * 1e-9 + 1e-9)).ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture
            );
    }

    // The single visible name of an identity, aggregated in SQL.
    private SearchItem FromIdentity(SearchIdentityRow row) =>
        row.IsDirectory
            ? Folder(
                row.Reference,
                row.Parent,
                row.Name!,
                row.Attributes,
                row.FirstScan,
                row.LastScan,
                row.ScanCount
            )
            : new(
                row.Reference,
                row.Parent,
                row.Name!,
                false,
                row.Attributes,
                row.FirstScan,
                row.LastScan,
                row.ScanCount,
                row.FirstScan == 0 ? row.FirstSize : null,
                row.LastSize,
                row.MinimumSize,
                row.MaximumSize,
                row.HasUnresolvedSize
            );

    // Folder sizes come from the folder samples of the identity, not from its listing rows.
    private SearchItem Folder(
        FileReference reference,
        FileReference parent,
        string name,
        uint? attributes,
        int firstScan,
        int lastScan,
        int observed
    )
    {
        bool found = folderSizes.TryGetValue(reference, out var sizes);
        return new(
            reference,
            parent,
            name,
            true,
            attributes,
            firstScan,
            lastScan,
            observed,
            found && sizes.FirstScan == 0 && firstScan == 0 ? sizes.FirstSize : null,
            found && sizes.LastScan == lastScan ? sizes.LastSize : null,
            found ? sizes.MinimumSize : null,
            found ? sizes.MaximumSize : null,
            !found || sizes.HasUnresolvedSize || sizes.ScanCount < observed
        );
    }

    private Dictionary<FileReference, SearchFolderSizes> folderSizes = [];

    // Record ranges whose listing rows fit the engine's memory for one sort. One identity never
    // spans two ranges, so its names are always folded together.
    private IEnumerable<(long First, long Last)> Partitions()
    {
        ulong limit = DuckDatabaseBootstrap.ReadMemoryLimit(
            (DuckScanDbContext)store.HistoryContext
        );
        // About 128 bytes per sorted row, with half of the limit left to the engine.
        long perPartition = (long)Math.Max(1UL << 16, limit / 2 / 128);
        if (store.ReadSearchEntryCount() <= perPartition)
        {
            yield return (0, long.MaxValue);
            yield break;
        }
        var (rows, maximum) = store.ReadSearchSegmentBounds(generations);
        long count = Math.Max(1, (rows + perPartition - 1) / perPartition);
        long width = maximum / count + 1;
        for (long first = 0; first <= maximum; first += width)
            yield return (first, Math.Min(maximum, first + width - 1));
    }

    // Folds version rows of one item, oldest first. File sizes come from the rows; folder sizes
    // from the folder samples of the identity. A trend is decided here when the rows' scans are
    // consecutive and do not interleave; otherwise it is read per scan later.
    private (SearchItem Item, SearchTrendEvidence? Trend) Fold(List<SearchSegmentRow> rows)
    {
        var sorted = rows.Count == 1 ? rows : [.. rows.OrderBy(row => row.FirstScan)];
        var first = sorted[0];
        var last = sorted.MaxBy(row => row.LastScan);
        int observed = sorted.Sum(row => row.ScanCount);
        if (first.IsDirectory)
            return (
                Folder(
                    first.Reference,
                    last.Parent,
                    last.Name,
                    last.Attributes,
                    first.FirstScan,
                    last.LastScan,
                    observed
                ),
                null
            );
        UInt128? minimum = null,
            maximum = null;
        bool unknown = false,
            increased = false,
            decreased = false,
            consecutive = true;
        ulong? previous = null;
        for (int i = 0; i < sorted.Count; i++)
        {
            var row = sorted[i];
            consecutive &=
                row.ScanCount == row.LastScan - row.FirstScan + 1
                && (i == 0 || row.FirstScan > sorted[i - 1].LastScan);
            if (row.FileSize is not { } size)
            {
                unknown = true;
                continue;
            }
            if (minimum is null || size < minimum)
                minimum = size;
            if (maximum is null || size > maximum)
                maximum = size;
            if (previous is { } before)
            {
                increased |= size > before;
                decreased |= size < before;
            }
            previous = size;
        }
        bool unknownAtEdge = first.FileSize is null || sorted[^1].FileSize is null;
        return (
            new(
                first.Reference,
                last.Parent,
                last.Name,
                false,
                last.Attributes,
                first.FirstScan,
                last.LastScan,
                observed,
                first.FirstScan == 0 ? first.FileSize : null,
                last.FileSize,
                minimum,
                maximum,
                unknown
            ),
            consecutive ? new(increased, decreased, unknown, unknownAtEdge) : null
        );
    }

    private enum TrendOutcome
    {
        Fail,
        Pass,
        Uncertain,
    }

    // Known sizes in the wrong direction rule a trend out. Without a known step in the wanted
    // direction, an unknown size can only supply one before or after all known sizes; between
    // equal known sizes it would also need a step back.
    private TrendOutcome Decide(SearchTrendEvidence evidence)
    {
        bool wanted = criteria.Trend == SizeTrend.Increasing;
        bool contrary = wanted ? evidence.Decreased : evidence.Increased;
        bool confirmed = wanted ? evidence.Increased : evidence.Decreased;
        return contrary || !confirmed && !evidence.UnknownAtEdge ? TrendOutcome.Fail
            : evidence.Unknown ? TrendOutcome.Uncertain
            : TrendOutcome.Pass;
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

    // Conditions decided by the folded rows. Paths and named streams need the folders of the
    // item's last scan and are decided in Run; trends without evidence are read per scan later.
    private Candidate? Evaluate(SearchItem item, bool merged, SearchTrendEvidence? trend)
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
        if (criteria.Trend != SizeTrend.Any)
        {
            if (trend is not { } evidence)
                candidate.NeedsTrend = true;
            else if (Decide(evidence) is var outcome && outcome == TrendOutcome.Fail)
                return null;
            else
                candidate.Uncertain |= outcome == TrendOutcome.Uncertain;
        }
        var attributes = criteria.Attributes;
        if (attributes.UsesStandardAttributes)
        {
            if (item.StandardFileAttributes is not { } value)
                candidate.UnknownAttributes = true;
            else if (
                !Requirement(
                    attributes.Hidden,
                    ((FileAttributes)value).HasFlag(FileAttributes.Hidden)
                )
                || !Requirement(
                    attributes.System,
                    ((FileAttributes)value).HasFlag(FileAttributes.System)
                )
                || !Requirement(
                    attributes.Compressed,
                    ((FileAttributes)value).HasFlag(FileAttributes.Compressed)
                )
                || !Requirement(
                    attributes.Sparse,
                    ((FileAttributes)value).HasFlag(FileAttributes.SparseFile)
                )
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

    // Folders and items whose listings interleave are decided from per-scan sizes. Folder sizes
    // come from the samples alone; files need the per-scan listing rows of their identity.
    private List<Candidate> ApplyExactTrends(List<Candidate> passed)
    {
        var pending = passed.Where(c => c.NeedsTrend).ToList();
        if (pending.Count == 0)
            return passed;
        var rejected = new HashSet<Candidate>();
        var folders = pending.Where(c => c.Item.IsDirectory).ToDictionary(c => c.Item.Reference);
        var sizes = new List<UInt128?>();
        FileReference? current = null;
        foreach (
            var (reference, _, size) in store.ReadSearchFolderSeries(
                generations,
                criteria.SizeKind,
                folders.Keys,
                token
            )
        )
        {
            if (current is { } previous && previous != reference)
                Judge(folders[previous], sizes);
            current = reference;
            sizes.Add(size);
        }
        if (current is { } lastFolder)
            Judge(folders[lastFolder], sizes);

        var files = pending
            .Where(c => !c.Item.IsDirectory)
            .GroupBy(c => c.Item.Reference)
            .ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<SearchSeriesRow>();
        foreach (
            var row in store.ReadSearchSeries(generations, criteria.SizeKind, files.Keys, token)
        )
        {
            if (rows.Count != 0 && rows[0].Reference != row.Reference)
                JudgeFiles();
            rows.Add(row);
        }
        if (rows.Count != 0)
            JudgeFiles();
        return [.. passed.Where(c => !rejected.Contains(c))];

        void JudgeFiles()
        {
            foreach (var candidate in files[rows[0].Reference])
            {
                var item = candidate.Item with { FollowsIdentity = candidate.Merged };
                sizes.AddRange(rows.Where(row => SameItem(item, row)).Select(row => row.Size));
                Judge(candidate, sizes);
            }
            rows.Clear();
        }

        void Judge(Candidate candidate, List<UInt128?> series)
        {
            token.ThrowIfCancellationRequested();
            bool increased = false,
                decreased = false;
            UInt128? previous = null;
            foreach (var size in series)
                if (size is { } known)
                {
                    if (previous is { } before)
                    {
                        increased |= known > before;
                        decreased |= known < before;
                    }
                    previous = known;
                }
            var outcome =
                series.Count == 0
                    ? TrendOutcome.Fail
                    : Decide(
                        new(
                            increased,
                            decreased,
                            series.Any(size => size is null),
                            series[0] is null || series[^1] is null
                        )
                    );
            if (outcome == TrendOutcome.Fail)
                rejected.Add(candidate);
            else
                candidate.Uncertain |= outcome == TrendOutcome.Uncertain;
            series.Clear();
        }
    }
}

/// <summary>A search result with the generation numbers of its scans, for reading series later.</summary>
internal sealed class StoredSearchResult(
    string volumeKey,
    HistorySearchCriteria criteria,
    HistorySearchStatus status,
    ImmutableArray<ScanHistoryEntry> scans,
    ImmutableArray<int> generations,
    SearchTree tree,
    Action release
) : HistorySearchResult(volumeKey, criteria, status, scans, tree, release)
{
    public ImmutableArray<int> Generations { get; } = generations;
}
