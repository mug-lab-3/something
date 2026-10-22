using System.Globalization;
using System.Numerics;
using Something.Application;
using Something.Application.Search;

namespace Something.App;

/// <summary>Short labels for the conditions a search ran with, in the display language.</summary>
internal static class SearchSummaryText
{
    public static string Describe(SearchSummaryPart part) =>
        part switch
        {
            SearchRangePart range => Strings.Format(
                "Summary_Range",
                Time(range.FirstUtc),
                Time(range.LastUtc),
                range.ScanCount
            ),
            SearchTargetPart { Target: SearchTarget.Folders } => Strings.Get(
                "Search_TargetFolders"
            ),
            SearchTargetPart { Target: SearchTarget.Files } => Strings.Get("Search_TargetFiles"),
            SearchTargetPart => Strings.Get("Search_TargetBoth"),
            SearchNamePart { IncludesPath: true } path => path.Mode switch
            {
                NameMatchMode.Wildcard => Strings.Format("Summary_PathWildcard", path.Pattern),
                NameMatchMode.RegularExpression => Strings.Format(
                    "Summary_PathRegex",
                    path.Pattern
                ),
                _ => Strings.Format("Summary_PathContains", path.Pattern),
            },
            SearchNamePart name => name.Mode switch
            {
                NameMatchMode.Wildcard => Strings.Format("Summary_NameWildcard", name.Pattern),
                NameMatchMode.RegularExpression => Strings.Format(
                    "Summary_NameRegex",
                    name.Pattern
                ),
                _ => Strings.Format("Summary_NameContains", name.Pattern),
            },
            SearchExtensionsPart extensions => Strings.Format(
                "Summary_Extensions",
                string.Join(' ', extensions.Extensions)
            ),
            SearchBaseFolderPart folder => Strings.Format("Summary_BaseFolder", folder.Path),
            SearchDepthPart depth => (depth.Minimum, depth.Maximum) switch
            {
                ({ } low, { } high) => Strings.Format("Summary_DepthBetween", low, high),
                ({ } low, null) => Strings.Format("Summary_DepthMin", low),
                (null, { } high) => Strings.Format("Summary_DepthMax", high),
                _ => "",
            },
            SearchAttributePart attribute => Strings.Format(
                "Summary_Attribute",
                AttributeName(attribute.Attribute),
                attribute.Requirement == AttributeRequirement.Set
                    ? Strings.Get("Search_AttributeSet")
                    : Strings.Get("Search_AttributeClear")
            ),
            SearchSizeKindPart => Strings.Get("Summary_SizeKindLogical"),
            SearchSizePart size => (size.Minimum, size.Maximum) switch
            {
                ({ } low, { } high) => Strings.Format(
                    "Summary_SizeBetween",
                    DisplayText.Bytes(low),
                    DisplayText.Bytes(high)
                ),
                ({ } low, null) => Strings.Format("Summary_SizeMin", DisplayText.Bytes(low)),
                (null, { } high) => Strings.Format("Summary_SizeMax", DisplayText.Bytes(high)),
                _ => "",
            },
            SearchDeltaPart delta => (delta.Minimum, delta.Maximum) switch
            {
                ({ } low, { } high) => Strings.Format(
                    "Summary_DeltaBetween",
                    Signed(low),
                    Signed(high)
                ),
                ({ } low, null) => Strings.Format("Summary_DeltaMin", Signed(low)),
                (null, { } high) => Strings.Format("Summary_DeltaMax", Signed(high)),
                _ => "",
            },
            SearchDeltaPercentPart rate => (rate.Minimum, rate.Maximum) switch
            {
                ({ } low, { } high) => Strings.Format(
                    "Summary_RateBetween",
                    Percent(low),
                    Percent(high)
                ),
                ({ } low, null) => Strings.Format("Summary_RateMin", Percent(low)),
                (null, { } high) => Strings.Format("Summary_RateMax", Percent(high)),
                _ => "",
            },
            SearchSwingPart swing => Strings.Format(
                "Summary_SwingMin",
                DisplayText.Bytes(swing.Minimum)
            ),
            SearchTrendPart { Trend: SizeTrend.Increasing } => Strings.Format(
                "Summary_Trend",
                Strings.Get("Search_TrendIncreasing")
            ),
            SearchTrendPart => Strings.Format(
                "Summary_Trend",
                Strings.Get("Search_TrendDecreasing")
            ),
            SearchPresencePart presence => Strings.Format(
                "Summary_Presence",
                presence.Presence switch
                {
                    SearchPresence.InAllScans => Strings.Get("Search_PresenceAll"),
                    SearchPresence.Appeared => Strings.Get("Search_PresenceAppeared"),
                    _ => Strings.Get("Search_PresenceDisappeared"),
                }
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(part)),
        };

    private static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);

    private static string Signed(BigInteger value) => DisplayText.Change(value, null);

    private static string AttributeName(SearchAttributeKind kind) =>
        kind switch
        {
            SearchAttributeKind.Hidden => Strings.Get("Summary_AttributeHidden"),
            SearchAttributeKind.System => Strings.Get("Summary_AttributeSystem"),
            SearchAttributeKind.Compressed => Strings.Get("Summary_AttributeCompressed"),
            SearchAttributeKind.Sparse => Strings.Get("Summary_AttributeSparse"),
            _ => Strings.Get("Summary_AttributeNamedStreams"),
        };

    private static string Percent(double value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);
}
