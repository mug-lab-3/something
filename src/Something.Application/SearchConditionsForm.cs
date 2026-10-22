using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Something.Application.Search;
using Something.Statistics;

namespace Something.Application;

/// <summary>The unit a size is typed in; binary multiples, as everywhere else in the application.</summary>
public enum SearchSizeUnit
{
    Bytes,
    KiB,
    MiB,
    GiB,
    TiB,
}

/// <summary>The input a problem belongs to, so the conditions screen can show it next to it.</summary>
public enum SearchFormField
{
    Range,
    Name,
    Extensions,
    Depth,
    Size,
    Delta,
    Rate,
    Swing,
}

/// <summary>What is wrong with an input. Values from <see cref="SearchCriteriaIssue"/> name problems
/// of the combined conditions; the rest name problems of one typed value.</summary>
public enum SearchFormProblem
{
    RangeMissing,
    NotANumber,
    NegativeNotAllowed,
    TooLarge,
    Criteria,
}

/// <param name="Field">The input the problem belongs to.</param>
/// <param name="Problem">What is wrong with it.</param>
/// <param name="Criteria">Set when <paramref name="Problem"/> is <see cref="SearchFormProblem.Criteria"/>.</param>
public readonly record struct SearchFormIssue(
    SearchFormField Field,
    SearchFormProblem Problem,
    SearchCriteriaIssue? Criteria = null
);

/// <param name="Criteria">What the typed input means, with unreadable values left out; null
/// until the range is complete. Search only when <see cref="CanSearch"/>.</param>
/// <param name="Issues">Every problem at once, so each input can show its own reason.</param>
public sealed record SearchFormResult(
    HistorySearchCriteria? Criteria,
    ImmutableArray<SearchFormIssue> Issues
)
{
    public bool CanSearch => Criteria is not null && Issues.IsEmpty;

    public bool Has(SearchFormField field) => Issues.Any(issue => issue.Field == field);
}

/// <summary>The inputs of the conditions screen, as typed. Building them into criteria is separate
/// from the window so that it can be tested: an empty input means "not specified", units are
/// applied, and every problem is reported at once.</summary>
public sealed class SearchConditionsForm
{
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public SearchTarget Target { get; set; }
    public string NamePattern { get; set; } = "";
    public NameMatchMode NameMode { get; set; }

    /// <summary>Whether the name pattern is matched against the whole path, so that everything
    /// under a folder with a matching name is included. On unless it is switched off.</summary>
    public bool NameIncludesPath { get; set; } = true;

    /// <summary>Separated by spaces, commas or semicolons; a leading dot is ignored.</summary>
    public string Extensions { get; set; } = "";

    /// <summary>Relative to the volume root; slashes are read as backslashes.</summary>
    public string BaseFolder { get; set; } = "";
    public string MinimumDepth { get; set; } = "";
    public string MaximumDepth { get; set; } = "";
    public AttributeRequirement Hidden { get; set; }
    public AttributeRequirement System { get; set; }
    public AttributeRequirement Compressed { get; set; }
    public AttributeRequirement Sparse { get; set; }
    public AttributeRequirement NamedStreams { get; set; }

    /// <summary>Which size the size, change and swing conditions, the values shown and the graph
    /// use: the space allocated on disk (the default, as in the other tabs) or the length of the
    /// file, which differs for compressed, sparse and cloud-only files.</summary>
    public SearchSizeKind SizeKind { get; set; }
    public string MinimumSize { get; set; } = "";
    public string MaximumSize { get; set; } = "";
    public SearchSizeUnit SizeUnit { get; set; } = SearchSizeUnit.MiB;
    public string MinimumDelta { get; set; } = "";
    public string MaximumDelta { get; set; } = "";
    public SearchSizeUnit DeltaUnit { get; set; } = SearchSizeUnit.MiB;
    public string MinimumRate { get; set; } = "";
    public string MaximumRate { get; set; } = "";
    public string MinimumSwing { get; set; } = "";
    public SearchSizeUnit SwingUnit { get; set; } = SearchSizeUnit.MiB;
    public SizeTrend Trend { get; set; }
    public SearchPresence Presence { get; set; }

    /// <summary>Returns every condition except the range to its initial value.</summary>
    public void ClearConditions()
    {
        var fresh = new SearchConditionsForm();
        Target = fresh.Target;
        NamePattern = fresh.NamePattern;
        NameMode = fresh.NameMode;
        NameIncludesPath = fresh.NameIncludesPath;
        Extensions = fresh.Extensions;
        BaseFolder = fresh.BaseFolder;
        MinimumDepth = fresh.MinimumDepth;
        MaximumDepth = fresh.MaximumDepth;
        Hidden = System = Compressed = Sparse = NamedStreams = AttributeRequirement.Any;
        SizeKind = fresh.SizeKind;
        MinimumSize = MaximumSize = "";
        SizeUnit = fresh.SizeUnit;
        MinimumDelta = MaximumDelta = "";
        DeltaUnit = fresh.DeltaUnit;
        MinimumRate = MaximumRate = "";
        MinimumSwing = "";
        SwingUnit = fresh.SwingUnit;
        Trend = fresh.Trend;
        Presence = fresh.Presence;
    }

