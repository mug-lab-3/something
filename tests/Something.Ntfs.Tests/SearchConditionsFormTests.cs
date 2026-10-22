using System.Globalization;
using Something.Application;
using Something.Application.Search;
using Something.Statistics;

namespace Something.Ntfs.Tests;

/// <summary>What the typed inputs of the conditions screen mean, without a window.</summary>
public sealed class SearchConditionsFormTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset End = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static SearchConditionsForm Form() => new() { Start = Start, End = End };

    private static SearchFormResult Build(
        SearchConditionsForm form,
        SearchRangePreview? range = null,
        FileVisibilityOptions? visibility = null
    ) => form.Build(visibility ?? new(), range, Culture);

    [Fact]
    public void AnEmptyFormMeansNoConditionsOverTheRange()
    {
        // Act
        var result = Build(Form());

        // Assert
        Assert.True(result.CanSearch);
        var criteria = result.Criteria!;
        Assert.Equal(
            (Start.UtcDateTime, End.UtcDateTime),
            (criteria.StartUtc.UtcDateTime, criteria.EndUtc.UtcDateTime)
        );
        Assert.Equal(TimeSpan.Zero, criteria.StartUtc.Offset);
        Assert.False(criteria.UsesChanges);
        Assert.False(criteria.Attributes.IsSpecified);
        Assert.Null(criteria.NamePattern);
        Assert.Null(criteria.BaseFolder);
        Assert.Empty(criteria.Extensions);
    }

    [Fact]
    public void TheRangeIsRequired()
    {
        // Arrange
        var form = new SearchConditionsForm { Start = Start };

        // Act
        var result = Build(form);

        // Assert
        Assert.Null(result.Criteria);
        Assert.False(result.CanSearch);
        Assert.Equal(
            [new SearchFormIssue(SearchFormField.Range, SearchFormProblem.RangeMissing)],
            result.Issues
        );
    }

    [Theory]
    [InlineData("1", SearchSizeUnit.Bytes, 1UL)]
    [InlineData("1", SearchSizeUnit.KiB, 1024UL)]
    [InlineData("1.5", SearchSizeUnit.MiB, 1572864UL)]
    [InlineData(" 2 ", SearchSizeUnit.GiB, 2147483648UL)]
    [InlineData("0.5", SearchSizeUnit.TiB, 549755813888UL)]
    [InlineData("1,024", SearchSizeUnit.Bytes, 1024UL)]
    [InlineData("0.4", SearchSizeUnit.Bytes, 0UL)]
    [InlineData("0.5", SearchSizeUnit.Bytes, 1UL)]
    public void SizesAreReadInTheirUnit(string text, SearchSizeUnit unit, ulong expected)
    {
        // Arrange
        var form = Form();
        form.MinimumSize = text;
        form.SizeUnit = unit;

        // Act
        var result = Build(form);

        // Assert
        Assert.True(result.CanSearch);
        Assert.Equal((UInt128)expected, result.Criteria!.MinimumSize);
        Assert.Null(result.Criteria.MaximumSize);
    }

    [Theory]
    [InlineData("abc", SearchFormProblem.NotANumber)]
    [InlineData("1x", SearchFormProblem.NotANumber)]
    [InlineData("-1", SearchFormProblem.NegativeNotAllowed)]
    [InlineData("99999999999999999999999999999999999", SearchFormProblem.TooLarge)]
    public void UnreadableSizesAreReportedAndLeftOut(string text, SearchFormProblem problem)
    {
        // Arrange
        var form = Form();
        form.MaximumSize = text;

        // Act
        var result = Build(form);

        // Assert
        Assert.False(result.CanSearch);
        Assert.Equal([new SearchFormIssue(SearchFormField.Size, problem)], result.Issues);
        Assert.Null(result.Criteria!.MaximumSize);
    }

    [Fact]
    public void ASizeBeyondTheUnitsRangeIsTooLarge()
    {
        // Arrange: 79 trillion TiB is beyond what a decimal can multiply.
        var form = Form();
        form.MinimumSize = "79228162514264337593543950335";
        form.SizeUnit = SearchSizeUnit.TiB;

        // Act
        var result = Build(form);

        // Assert
        Assert.Equal(
            [new SearchFormIssue(SearchFormField.Size, SearchFormProblem.TooLarge)],
            result.Issues
        );
    }

    [Fact]
    public void ChangesMayBeNegativeAndUseTheirOwnUnit()
    {
        // Arrange
        var form = Form();
        form.MinimumDelta = "-2";
        form.MaximumDelta = "1.5";
        form.DeltaUnit = SearchSizeUnit.KiB;
        form.MinimumRate = "-12.5";
        form.MaximumRate = "200";
        form.MinimumSwing = "3";
        form.SwingUnit = SearchSizeUnit.GiB;

        // Act
        var criteria = Build(form).Criteria!;

        // Assert
        Assert.Equal((System.Numerics.BigInteger)(-2048), criteria.MinimumDelta);
        Assert.Equal((System.Numerics.BigInteger)1536, criteria.MaximumDelta);
        Assert.Equal((-12.5, 200.0), (criteria.MinimumDeltaPercent, criteria.MaximumDeltaPercent));
        Assert.Equal((UInt128)(3UL << 30), criteria.MinimumSwing);
        Assert.True(criteria.UsesChanges);
    }

    [Theory]
    [InlineData("12abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void UnreadableRatesAreReported(string text)
    {
        // Arrange
        var form = Form();
        form.MinimumRate = text;

        // Act
        var result = Build(form);

        // Assert
        Assert.Contains(
            new SearchFormIssue(SearchFormField.Rate, SearchFormProblem.NotANumber),
            result.Issues
        );
    }

    [Fact]
    public void DepthsAreWholeNumbersFromOne()
    {
        // Arrange
        var good = Form();
        good.MinimumDepth = "2";
        good.MaximumDepth = " 5 ";
        var zero = Form();
        zero.MinimumDepth = "0";
        var text = Form();
        text.MaximumDepth = "two";
        var reversed = Form();
        reversed.MinimumDepth = "4";
        reversed.MaximumDepth = "3";

        // Act and assert
        Assert.Equal(
            (2, 5),
            (Build(good).Criteria!.MinimumDepth, Build(good).Criteria!.MaximumDepth)
        );
        var criteriaIssue = (SearchCriteriaIssue?)SearchCriteriaIssue.DepthRangeInvalid;
        Assert.Equal(
            [new SearchFormIssue(SearchFormField.Depth, SearchFormProblem.Criteria, criteriaIssue)],
            Build(zero).Issues
        );
        Assert.Equal(
            [new SearchFormIssue(SearchFormField.Depth, SearchFormProblem.NotANumber)],
            Build(text).Issues
        );
        Assert.Equal(
            [new SearchFormIssue(SearchFormField.Depth, SearchFormProblem.Criteria, criteriaIssue)],
            Build(reversed).Issues
        );
    }

    [Fact]
    public void ExtensionsAreSplitCleanedAndDeduplicated()
    {
        // Arrange
        var form = Form();
        form.Extensions = ".log, TXT;log\t.Bak  、md";

        // Act
        var criteria = Build(form).Criteria!;

        // Assert
        Assert.Equal(["log", "TXT", "Bak", "md"], criteria.Extensions);
    }

    [Fact]
    public void AnExtensionWithASeparatorIsRejected()
    {
        // Arrange
        var form = Form();
        form.Extensions = "a\\b";

        // Act
        var result = Build(form);

        // Assert
        Assert.Equal(
            [
                new SearchFormIssue(
                    SearchFormField.Extensions,
                    SearchFormProblem.Criteria,
                    SearchCriteriaIssue.InvalidExtension
                ),
            ],
            result.Issues
        );
    }

    [Theory]
    [InlineData("Users/me/", "Users\\me")]
    [InlineData("\\Data\\", "Data")]
    [InlineData("  ", null)]
    [InlineData("", null)]
    public void TheBaseFolderIsReadRelativeToTheRoot(string text, string? expected)
    {
        // Arrange
        var form = Form();
        form.BaseFolder = text;

        // Act
        var criteria = Build(form).Criteria!;

        // Assert
        Assert.Equal(expected, criteria.BaseFolder);
    }

    [Fact]
    public void ARegularExpressionThatCannotRunIsReportedWithTheName()
    {
        // Arrange
        var form = Form();
        form.NameMode = NameMatchMode.RegularExpression;
        form.NamePattern = @"(a)\1";

        // Act
        var result = Build(form);

        // Assert
        Assert.Equal(
            [
                new SearchFormIssue(
                    SearchFormField.Name,
                    SearchFormProblem.Criteria,
                    SearchCriteriaIssue.InvalidRegularExpression
                ),
            ],
            result.Issues
        );
        Assert.Equal(@"(a)\1", result.Criteria!.NamePattern);
    }

    [Fact]
    public void EveryProblemIsReportedAtOnceInScreenOrder()
    {
        // Arrange
        var form = Form();
        form.End = Start.AddDays(-1);
        form.NameMode = NameMatchMode.RegularExpression;
        form.NamePattern = "(";
        form.MinimumSize = "5";
        form.MaximumSize = "1";
        form.MinimumDelta = "x";

        // Act
        var result = Build(form);

        // Assert
        Assert.Equal(
            [
                SearchFormField.Delta,
                SearchFormField.Range,
                SearchFormField.Name,
                SearchFormField.Size,
            ],
            result.Issues.Select(issue => issue.Field)
        );
    }

    [Theory]
    [InlineData(0, true, SearchCriteriaIssue.NoScansInRange)]
    [InlineData(1, true, SearchCriteriaIssue.ChangesNeedTwoScans)]
    [InlineData(1, false, null)]
    [InlineData(2, true, null)]
    public void TheScansOfTheRangeDecideWhetherChangesCanBeSearched(
        int scans,
        bool changes,
        SearchCriteriaIssue? expected
    )
    {
        // Arrange
        var form = Form();
        if (changes)
            form.Trend = SizeTrend.Increasing;

        // Act
        var result = Build(form, new SearchRangePreview(scans, null, null));

        // Assert
        Assert.Equal(
            expected is null
                ? []
                :
                [
                    new SearchFormIssue(
                        SearchFormField.Range,
                        SearchFormProblem.Criteria,
                        expected
                    ),
                ],
            result.Issues
        );
    }

    [Fact]
    public void AttributeAndEnumChoicesCarryOver()
    {
        // Arrange
        var form = Form();
        form.Target = SearchTarget.Files;
        form.Hidden = AttributeRequirement.Clear;
        form.NamedStreams = AttributeRequirement.Set;
        form.Trend = SizeTrend.Decreasing;
        form.Presence = SearchPresence.Disappeared;
        form.NameMode = NameMatchMode.Wildcard;
        form.NamePattern = "*.tmp";

        // Act
        var criteria = Build(form, visibility: new(IncludeHidden: false)).Criteria!;

        // Assert
        Assert.Equal(SearchTarget.Files, criteria.Target);
        Assert.Equal(AttributeRequirement.Clear, criteria.Attributes.Hidden);
        Assert.Equal(AttributeRequirement.Set, criteria.Attributes.NamedStreams);
        Assert.Equal(SearchSizeKind.Allocated, criteria.SizeKind);
        Assert.Equal(SizeTrend.Decreasing, criteria.Trend);
        Assert.Equal(SearchPresence.Disappeared, criteria.Presence);
        Assert.Equal((NameMatchMode.Wildcard, "*.tmp"), (criteria.NameMode, criteria.NamePattern));
        Assert.False(criteria.Visibility.IncludeHidden);
    }

    [Fact]
    public void ClearingKeepsTheRangeOnly()
    {
        // Arrange
        var form = Form();
        form.Target = SearchTarget.Folders;
        form.NamePattern = "x";
        form.MinimumSize = "1";
        form.SizeUnit = SearchSizeUnit.TiB;
        form.Hidden = AttributeRequirement.Set;
        form.Trend = SizeTrend.Increasing;
        Assert.True(form.HasConditions);

        // Act
        form.ClearConditions();

        // Assert
        Assert.False(form.HasConditions);
        Assert.Equal((Start, End), (form.Start, form.End));
        Assert.Equal(SearchSizeUnit.MiB, form.SizeUnit);
        Assert.True(SearchConditionsForm.Same(Build(form).Criteria!, Build(Form()).Criteria!));
    }

    [Fact]
    public void TheNamePatternAppliesToThePathUnlessSwitchedOff()
    {
        // Arrange
        var form = Form();
        form.NamePattern = "mug";

        // Act
        var byPath = Build(form).Criteria!;
        form.NameIncludesPath = false;
        var byName = Build(form).Criteria!;

        // Assert: the path is the default; the other is a different search, and clearing
        // returns to the default without counting as a condition
        Assert.True(byPath.NameIncludesPath);
        Assert.False(byName.NameIncludesPath);
        Assert.False(SearchConditionsForm.Same(byName, byPath));
        Assert.True(form.DiffersFrom(byPath, new(), Culture));
        Assert.True(form.HasConditions);
        form.ClearConditions();
        Assert.True(form.NameIncludesPath);
        Assert.False(form.HasConditions);
        Assert.False(Form().HasConditions);
    }

    [Fact]
    public void TheKindOfSizeIsAllocatedUnlessChosenAndCountsAsACondition()
    {
        // Arrange
        var form = Form();
        var allocated = Build(form).Criteria!;

        // Act
        form.SizeKind = SearchSizeKind.Logical;
        var logical = Build(form).Criteria!;

        // Assert: the default matches the other tabs; the other is a different search to run again
        Assert.Equal(SearchSizeKind.Allocated, allocated.SizeKind);
        Assert.Equal(SearchSizeKind.Logical, logical.SizeKind);
        Assert.True(form.HasConditions);
        Assert.True(form.DiffersFrom(allocated, new(), Culture));
        Assert.False(SearchConditionsForm.Same(allocated, logical));
        form.ClearConditions();
        Assert.Equal(SearchSizeKind.Allocated, form.SizeKind);
        Assert.False(form.HasConditions);
    }

    [Fact]
    public void ChangingOnlyTheRangeIsNotAConditionButCountsAsADifference()
    {
        // Arrange
        var form = Form();
        var searched = Build(form).Criteria!;

        // Act
        form.Start = Start.AddDays(1);

        // Assert
        Assert.False(form.HasConditions);
        Assert.True(form.DiffersFrom(searched, new(), Culture));
    }

    [Fact]
    public void ADifferenceIsFoundByContentNotByReference()
    {
        // Arrange
        var form = Form();
        form.Extensions = "log txt";
        var searched = Build(form).Criteria!;
        var same = Form();
        same.Extensions = ".LOG;txt";
        var other = Form();
        other.Extensions = "log md";

        // Act and assert
        Assert.False(same.DiffersFrom(searched, new(), Culture));
        Assert.True(other.DiffersFrom(searched, new(), Culture));
    }

    [Fact]
    public void UnreadableInputCountsAsDifferentFromWhatWasSearched()
    {
        // Arrange
        var form = Form();
        var searched = Build(form).Criteria!;
        form.MinimumSize = "abc";

        // Act and assert: the criteria equal the searched ones, the typed text does not.
        Assert.True(form.DiffersFrom(searched, new(), Culture));
        form.MinimumSize = "";
        Assert.False(form.DiffersFrom(searched, new(), Culture));
        form.Start = null;
        Assert.True(form.DiffersFrom(searched, new(), Culture));
    }

    [Fact]
    public void DisplaySettingsAreAPartOfTheSearch()
    {
        // Arrange
        var form = Form();
        var searched = Build(form).Criteria!;

        // Act and assert
        Assert.True(form.DiffersFrom(searched, new(IncludeSystem: false), Culture));
    }

    [Fact]
    public void NumbersFollowTheCultureOfTheScreen()
    {
        // Arrange
        var form = Form();
        form.MinimumRate = "1,5";

        // Act
        var german = form.Build(new(), null, CultureInfo.GetCultureInfo("de-DE"));
        var invariant = form.Build(new(), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(1.5, german.Criteria!.MinimumDeltaPercent);
        Assert.Null(invariant.Criteria!.MinimumDeltaPercent);
        Assert.Contains(
            new SearchFormIssue(SearchFormField.Rate, SearchFormProblem.NotANumber),
            invariant.Issues
        );
    }

    [Fact]
    public void CopiesDoNotShareTheirInput()
    {
        // Arrange
        var form = Form();
        form.NamePattern = "a";

        // Act
        var copy = form.Copy();
        copy.NamePattern = "b";

        // Assert
        Assert.Equal("a", form.NamePattern);
        Assert.Equal("b", copy.NamePattern);
    }
}
