using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;
using Something.Statistics;

namespace Something.Application.Search;

public enum SearchTarget
{
    Both,
    Folders,
    Files,
}

public enum NameMatchMode
{
    Contains,
    Wildcard,
    RegularExpression,
}

public enum AttributeRequirement
{
    Any,
    Set,
    Clear,
}

public enum SearchSizeKind
{
    Allocated,
    Logical,
}

public enum SizeTrend
{
    Any,
    Increasing,
    Decreasing,
}

public enum SearchPresence
{
    Any,
    InAllScans,
    Appeared,
    Disappeared,
}

/// <summary>Each requirement is independent. Unknown attributes satisfy neither Set nor Clear.</summary>
public sealed record SearchAttributes(
    AttributeRequirement Hidden = AttributeRequirement.Any,
    AttributeRequirement System = AttributeRequirement.Any,
    AttributeRequirement Compressed = AttributeRequirement.Any,
    AttributeRequirement Sparse = AttributeRequirement.Any,
    AttributeRequirement NamedStreams = AttributeRequirement.Any
)
{
    public const uint HiddenBit = 0x2,
        SystemBit = 0x4,
        SparseBit = 0x200,
        CompressedBit = 0x800;

    public bool IsSpecified =>
        Hidden != AttributeRequirement.Any
        || System != AttributeRequirement.Any
        || Compressed != AttributeRequirement.Any
        || Sparse != AttributeRequirement.Any
        || NamedStreams != AttributeRequirement.Any;

    /// <summary>Conditions on standard attribute bits; named streams are observed separately.</summary>
    public bool UsesStandardAttributes =>
        Hidden != AttributeRequirement.Any
        || System != AttributeRequirement.Any
        || Compressed != AttributeRequirement.Any
        || Sparse != AttributeRequirement.Any;
}

/// <summary>Conditions are combined with AND. Value conditions use the last scan of the range
/// in which the item was observed. Changes run from the first scan of the range to that scan.</summary>
public sealed record HistorySearchCriteria(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public SearchTarget Target { get; init; }
    public string? NamePattern { get; init; }
    public NameMatchMode NameMode { get; init; }

    /// <summary>Without the leading dot; matches files only.</summary>
    public ImmutableArray<string> Extensions { get; init; } = [];

    /// <summary>Relative to the volume root with backslashes; only descendants match.</summary>
    public string? BaseFolder { get; init; }

    /// <summary>The volume root is depth zero, so its children are depth one.</summary>
    public int? MinimumDepth { get; init; }
    public int? MaximumDepth { get; init; }
    public SearchAttributes Attributes { get; init; } = new();
    public SearchSizeKind SizeKind { get; init; }
    public UInt128? MinimumSize { get; init; }
    public UInt128? MaximumSize { get; init; }
    public BigInteger? MinimumDelta { get; init; }
    public BigInteger? MaximumDelta { get; init; }
    public double? MinimumDeltaPercent { get; init; }
    public double? MaximumDeltaPercent { get; init; }
    public UInt128? MinimumSwing { get; init; }
    public SizeTrend Trend { get; init; }
    public SearchPresence Presence { get; init; }

    /// <summary>Applied to the items only when no attribute requirement is specified.</summary>
    public FileVisibilityOptions Visibility { get; init; } = new();

    public bool UsesChanges =>
        MinimumDelta is not null
        || MaximumDelta is not null
        || MinimumDeltaPercent is not null
        || MaximumDeltaPercent is not null
        || MinimumSwing is not null
        || Trend != SizeTrend.Any;

    /// <summary>Throws for contradictory or malformed criteria; user-facing messages belong to the caller.</summary>
    public void Validate()
    {
        if (EndUtc < StartUtc)
            throw new ArgumentException("The range ends before it starts.");
        foreach (
            var value in new Enum[]
            {
                Target,
                NameMode,
                SizeKind,
                Trend,
                Presence,
                Attributes.Hidden,
                Attributes.System,
                Attributes.Compressed,
                Attributes.Sparse,
                Attributes.NamedStreams,
            }
        )
            if (!Enum.IsDefined(value.GetType(), value))
                throw new ArgumentOutOfRangeException(value.GetType().Name);
        if (MinimumSize > MaximumSize)
            throw new ArgumentException("The minimum size exceeds the maximum.");
        if (MinimumDelta > MaximumDelta)
            throw new ArgumentException("The minimum change exceeds the maximum.");
        if (
            MinimumDeltaPercent is { } low && !double.IsFinite(low)
            || MaximumDeltaPercent is { } high && !double.IsFinite(high)
            || MinimumDeltaPercent > MaximumDeltaPercent
        )
            throw new ArgumentException("The change rate range is invalid.");
        if (MinimumDepth is < 1 || MaximumDepth is < 1 || MinimumDepth > MaximumDepth)
            throw new ArgumentException("The depth range is invalid.");
        if (Extensions.Any(e => string.IsNullOrWhiteSpace(e) || e.Contains('\\')))
            throw new ArgumentException("An extension is empty or contains a separator.");
        if (
            NameMode == NameMatchMode.RegularExpression
            && !string.IsNullOrEmpty(NamePattern)
            && !IsValidRegularExpression(NamePattern)
        )
            throw new ArgumentException("The regular expression is invalid.", nameof(NamePattern));
    }

    /// <summary>Whether the name pattern can run. Backreferences and lookarounds are rejected,
    /// since matching never backtracks.</summary>
    public static bool IsValidRegularExpression(string pattern)
    {
        try
        {
            _ = CreateRegex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // NonBacktracking reports constructs it cannot run this way.
            return false;
        }
    }

    public static Regex CreateRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
        );
}
