using System.IO.Enumeration;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Something.Application.Search;

/// <summary>Whether conditions hold for an item. An unknown value never fails a condition by
/// itself; it leaves the item uncertain.</summary>
public enum ConditionOutcome
{
    Fail,
    Pass,
    Uncertain,
}

/// <summary>Steps between consecutive known sizes of one item, in scan order.</summary>
/// <param name="Increased">Some known size was larger than the known size before it.</param>
/// <param name="Decreased">Some known size was smaller than the known size before it.</param>
/// <param name="Unknown">Some size was unresolved.</param>
/// <param name="UnknownAtEdge">The first or the last size was unresolved.</param>
public readonly record struct SizeTrendEvidence(
    bool Increased,
    bool Decreased,
    bool Unknown,
    bool UnknownAtEdge
)
{
    /// <summary>The evidence of one size per scan, oldest first; null for an unresolved size.</summary>
    public static SizeTrendEvidence FromSeries(IReadOnlyList<UInt128?> sizes)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sizes.Count);
        bool increased = false,
            decreased = false;
        UInt128? previous = null;
        foreach (var size in sizes)
            if (size is { } known)
            {
                if (previous is { } before)
                {
                    increased |= known > before;
                    decreased |= known < before;
                }
                previous = known;
            }
        return new(
            increased,
            decreased,
            sizes.Any(size => size is null),
            sizes[0] is null || sizes[^1] is null
        );
    }
}

/// <summary>The conditions an item's own values decide; paths and named streams come later.</summary>
/// <param name="Matches">No condition failed.</param>
/// <param name="Uncertain">A condition could not be decided because a size was unresolved.</param>
/// <param name="NeedsTrend">The trend could not be decided from the evidence at hand; the
/// caller reads one size per scan and decides it with <see cref="SearchConditions.DecideTrend(IReadOnlyList{UInt128?})"/>.</param>
/// <param name="UnknownAttributes">An attribute condition could not be decided because the
/// attributes were not recorded; such items are counted, not shown.</param>
public readonly record struct SearchVerdict(
    bool Matches,
    bool Uncertain,
    bool NeedsTrend,
    bool UnknownAttributes
)
{
    public static SearchVerdict Fail => default;
}

/// <summary>The criteria of one search, prepared for a range of <paramref name="scanCount"/>
/// scans. Decides every condition of an item in .NET; a repository may drop rows earlier only
/// when they certainly fail.</summary>
public sealed class SearchConditions(HistorySearchCriteria criteria, int scanCount)
{
    private readonly Regex? regex =
        criteria.NameMode == NameMatchMode.RegularExpression
        && !string.IsNullOrEmpty(criteria.NamePattern)
            ? HistorySearchCriteria.CreateRegex(criteria.NamePattern)
            : null;

    // In a path, a backslash separates folders: it is not the escape of a wildcard, and a slash
    // reads as a backslash. A regular expression is the user's own.
    private readonly string? pathPattern = criteria.NameMode switch
    {
        NameMatchMode.Wildcard => criteria.NamePattern?.Replace('/', '\\').Replace("\\", "\\\\"),
        NameMatchMode.Contains => criteria.NamePattern?.Replace('/', '\\'),
        _ => criteria.NamePattern,
    };
    private readonly string? baseFolder = string.IsNullOrEmpty(criteria.BaseFolder?.Trim('\\'))
        ? null
        : criteria.BaseFolder.Trim('\\') + '\\';

    public HistorySearchCriteria Criteria { get; } = criteria;
    public int ScanCount { get; } = scanCount;
    private int Last => ScanCount - 1;

    // The name pattern applies to the path, which is only known with the item's folders.
    private bool NameOnPath =>
        Criteria.NameIncludesPath && !string.IsNullOrEmpty(Criteria.NamePattern);

    /// <summary>Whether display paths are needed: the base folder and a name on the path use them.</summary>
    public bool NeedsPaths => baseFolder is not null || NameOnPath;

    public bool NeedsNamedStreams => Criteria.Attributes.NamedStreams != AttributeRequirement.Any;

    /// <summary>The display settings apply only when no attribute condition is specified.</summary>
    public bool AppliesVisibility =>
        !Criteria.Attributes.IsSpecified && !Criteria.Visibility.IncludesAll;

    /// <summary>Whether an item or folder with <paramref name="attributes"/> is shown under the
    /// display settings; always true when they do not apply.</summary>
    public bool IsVisible(uint? attributes) =>
        !AppliesVisibility || Criteria.Visibility.Includes(attributes);