    /// <summary>Whether any condition besides the range differs from its initial value.</summary>
    public bool HasConditions => !ConditionValues(this).SequenceEqual(ConditionValues(new()));

    private static IEnumerable<object> ConditionValues(SearchConditionsForm form) =>
        [
            form.Target,
            form.NamePattern,
            form.NameMode,
            form.NameIncludesPath,
            form.Extensions,
            form.BaseFolder,
            form.MinimumDepth,
            form.MaximumDepth,
            form.Hidden,
            form.System,
            form.Compressed,
            form.Sparse,
            form.NamedStreams,
            form.SizeKind,
            form.MinimumSize,
            form.MaximumSize,
            form.SizeUnit,
            form.MinimumDelta,
            form.MaximumDelta,
            form.DeltaUnit,
            form.MinimumRate,
            form.MaximumRate,
            form.MinimumSwing,
            form.SwingUnit,
            form.Trend,
            form.Presence,
        ];

    public SearchConditionsForm Copy() => (SearchConditionsForm)MemberwiseClone();

    /// <param name="visibility">The display settings; they apply only without attribute conditions.</param>
    /// <param name="range">The scans the range selects, once known, to report an empty range or
    /// change conditions on a single scan.</param>
    /// <param name="culture">How numbers are written; the UI culture of the screen.</param>
    public SearchFormResult Build(
        FileVisibilityOptions visibility,
        SearchRangePreview? range,
        IFormatProvider? culture = null
    )
    {
        culture ??= CultureInfo.CurrentCulture;
        var issues = ImmutableArray.CreateBuilder<SearchFormIssue>();
        void Problem(SearchFormField field, SearchFormProblem problem) =>
            issues.Add(new(field, problem));

        UInt128? Size(string text, SearchSizeUnit unit, SearchFormField field) =>
            ParseBytes(text, unit, culture) switch
            {
                (null, null) => null,
                ({ } value, _) when value.Sign < 0 => Reject<UInt128?>(
                    field,
                    SearchFormProblem.NegativeNotAllowed
                ),
                ({ } value, _) when value > (BigInteger)UInt128.MaxValue => Reject<UInt128?>(
                    field,
                    SearchFormProblem.TooLarge
                ),
                ({ } value, _) => (UInt128)value,
                (null, { } problem) => Reject<UInt128?>(field, problem),
            };
        BigInteger? Signed(string text, SearchSizeUnit unit, SearchFormField field) =>
            ParseBytes(text, unit, culture) switch
            {
                (null, null) => null,
                ({ } value, _) => value,
                (null, { } problem) => Reject<BigInteger?>(field, problem),
            };
        double? Rate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (
                double.TryParse(text, NumberStyles.Float, culture, out double value)
                && double.IsFinite(value)
            )
                return value;
            return Reject<double?>(SearchFormField.Rate, SearchFormProblem.NotANumber);
        }
        int? Depth(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return int.TryParse(text, NumberStyles.Integer, culture, out int value)
                ? value
                : Reject<int?>(SearchFormField.Depth, SearchFormProblem.NotANumber);
        }
        T Reject<T>(SearchFormField field, SearchFormProblem problem)
        {
            Problem(field, problem);
            return default!;
        }

        var minimumSize = Size(MinimumSize, SizeUnit, SearchFormField.Size);
        var maximumSize = Size(MaximumSize, SizeUnit, SearchFormField.Size);
        var minimumDelta = Signed(MinimumDelta, DeltaUnit, SearchFormField.Delta);
        var maximumDelta = Signed(MaximumDelta, DeltaUnit, SearchFormField.Delta);
        var minimumRate = Rate(MinimumRate);
        var maximumRate = Rate(MaximumRate);
        var swing = Size(MinimumSwing, SwingUnit, SearchFormField.Swing);
        var minimumDepth = Depth(MinimumDepth);
        var maximumDepth = Depth(MaximumDepth);
        // A repeated problem of the same input reads as one.
        var distinct = issues.Distinct().ToList();
        issues.Clear();
        issues.AddRange(distinct);

