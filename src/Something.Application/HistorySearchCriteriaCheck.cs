using System.Collections.Immutable;
using System.Numerics;
using Something.Storage;

namespace Something.Application;

/// <summary>Problems the conditions screen shows next to the input; any one disables searching.</summary>
public enum SearchCriteriaIssue
{
    RangeReversed,
    NoScansInRange,
    ChangesNeedTwoScans,
    InvalidRegularExpression,
    InvalidExtension,
    DepthRangeInvalid,
    SizeRangeReversed,
    DeltaRangeReversed,
    RateRangeInvalid,
}

public static class HistorySearchCriteriaCheck
{
    /// <summary>Every issue at once, in screen order, so each input can show its own reason.
    /// Without a range preview, only the criteria themselves are checked.</summary>
    public static ImmutableArray<SearchCriteriaIssue> Check(
        HistorySearchCriteria criteria,
        SearchRangePreview? range
    )
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var issues = ImmutableArray.CreateBuilder<SearchCriteriaIssue>();
        if (criteria.EndUtc < criteria.StartUtc)
            issues.Add(SearchCriteriaIssue.RangeReversed);
        else if (range is { ScanCount: 0 })
            issues.Add(SearchCriteriaIssue.NoScansInRange);
        else if (range is { ScanCount: 1 } && criteria.UsesChanges)
            issues.Add(SearchCriteriaIssue.ChangesNeedTwoScans);
        if (
            criteria.NameMode == NameMatchMode.RegularExpression
            && !string.IsNullOrEmpty(criteria.NamePattern)
            && !HistorySearchCriteria.IsValidRegularExpression(criteria.NamePattern)
        )
            issues.Add(SearchCriteriaIssue.InvalidRegularExpression);
        if (criteria.Extensions.Any(e => string.IsNullOrWhiteSpace(e) || e.Contains('\\')))
            issues.Add(SearchCriteriaIssue.InvalidExtension);
        if (
            criteria.MinimumDepth is < 1
            || criteria.MaximumDepth is < 1
            || criteria.MinimumDepth > criteria.MaximumDepth
        )
            issues.Add(SearchCriteriaIssue.DepthRangeInvalid);
        if (criteria.MinimumSize > criteria.MaximumSize)
            issues.Add(SearchCriteriaIssue.SizeRangeReversed);
        if (criteria.MinimumDelta > criteria.MaximumDelta)
            issues.Add(SearchCriteriaIssue.DeltaRangeReversed);
        if (
            criteria.MinimumDeltaPercent is { } low && !double.IsFinite(low)
            || criteria.MaximumDeltaPercent is { } high && !double.IsFinite(high)
            || criteria.MinimumDeltaPercent > criteria.MaximumDeltaPercent
        )
            issues.Add(SearchCriteriaIssue.RateRangeInvalid);
        return issues.ToImmutable();
    }
}

/// <summary>One short label of the conditions summary. Only specified conditions produce parts;
/// the caller formats sizes, dates and wording for the display language.</summary>
public abstract record SearchSummaryPart;

public sealed record SearchRangePart(DateTimeOffset FirstUtc, DateTimeOffset LastUtc, int ScanCount)
    : SearchSummaryPart;

public sealed record SearchTargetPart(SearchTarget Target) : SearchSummaryPart;

public sealed record SearchNamePart(string Pattern, NameMatchMode Mode) : SearchSummaryPart;

public sealed record SearchExtensionsPart(ImmutableArray<string> Extensions) : SearchSummaryPart;

public sealed record SearchBaseFolderPart(string Path) : SearchSummaryPart;

public sealed record SearchDepthPart(int? Minimum, int? Maximum) : SearchSummaryPart;

public enum SearchAttributeKind
{
    Hidden,
    System,
    Compressed,
    Sparse,
    NamedStreams,
}

public sealed record SearchAttributePart(
    SearchAttributeKind Attribute,
    AttributeRequirement Requirement
) : SearchSummaryPart;

public sealed record SearchSizePart(SearchSizeKind Kind, UInt128? Minimum, UInt128? Maximum)
    : SearchSummaryPart;

public sealed record SearchDeltaPart(BigInteger? Minimum, BigInteger? Maximum) : SearchSummaryPart;

public sealed record SearchDeltaPercentPart(double? Minimum, double? Maximum) : SearchSummaryPart;

public sealed record SearchSwingPart(UInt128 Minimum) : SearchSummaryPart;

public sealed record SearchTrendPart(SizeTrend Trend) : SearchSummaryPart;

public sealed record SearchPresencePart(SearchPresence Presence) : SearchSummaryPart;

public static class HistorySearchSummary
{
    /// <summary>The criteria a completed search used, starting with the scans it actually read.</summary>
    public static ImmutableArray<SearchSummaryPart> Describe(HistorySearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var criteria = result.Criteria;
        var parts = ImmutableArray.CreateBuilder<SearchSummaryPart>();
        if (!result.Scans.IsEmpty)
            parts.Add(
                new SearchRangePart(
                    result.Scans[0].CompletedUtc,
                    result.Scans[^1].CompletedUtc,
                    result.Scans.Length
                )
            );
        if (criteria.Target != SearchTarget.Both)
            parts.Add(new SearchTargetPart(criteria.Target));
        if (!string.IsNullOrEmpty(criteria.NamePattern))
            parts.Add(new SearchNamePart(criteria.NamePattern, criteria.NameMode));
        if (!criteria.Extensions.IsEmpty)
            parts.Add(new SearchExtensionsPart(criteria.Extensions));
        if (!string.IsNullOrEmpty(criteria.BaseFolder?.Trim('\\')))
            parts.Add(new SearchBaseFolderPart(criteria.BaseFolder.Trim('\\')));
        if (criteria.MinimumDepth is not null || criteria.MaximumDepth is not null)
            parts.Add(new SearchDepthPart(criteria.MinimumDepth, criteria.MaximumDepth));
        foreach (
            var (kind, requirement) in new[]
            {
                (SearchAttributeKind.Hidden, criteria.Attributes.Hidden),
                (SearchAttributeKind.System, criteria.Attributes.System),
                (SearchAttributeKind.Compressed, criteria.Attributes.Compressed),
                (SearchAttributeKind.Sparse, criteria.Attributes.Sparse),
                (SearchAttributeKind.NamedStreams, criteria.Attributes.NamedStreams),
            }
        )
            if (requirement != AttributeRequirement.Any)
                parts.Add(new SearchAttributePart(kind, requirement));
        if (criteria.MinimumSize is not null || criteria.MaximumSize is not null)
            parts.Add(
                new SearchSizePart(criteria.SizeKind, criteria.MinimumSize, criteria.MaximumSize)
            );
        if (criteria.MinimumDelta is not null || criteria.MaximumDelta is not null)
            parts.Add(new SearchDeltaPart(criteria.MinimumDelta, criteria.MaximumDelta));
        if (criteria.MinimumDeltaPercent is not null || criteria.MaximumDeltaPercent is not null)
            parts.Add(
                new SearchDeltaPercentPart(
                    criteria.MinimumDeltaPercent,
                    criteria.MaximumDeltaPercent
                )
            );
        if (criteria.MinimumSwing is { } swing)
            parts.Add(new SearchSwingPart(swing));
        if (criteria.Trend != SizeTrend.Any)
            parts.Add(new SearchTrendPart(criteria.Trend));
        if (criteria.Presence != SearchPresence.Any)
            parts.Add(new SearchPresencePart(criteria.Presence));
        return parts.ToImmutable();
    }
}