    /// <summary>Target, name, extension, presence, attributes, size, change, rate, swing and,
    /// when <paramref name="trend"/> is given, trend.</summary>
    public SearchVerdict Evaluate(SearchItem item, SizeTrendEvidence? trend)
    {
        if (
            Criteria.Target == SearchTarget.Folders && !item.IsDirectory
            || Criteria.Target == SearchTarget.Files && item.IsDirectory
            || !NameOnPath && !NameMatches(item.Name, Criteria.NamePattern)
            || !Criteria.Extensions.IsEmpty && (item.IsDirectory || !HasExtension(item.Name))
            || !PresenceMatches(item)
        )
            return SearchVerdict.Fail;
        bool uncertain = false,
            needsTrend = false,
            unknownAttributes = false;
        if (Criteria.Trend != SizeTrend.Any)
        {
            if (trend is not { } evidence)
                needsTrend = true;
            else if (DecideTrend(evidence) is var outcome && outcome == ConditionOutcome.Fail)
                return SearchVerdict.Fail;
            else
                uncertain |= outcome == ConditionOutcome.Uncertain;
        }
        var attributes = Criteria.Attributes;
        if (attributes.UsesStandardAttributes)
        {
            if (item.StandardFileAttributes is not { } value)
                unknownAttributes = true;
            else
            {
                var flags = (FileAttributes)value;
                if (
                    !Requirement(attributes.Hidden, flags.HasFlag(FileAttributes.Hidden))
                    || !Requirement(attributes.System, flags.HasFlag(FileAttributes.System))
                    || !Requirement(attributes.Compressed, flags.HasFlag(FileAttributes.Compressed))
                    || !Requirement(attributes.Sparse, flags.HasFlag(FileAttributes.SparseFile))
                )
                    return SearchVerdict.Fail;
            }
        }
        if (Criteria.MinimumSize is not null || Criteria.MaximumSize is not null)
        {
            if (item.Size is not { } size)
                uncertain = true;
            else if (size < Criteria.MinimumSize || size > Criteria.MaximumSize)
                return SearchVerdict.Fail;
        }
        if (Criteria.MinimumDelta is not null || Criteria.MaximumDelta is not null)
        {
            if (!item.InFirstScan)
                return SearchVerdict.Fail;
            if (item.Delta is not { } delta)
                uncertain = true;
            else if (delta < Criteria.MinimumDelta || delta > Criteria.MaximumDelta)
                return SearchVerdict.Fail;
        }
        if (Criteria.MinimumDeltaPercent is not null || Criteria.MaximumDeltaPercent is not null)
        {
            // A rate needs a known, nonzero size in the first scan of the range.
            if (!item.InFirstScan || item.FirstSize is not { } first || first == 0)
                return SearchVerdict.Fail;
            if (item.Size is not { } last)
                uncertain = true;
            else
            {
                double percent =
                    (double)((BigInteger)last - (BigInteger)first) / (double)first * 100;
                if (
                    percent < Criteria.MinimumDeltaPercent
                    || percent > Criteria.MaximumDeltaPercent
                )
                    return SearchVerdict.Fail;
            }
        }
        if (Criteria.MinimumSwing is { } swing)
        {
            // Known sizes give a lower bound of the swing; unresolved sizes could only widen it.
            bool reached =
                item.MaximumKnownSize is { } high
                && item.MinimumKnownSize is { } low
                && high - low >= swing;
            if (!reached)
            {
                if (!item.HasUnresolvedSize)
                    return SearchVerdict.Fail;
                uncertain = true;
            }
        }
        return new(true, uncertain, needsTrend, unknownAttributes);
    }

    /// <summary>Known sizes in the wrong direction rule a trend out. Without a known step in the
    /// wanted direction, an unknown size can only supply one before or after all known sizes;
    /// between equal known sizes it would also need a step back.</summary>
    public ConditionOutcome DecideTrend(SizeTrendEvidence evidence)
    {
        bool wanted = Criteria.Trend == SizeTrend.Increasing;
        bool contrary = wanted ? evidence.Decreased : evidence.Increased;
        bool confirmed = wanted ? evidence.Increased : evidence.Decreased;
        return contrary || !confirmed && !evidence.UnknownAtEdge ? ConditionOutcome.Fail
            : evidence.Unknown ? ConditionOutcome.Uncertain
            : ConditionOutcome.Pass;
    }

    /// <summary>The trend of one size per scan in which the item was observed, oldest first.
    /// An item without any observation fails.</summary>
    public ConditionOutcome DecideTrend(IReadOnlyList<UInt128?> sizes) =>
        sizes.Count == 0 ? ConditionOutcome.Fail : DecideTrend(SizeTrendEvidence.FromSeries(sizes));

    /// <summary>Depth, base folder and, when it applies to the path, the name pattern.
    /// <paramref name="parentPath"/> is the parent's display path relative to the root (empty for
    /// the root); it may be null when <see cref="NeedsPaths"/> is false.</summary>
    public bool PathMatches(int depth, string? parentPath, string name)
    {
        if (depth < Criteria.MinimumDepth || depth > Criteria.MaximumDepth)
            return false;
        if (!NeedsPaths)
            return true;
        ArgumentNullException.ThrowIfNull(parentPath);
        string path = parentPath.Length == 0 ? name : parentPath + '\\' + name;
        if (
            baseFolder is not null
            && !(
                path.Length > baseFolder.Length
                && path.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase)
            )
        )
            return false;
        return !NameOnPath || NameMatches(path, pathPattern!);
    }

    public bool NamedStreamsMatch(bool hasNamedStreams) =>
        Requirement(Criteria.Attributes.NamedStreams, hasNamedStreams);

    private bool PresenceMatches(SearchItem item) =>
        Criteria.Presence switch
        {
            SearchPresence.InAllScans => item.ObservedCount == ScanCount,
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

    // The text is an item name, or its path with the pattern made for paths.
    private bool NameMatches(string text, string? pattern) =>
        string.IsNullOrEmpty(pattern)
        || Criteria.NameMode switch
        {
            NameMatchMode.Wildcard => FileSystemName.MatchesSimpleExpression(
                pattern,
                text,
                ignoreCase: true
            ),
            NameMatchMode.RegularExpression => regex!.IsMatch(text),
            _ => text.Contains(pattern, StringComparison.OrdinalIgnoreCase),
        };

    private bool HasExtension(string name) =>
        Criteria.Extensions.Any(extension =>
        {
            string suffix = "." + extension.TrimStart('.');
            return name.Length > suffix.Length
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        });
}
