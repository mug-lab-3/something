using Something.Application.Search;

namespace Something.Ntfs.Tests;

/// <summary>Search conditions decided from item values alone, without a database.</summary>
public sealed class SearchConditionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly HistorySearchCriteria Everything = new(Now.AddDays(-30), Now);
    private const uint Hidden = (uint)FileAttributes.Hidden;
    private const uint System = (uint)FileAttributes.System;
    private const uint Compressed = (uint)FileAttributes.Compressed;
    private const uint Sparse = (uint)FileAttributes.SparseFile;

    /// <summary>A file observed in every one of three scans unless told otherwise.</summary>
    private static SearchItem Item(
        string name = "a.log",
        bool directory = false,
        uint? attributes = 0,
        int first = 0,
        int last = 2,
        int? observed = null,
        ulong? firstSize = 100,
        ulong? size = 100,
        ulong? minimum = null,
        ulong? maximum = null,
        bool unresolved = false
    ) =>
        new(
            FileReference.Create(30, 1),
            FileReference.Create(5, 5),
            name,
            directory,
            attributes,
            first,
            last,
            observed ?? last - first + 1,
            firstSize,
            size,
            minimum ?? Math.Min(firstSize ?? size ?? 0, size ?? firstSize ?? 0),
            maximum ?? Math.Max(firstSize ?? size ?? 0, size ?? firstSize ?? 0),
            unresolved
        );

    private static SearchVerdict Evaluate(
        HistorySearchCriteria criteria,
        SearchItem item,
        SizeTrendEvidence? trend = null,
        int scans = 3
    ) => new SearchConditions(criteria, scans).Evaluate(item, trend);

    [Fact]
    public void EverythingMatchesWithoutDoubt()
    {
        // Act
        var verdict = Evaluate(Everything, Item());

        // Assert
        Assert.Equal(new SearchVerdict(true, false, false, false), verdict);
    }

    [Theory]
    [InlineData(SearchTarget.Both, false, true)]
    [InlineData(SearchTarget.Both, true, true)]
    [InlineData(SearchTarget.Files, false, true)]
    [InlineData(SearchTarget.Files, true, false)]
    [InlineData(SearchTarget.Folders, false, false)]
    [InlineData(SearchTarget.Folders, true, true)]
    public void TargetSelectsFilesOrFolders(SearchTarget target, bool directory, bool expected)
    {
        // Act
        var verdict = Evaluate(Everything with { Target = target }, Item(directory: directory));

        // Assert
        Assert.Equal(expected, verdict.Matches);
    }

    [Theory]
    [InlineData(NameMatchMode.Contains, "LOG", "a.log", true)]
    [InlineData(NameMatchMode.Contains, "b", "a.log", false)]
    [InlineData(NameMatchMode.Wildcard, "A.*", "a.log", true)]
    [InlineData(NameMatchMode.Wildcard, "?.log", "ab.log", false)]
    [InlineData(NameMatchMode.RegularExpression, "^A\\.(log|txt)$", "a.LOG", true)]
    [InlineData(NameMatchMode.RegularExpression, "^b", "a.log", false)]
    [InlineData(NameMatchMode.RegularExpression, "", "a.log", true)]
    public void NameModesIgnoreCase(NameMatchMode mode, string pattern, string name, bool expected)
    {
        // Arrange
        var criteria = Everything with
        {
            NameMode = mode,
            NamePattern = pattern,
        };

        // Act
        var verdict = Evaluate(criteria, Item(name));

        // Assert
        Assert.Equal(expected, verdict.Matches);
    }

    [Theory]
    [InlineData("LOG", "a.log", false, true)]
    [InlineData(".log", "a.log", false, true)]
    [InlineData("log", ".log", false, false)]
    [InlineData("log", "a.log.bak", false, false)]
    [InlineData("log", "logs.log", true, false)]
    public void ExtensionsMatchFilesOnly(
        string extension,
        string name,
        bool directory,
        bool expected
    )
    {
        // Arrange
        var criteria = Everything with
        {
            Extensions = [extension],
        };

        // Act
        var verdict = Evaluate(criteria, Item(name, directory));

        // Assert
        Assert.Equal(expected, verdict.Matches);
    }

    [Theory]
    [InlineData(SearchPresence.InAllScans, 0, 2, 3, true)]
    [InlineData(SearchPresence.InAllScans, 0, 2, 2, false)]
    [InlineData(SearchPresence.Appeared, 1, 2, 2, true)]
    [InlineData(SearchPresence.Appeared, 0, 2, 3, false)]
    [InlineData(SearchPresence.Appeared, 1, 1, 1, false)]
    [InlineData(SearchPresence.Disappeared, 0, 1, 2, true)]
    [InlineData(SearchPresence.Disappeared, 0, 2, 3, false)]
    [InlineData(SearchPresence.Disappeared, 1, 1, 1, false)]
    public void PresenceComparesTheFirstAndLastScans(
        SearchPresence presence,
        int first,
        int last,
        int observed,
        bool expected
    )
    {
        // Act
        var verdict = Evaluate(
            Everything with
            {
                Presence = presence,
            },
            Item(first: first, last: last, observed: observed, firstSize: first == 0 ? 100 : null)
        );

        // Assert
        Assert.Equal(expected, verdict.Matches);
    }

    [Theory]
    [InlineData(Hidden, AttributeRequirement.Set, true)]
    [InlineData(Hidden, AttributeRequirement.Clear, false)]
    [InlineData(0u, AttributeRequirement.Set, false)]
    [InlineData(0u, AttributeRequirement.Clear, true)]
    public void HiddenRequirement(uint attributes, AttributeRequirement requirement, bool expected)
    {
        // Arrange
        var criteria = Everything with
        {
            Attributes = new(Hidden: requirement),
        };

        // Act
        var verdict = Evaluate(criteria, Item(attributes: attributes));

        // Assert
        Assert.Equal(expected, verdict.Matches);
    }

    [Fact]
    public void EveryStandardAttributeIsCheckedIndependently()
    {
        // Arrange
        var criteria = Everything with
        {
            Attributes = new(
                AttributeRequirement.Clear,
                AttributeRequirement.Set,
                AttributeRequirement.Set,
                AttributeRequirement.Clear
            ),
        };

        // Act
        bool exact = Evaluate(criteria, Item(attributes: System | Compressed)).Matches;
        bool hidden = Evaluate(criteria, Item(attributes: Hidden | System | Compressed)).Matches;
        bool sparse = Evaluate(criteria, Item(attributes: System | Compressed | Sparse)).Matches;
        bool uncompressed = Evaluate(criteria, Item(attributes: System)).Matches;

        // Assert
        Assert.Equal((true, false, false, false), (exact, hidden, sparse, uncompressed));
    }

    [Fact]
    public void UnknownAttributesAreReportedInsteadOfDecided()
    {
        // Arrange
        var criteria = Everything with
        {
            Attributes = new(Hidden: AttributeRequirement.Set),
        };

        // Act
        var verdict = Evaluate(criteria, Item(attributes: null));

        // Assert
        Assert.Equal(new SearchVerdict(true, false, false, true), verdict);
    }

    [Fact]
    public void NamedStreamsAreLeftToTheCallerAndDecidedByRequirement()
    {
        // Arrange
        var any = new SearchConditions(Everything, 3);
        var set = new SearchConditions(
            Everything with
            {
                Attributes = new(NamedStreams: AttributeRequirement.Set),
            },
            3
        );

        // Act
        bool[] outcomes =
        [
            any.NeedsNamedStreams,
            set.NeedsNamedStreams,
            set.NamedStreamsMatch(true),
            set.NamedStreamsMatch(false),
            set.Evaluate(Item(attributes: null), null).UnknownAttributes,
        ];

        // Assert
        Assert.Equal([false, true, true, false, false], outcomes);
    }

    [Theory]
    [InlineData(100, 100, 200, true, false)]
    [InlineData(200, 100, 200, true, false)]
    [InlineData(99, 100, 200, false, false)]
    [InlineData(201, 100, 200, false, false)]
    [InlineData(null, 100, 200, true, true)]
    public void SizeBoundsAreInclusiveAndAnUnknownSizeIsUncertain(
        int? size,
        int minimum,
        int maximum,
        bool matches,
        bool uncertain
    )
    {
        // Arrange
        var criteria = Everything with
        {
            MinimumSize = (UInt128)minimum,
            MaximumSize = (UInt128)maximum,
        };

        // Act
        var verdict = Evaluate(
            criteria,
            Item(size: size is { } known ? (ulong)known : null, unresolved: size is null)
        );

        // Assert
        Assert.Equal((matches, uncertain), (verdict.Matches, verdict.Uncertain));
    }

    [Fact]
    public void SizeAboveSixtyFourBitsIsCompared()
    {
        // Arrange
        UInt128 huge = (UInt128)ulong.MaxValue + 10;
        var criteria = Everything with { MinimumSize = (UInt128)ulong.MaxValue + 1 };

        // Act
        var verdict = Evaluate(
            criteria,
            Item() with
            {
                FirstSize = huge,
                Size = huge,
                MinimumKnownSize = huge,
                MaximumKnownSize = huge,
            }
        );

        // Assert
        Assert.True(verdict.Matches);
    }

    [Fact]
    public void AnItemThatCameLaterHasItsWholeSizeAsChangeButNoRate()
    {
        // Arrange
        var item = Item(first: 1, firstSize: null, size: 150);
        var rate = Everything with { MinimumDeltaPercent = 0 };

        // Act
        var verdict = Evaluate(rate, item);

        // Assert: a change from nothing has no rate
        Assert.Equal(150, item.Delta);
        Assert.True(item.AppearedInRange);
        Assert.False(verdict.Matches);
    }

    [Theory]
    [InlineData(0, 100, 150, -100, 100, true, false)]
    [InlineData(0, 150, 100, -50, -50, true, false)]
    [InlineData(0, 150, 100, -49, null, false, false)]
    [InlineData(0, 100, null, 0, null, true, true)]
    // Not in the first scan: counted from zero, so the change is the whole size.
    [InlineData(1, null, 150, 150, 150, true, false)]
    [InlineData(1, null, 150, 151, null, false, false)]
    [InlineData(1, null, 150, 0, 149, false, false)]
    [InlineData(1, null, 0, 0, 0, true, false)]
    // ...unless that size is unresolved: then nothing is known about the change.
    [InlineData(1, null, null, 1, null, true, true)]
    public void DeltaCountsAnItemThatCameLaterFromZero(
        int first,
        int? firstSize,
        int? size,
        int? minimum,
        int? maximum,
        bool matches,
        bool uncertain
    )
    {
        // Arrange
        var criteria = Everything with
        {
            MinimumDelta = minimum,
            MaximumDelta = maximum,
        };

        // Act
        var verdict = Evaluate(
            criteria,
            Item(
                first: first,
                firstSize: firstSize is { } a ? (ulong)a : null,
                size: size is { } b ? (ulong)b : null,
                minimum: 0,
                maximum: 1000,
                unresolved: size is null
            )
        );

        // Assert
        Assert.Equal((matches, uncertain), (verdict.Matches, verdict.Uncertain));
    }

    [Theory]
    [InlineData(100, 150, 50.0, 50.0, true, false)]
    [InlineData(100, 50, -50.0, -50.0, true, false)]
    [InlineData(100, 150, 50.1, null, false, false)]
    [InlineData(0, 150, null, null, false, false)]
    [InlineData(100, null, 10.0, null, true, true)]
    public void RateNeedsANonzeroFirstSize(
        int firstSize,
        int? size,
        double? minimum,
        double? maximum,
        bool matches,
        bool uncertain
    )
    {
        // Arrange
        var criteria = Everything with
        {
            MinimumDeltaPercent = minimum ?? double.MinValue,
            MaximumDeltaPercent = maximum,
        };

        // Act
        var verdict = Evaluate(
            criteria,
            Item(
                firstSize: (ulong)firstSize,
                size: size is { } known ? (ulong)known : null,
                minimum: 0,
                maximum: 1000,
                unresolved: size is null
            )
        );

        // Assert
        Assert.Equal((matches, uncertain), (verdict.Matches, verdict.Uncertain));
    }

    [Theory]
    [InlineData(100, 200, false, 100, true, false)]
    [InlineData(100, 199, false, 100, false, false)]
    [InlineData(100, 199, true, 100, true, true)]
    public void SwingUsesKnownSizesAsALowerBound(
        int minimum,
        int maximum,
        bool unresolved,
        int swing,
        bool matches,
        bool uncertain
    )
    {
        // Arrange
        var criteria = Everything with
        {
            MinimumSwing = (UInt128)swing,
        };

        // Act
        var verdict = Evaluate(
            criteria,
            Item(minimum: (ulong)minimum, maximum: (ulong)maximum, unresolved: unresolved)
        );

        // Assert
        Assert.Equal((matches, uncertain), (verdict.Matches, verdict.Uncertain));
    }

    [Fact]
    public void TrendWithoutEvidenceIsLeftForPerScanSizes()
    {
        // Arrange
        var criteria = Everything with
        {
            Trend = SizeTrend.Increasing,
        };

        // Act
        var verdict = Evaluate(criteria, Item());

        // Assert
        Assert.Equal(new SearchVerdict(true, false, true, false), verdict);
    }

    [Theory]
    [InlineData(SizeTrend.Increasing, new[] { 1, 2, 2, 3 }, ConditionOutcome.Pass)]
    [InlineData(SizeTrend.Increasing, new[] { 1, 1 }, ConditionOutcome.Fail)]
    [InlineData(SizeTrend.Increasing, new[] { 1, 3, 2 }, ConditionOutcome.Fail)]
    [InlineData(SizeTrend.Increasing, new[] { 1, -1, 3 }, ConditionOutcome.Uncertain)]
    [InlineData(SizeTrend.Increasing, new[] { -1, 1, 1 }, ConditionOutcome.Uncertain)]
    [InlineData(SizeTrend.Increasing, new[] { 1, -1, 1 }, ConditionOutcome.Fail)]
    [InlineData(SizeTrend.Increasing, new[] { -1 }, ConditionOutcome.Uncertain)]
    [InlineData(SizeTrend.Decreasing, new[] { 3, 2, 2 }, ConditionOutcome.Pass)]
    [InlineData(SizeTrend.Decreasing, new[] { 2, 3 }, ConditionOutcome.Fail)]
    [InlineData(SizeTrend.Decreasing, new[] { 3, 3, -1 }, ConditionOutcome.Uncertain)]
    public void TrendFromSizesPerScan(SizeTrend trend, int[] sizes, ConditionOutcome expected)
    {
        // Arrange: -1 stands for an unresolved size.
        var conditions = new SearchConditions(Everything with { Trend = trend }, sizes.Length);
        UInt128?[] series = [.. sizes.Select(size => size < 0 ? null : (UInt128?)size)];

        // Act
        var fromSeries = conditions.DecideTrend(series);
        var fromEvidence = Evaluate(
            Everything with
            {
                Trend = trend,
            },
            Item(),
            SizeTrendEvidence.FromSeries(series)
        );

        // Assert
        Assert.Equal(expected, fromSeries);
        Assert.Equal(
            (expected != ConditionOutcome.Fail, expected == ConditionOutcome.Uncertain),
            (fromEvidence.Matches, fromEvidence.Uncertain)
        );
    }

    [Fact]
    public void TrendOfAnUnobservedItemFails()
    {
        // Arrange
        var conditions = new SearchConditions(Everything with { Trend = SizeTrend.Increasing }, 3);

        // Act
        var outcome = conditions.DecideTrend([]);

        // Assert
        Assert.Equal(ConditionOutcome.Fail, outcome);
    }

    [Fact]
    public void EvidenceMarksUnknownSizesAndEdges()
    {
        // Act
        var middle = SizeTrendEvidence.FromSeries([1, null, 2]);
        var edge = SizeTrendEvidence.FromSeries([2, 1, null]);

        // Assert
        Assert.Equal(new SizeTrendEvidence(true, false, true, false), middle);
        Assert.Equal(new SizeTrendEvidence(false, true, true, true), edge);
        Assert.Throws<ArgumentOutOfRangeException>(() => SizeTrendEvidence.FromSeries([]));
    }

    [Theory]
    [InlineData(null, 1, null, "a", true)]
    [InlineData(null, 4, null, "a", false)]
    [InlineData(null, 2, 2, "a", true)]
    [InlineData("\\Data\\", 1, null, "Data", false)]
    [InlineData("Data", 2, null, "DATA\\a.log", true)]
    [InlineData("Data", 3, null, "Data\\Sub\\a.log", true)]
    [InlineData("Data", 2, null, "Database\\a.log", false)]
    public void PathMatchesDepthAndBaseFolderDescendants(
        string? baseFolder,
        int depth,
        int? minimumDepth,
        string path,
        bool expected
    )
    {
        // Arrange
        var conditions = new SearchConditions(
            Everything with
            {
                BaseFolder = baseFolder,
                MinimumDepth = minimumDepth,
                MaximumDepth = 3,
            },
            3
        );
        int separator = path.LastIndexOf('\\');
        string parent = separator < 0 ? "" : path[..separator];
        string name = path[(separator + 1)..];

        // Act
        bool matches = conditions.PathMatches(depth, parent, name);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData(NameMatchMode.Contains, "mug", "Users\\mug", "a.txt", true)]
    [InlineData(NameMatchMode.Contains, "MUG", "Users\\mug\\Docs", "a.txt", true)]
    [InlineData(NameMatchMode.Contains, "mug", "Users\\other", "mug.txt", true)]
    [InlineData(NameMatchMode.Contains, "mug", "Users\\other", "a.txt", false)]
    [InlineData(NameMatchMode.Contains, "users\\mug", "Users\\mug", "a.txt", true)]
    [InlineData(NameMatchMode.Contains, "users/mug", "Users\\mug", "a.txt", true)]
    [InlineData(NameMatchMode.Wildcard, "users/mug/*", "Users\\mug", "a.txt", true)]
    [InlineData(NameMatchMode.Wildcard, "*\\mug\\*.txt", "Users\\mug", "a.txt", true)]
    [InlineData(NameMatchMode.Wildcard, "*\\mug\\*.txt", "Users\\mug", "a.log", false)]
    [InlineData(NameMatchMode.Wildcard, "users\\mug\\*", "Users\\mug", "a.txt", true)]
    [InlineData(NameMatchMode.Wildcard, "users\\mug\\*", "Users\\other", "a.txt", false)]
    [InlineData(NameMatchMode.RegularExpression, "^Users\\\\mug(\\\\|$)", "Users\\mug", "a", true)]
    [InlineData(
        NameMatchMode.RegularExpression,
        "^Users\\\\mug(\\\\|$)",
        "Users\\mugs",
        "a",
        false
    )]
    public void ANamePatternOnThePathCoversEverythingBelowAMatchingFolder(
        NameMatchMode mode,
        string pattern,
        string parent,
        string name,
        bool expected
    )
    {
        // Arrange
        var conditions = new SearchConditions(
            Everything with
            {
                NamePattern = pattern,
                NameMode = mode,
                NameIncludesPath = true,
            },
            3
        );

        // Act
        bool matches = conditions.PathMatches(parent.Count(c => c == '\\') + 2, parent, name);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void ANamePatternOnThePathLeavesTheNameToThePathCheck()
    {
        // Arrange
        var onPath = new SearchConditions(
            Everything with
            {
                NamePattern = "mug",
                NameIncludesPath = true,
            },
            3
        );
        var onName = new SearchConditions(Everything with { NamePattern = "mug" }, 3);

        // Act
        var itemOnPath = onPath.Evaluate(Item("a.log"), null);
        var itemOnName = onName.Evaluate(Item("a.log"), null);

        // Assert: the item name alone does not decide it when the path does
        Assert.True(itemOnPath.Matches);
        Assert.False(itemOnName.Matches);
    }

    [Theory]
    [InlineData("mug", true, true)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    [InlineData("mug", false, false)]
    public void ANamePatternOnThePathNeedsPaths(string? pattern, bool onPath, bool expected)
    {
        // Act
        var conditions = new SearchConditions(
            Everything with
            {
                NamePattern = pattern,
                NameIncludesPath = onPath,
            },
            3
        );

        // Assert
        Assert.Equal(expected, conditions.NeedsPaths);
    }

    [Fact]
    public void ANamePatternOnThePathAndABaseFolderMustBothHold()
    {
        // Arrange
        var conditions = new SearchConditions(
            Everything with
            {
                NamePattern = "mug",
                NameIncludesPath = true,
                BaseFolder = "Users\\mug\\Docs",
            },
            3
        );

        // Act and assert
        Assert.True(conditions.PathMatches(4, "Users\\mug\\Docs", "a.txt"));
        Assert.False(conditions.PathMatches(4, "Users\\mug\\Pics", "a.txt"));
        Assert.False(conditions.PathMatches(3, "Users\\other\\Docs", "a.txt"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("\\", false)]
    [InlineData("Data", true)]
    public void OnlyABaseFolderNeedsPaths(string? baseFolder, bool expected)
    {
        // Act
        var conditions = new SearchConditions(Everything with { BaseFolder = baseFolder }, 3);

        // Assert
        Assert.Equal(expected, conditions.NeedsPaths);
        if (expected)
            Assert.Throws<ArgumentNullException>(() => conditions.PathMatches(1, null, "a"));
        else
            Assert.True(conditions.PathMatches(1, null, "a"));
    }

    [Fact]
    public void DisplaySettingsApplyOnlyWithoutAttributeConditions()
    {
        // Arrange
        var hideHidden = Everything with
        {
            Visibility = new(IncludeHidden: false),
        };
        var plain = new SearchConditions(hideHidden, 3);
        var withAttributes = new SearchConditions(
            hideHidden with
            {
                Attributes = new(System: AttributeRequirement.Clear),
            },
            3
        );

        // Act
        bool[] outcomes =
        [
            plain.AppliesVisibility,
            plain.IsVisible(Hidden),
            plain.IsVisible(System),
            plain.IsVisible(null),
            withAttributes.AppliesVisibility,
            withAttributes.IsVisible(Hidden),
        ];

        // Assert
        Assert.Equal([true, false, true, true, false, true], outcomes);
        // Visibility is decided where items are placed, with their folders; not by Evaluate.
        Assert.True(plain.Evaluate(Item(attributes: Hidden), null).Matches);
    }
}