        if (Start is not { } start || End is not { } end)
        {
            Problem(SearchFormField.Range, SearchFormProblem.RangeMissing);
            return new(null, issues.ToImmutable());
        }
        var criteria = new HistorySearchCriteria(start.ToUniversalTime(), end.ToUniversalTime())
        {
            Target = Target,
            NamePattern = string.IsNullOrEmpty(NamePattern) ? null : NamePattern,
            NameMode = NameMode,
            NameIncludesPath = NameIncludesPath,
            Extensions = ReadExtensions(Extensions),
            BaseFolder = ReadBaseFolder(BaseFolder),
            MinimumDepth = minimumDepth,
            MaximumDepth = maximumDepth,
            Attributes = new(Hidden, System, Compressed, Sparse, NamedStreams),
            SizeKind = SizeKind,
            MinimumSize = minimumSize,
            MaximumSize = maximumSize,
            MinimumDelta = minimumDelta,
            MaximumDelta = maximumDelta,
            MinimumDeltaPercent = minimumRate,
            MaximumDeltaPercent = maximumRate,
            MinimumSwing = swing,
            Trend = Trend,
            Presence = Presence,
            Visibility = visibility,
        };
        foreach (var issue in HistorySearchCriteriaCheck.Check(criteria, range))
            issues.Add(new(FieldOf(issue), SearchFormProblem.Criteria, issue));
        return new(criteria, issues.ToImmutable());
    }

    /// <summary>Whether the current input differs from the conditions a search ran with. Input
    /// that cannot be read counts as different, since it is not what was searched.</summary>
    public bool DiffersFrom(
        HistorySearchCriteria searched,
        FileVisibilityOptions visibility,
        IFormatProvider? culture = null
    )
    {
        var current = Build(visibility, null, culture);
        return current.Criteria is null
            || current.Issues.Any(issue => issue.Problem != SearchFormProblem.Criteria)
            || !Same(current.Criteria, searched);
    }

    public static bool Same(HistorySearchCriteria a, HistorySearchCriteria b) =>
        a with { Extensions = [] } == b with { Extensions = [] }
        && a.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Extensions);

    private static SearchFormField FieldOf(SearchCriteriaIssue issue) =>
        issue switch
        {
            SearchCriteriaIssue.RangeReversed
            or SearchCriteriaIssue.NoScansInRange
            or SearchCriteriaIssue.ChangesNeedTwoScans => SearchFormField.Range,
            SearchCriteriaIssue.InvalidRegularExpression => SearchFormField.Name,
            SearchCriteriaIssue.InvalidExtension => SearchFormField.Extensions,
            SearchCriteriaIssue.DepthRangeInvalid => SearchFormField.Depth,
            SearchCriteriaIssue.SizeRangeReversed => SearchFormField.Size,
            SearchCriteriaIssue.DeltaRangeReversed => SearchFormField.Delta,
            SearchCriteriaIssue.RateRangeInvalid => SearchFormField.Rate,
            _ => throw new ArgumentOutOfRangeException(nameof(issue)),
        };

    private static ImmutableArray<string> ReadExtensions(string text) =>
        [
            .. text.Split(
                    [' ', '\t', ',', ';', '、', '，', '　'],
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Select(part => part.TrimStart('.'))
                .Where(part => part.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    private static string? ReadBaseFolder(string text)
    {
        string folder = text.Trim().Replace('/', '\\').Trim('\\');
        return folder.Length == 0 ? null : folder;
    }

    private static BigInteger UnitBytes(SearchSizeUnit unit) =>
        unit switch
        {
            SearchSizeUnit.Bytes => 1,
            SearchSizeUnit.KiB => BigInteger.One << 10,
            SearchSizeUnit.MiB => BigInteger.One << 20,
            SearchSizeUnit.GiB => BigInteger.One << 30,
            SearchSizeUnit.TiB => BigInteger.One << 40,
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };

    // A decimal such as "1.5" is read exactly and rounded to a whole number of bytes.
    private static (BigInteger? Value, SearchFormProblem? Problem) ParseBytes(
        string text,
        SearchSizeUnit unit,
        IFormatProvider culture
    )
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        if (!decimal.TryParse(text, NumberStyles.Number, culture, out decimal number))
            return (
                null,
                text.Trim().Length > 0 && IsNumberTooLarge(text, culture)
                    ? SearchFormProblem.TooLarge
                    : SearchFormProblem.NotANumber
            );
        try
        {
            var bytes = number * (decimal)UnitBytes(unit);
            return (new BigInteger(Math.Round(bytes, MidpointRounding.AwayFromZero)), null);
        }
        catch (OverflowException)
        {
            return (null, SearchFormProblem.TooLarge);
        }
    }

    // decimal.TryParse fails on both malformed and out-of-range text; only digits can be too large.
    private static bool IsNumberTooLarge(string text, IFormatProvider culture) =>
        BigInteger.TryParse(text, NumberStyles.Integer, culture, out _);
}
