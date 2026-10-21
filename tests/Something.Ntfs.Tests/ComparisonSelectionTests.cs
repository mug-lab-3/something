using System.Numerics;
using Something.Application.Browsing;
using Something.Application.Comparison;
using Something.Application.History;
using Something.Statistics;

namespace Something.Ntfs.Tests;

public sealed class ComparisonSelectionTests
{
    private static readonly TimeZoneInfo Japan = TimeZoneInfo.CreateCustomTimeZone(
        "TestJapan",
        TimeSpan.FromHours(9),
        "TestJapan",
        "TestJapan"
    );
    private static readonly DateTimeOffset Now = new(
        2026,
        10,
        18,
        15,
        30,
        0,
        TimeSpan.FromHours(9)
    );
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(FolderChangePeriod.Today, 2026, 10, 18)]
    [InlineData(FolderChangePeriod.ThisWeek, 2026, 10, 12)]
    [InlineData(FolderChangePeriod.ThisMonth, 2026, 10, 1)]
    [InlineData(FolderChangePeriod.ThisYear, 2026, 1, 1)]
    public void CalendarRangesUseLocalMidnightAndMonday(
        FolderChangePeriod period,
        int year,
        int month,
        int day
    )
    {
        var selection = ComparisonSelection.Resolve([], period, Now, Japan);

        Assert.Equal(
            new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(9)).ToUniversalTime(),
            selection.RequestedBeforeUtc
        );
        Assert.Equal(Now.ToUniversalTime(), selection.RequestedAfterUtc);
        Assert.False(selection.Available);
    }

    [Theory]
    [InlineData(FolderChangePeriod.Last24Hours, 1)]
    [InlineData(FolderChangePeriod.Last7Days, 7)]
    [InlineData(FolderChangePeriod.Last30Days, 30)]
    [InlineData(FolderChangePeriod.Last365Days, 365)]
    public void RollingRangesUseExactDurations(FolderChangePeriod period, int days)
    {
        var selection = ComparisonSelection.Resolve([], period, Now, Japan);

        Assert.Equal(
            TimeSpan.FromDays(days),
            selection.RequestedAfterUtc - selection.RequestedBeforeUtc
        );
    }

    [Fact]
    public void EndpointsPreferBoundaryBaselineAndExplicitlyMarkPartialCoverage()
    {
        var baseline = History(Now.AddDays(-7).AddHours(-3));
        var tooLate = History(Now.AddDays(-7).AddMinutes(1));
        var after = History(Now.AddMinutes(-1));
        var future = History(Now.AddMinutes(1));

        var selection = ComparisonSelection.Resolve(
            [future, tooLate, baseline, after],
            FolderChangePeriod.Last7Days,
            Now,
            Japan
        );

        Assert.Equal(baseline, selection.Before);
        Assert.Equal(after, selection.After);
        Assert.True(selection.Available);
        Assert.False(selection.IsPartial);
        var partial = ComparisonSelection.Resolve(
            [tooLate, after, future],
            FolderChangePeriod.Last7Days,
            Now,
            Japan
        );
        Assert.Equal(tooLate, partial.Before);
        Assert.Equal(after, partial.After);
        Assert.True(partial.IsPartial);
        Assert.False(
            ComparisonSelection
                .Resolve([baseline], FolderChangePeriod.Last7Days, Now, Japan)
                .Available
        );
    }

    [Theory]
    [InlineData(FolderChangePeriod.Last24Hours)]
    [InlineData(FolderChangePeriod.Last7Days)]
    [InlineData(FolderChangePeriod.Last30Days)]
    [InlineData(FolderChangePeriod.Last365Days)]
    [InlineData(FolderChangePeriod.Today)]
    [InlineData(FolderChangePeriod.ThisWeek)]
    [InlineData(FolderChangePeriod.ThisMonth)]
    [InlineData(FolderChangePeriod.ThisYear)]
    public void RecentHistoryWorksForEveryPresetWithoutInventingMissingCapacity(
        FolderChangePeriod period
    )
    {
        var first = History(Now.AddMinutes(-3));
        var last = History(Now.AddMinutes(-1));

        var selected = ComparisonSelection.Resolve(
            [last, first, History(Now.AddMinutes(1))],
            period,
            Now,
            Japan
        );

        Assert.Equal(first, selected.Before);
        Assert.Equal(last, selected.After);
        Assert.True(selected.Available);
        Assert.True(selected.IsPartial);
        var single = ComparisonSelection.Resolve([last], period, Now, Japan);
        Assert.False(single.Available);
        Assert.False(single.IsPartial);
        Assert.Equal(last, single.Before);
        Assert.Null(single.After);
        var anchoredSingle = ComparisonSelection.ResolveAtScan([last], period, last, Japan);
        Assert.Equal(last, anchoredSingle.Before);
        Assert.Null(anchoredSingle.After);
        Assert.False(anchoredSingle.Available);
        var old = History(Now.AddYears(-2));
        var oldSingle = ComparisonSelection.Resolve([old], period, Now, Japan);
        Assert.Equal(old, oldSingle.Before);
        Assert.Null(oldSingle.After);
    }

    [Fact]
    public void CoincidentCompletionTimesUseDistinctRetainedScansDeterministically()
    {
        var first = History(Now.AddMinutes(-1)) with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        };
        var last = first with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };

        var selected = ComparisonSelection.Resolve(
            [last, first],
            FolderChangePeriod.Last24Hours,
            Now,
            Japan
        );

        Assert.Equal(first, selected.Before);
        Assert.Equal(last, selected.After);
        Assert.True(selected.IsPartial);
    }

    [Fact]
    public void CalendarYearAndElapsed365DaysDifferAcrossLeapYear()
    {
        var now = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);

        var year = ComparisonSelection.Resolve(
            [],
            FolderChangePeriod.ThisYear,
            now,
            TimeZoneInfo.Utc
        );
        var rolling = ComparisonSelection.Resolve(
            [],
            FolderChangePeriod.Last365Days,
            now,
            TimeZoneInfo.Utc
        );

        Assert.Equal(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            year.RequestedBeforeUtc
        );
        Assert.Equal(year.RequestedBeforeUtc, rolling.RequestedBeforeUtc);
        Assert.Equal(
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
            ComparisonSelection
                .Resolve([], FolderChangePeriod.Last365Days, now.AddDays(1), TimeZoneInfo.Utc)
                .RequestedBeforeUtc
        );
    }

    [Fact]
    public void RejectsMixedVolumesAndUnsupportedPeriod()
    {
        Assert.Throws<ArgumentException>(() =>
            ComparisonSelection.Resolve(
                [History(Now), History(Now) with { VolumeKey = "other" }],
                FolderChangePeriod.Today,
                Now,
                Japan
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ComparisonSelection.Resolve([], FolderChangePeriod.Custom, Now, Japan)
        );
    }

    [Fact]
    public void PresetsAnchorToSelectedHistoryRatherThanTheNewestScanOrWallClock()
    {
        var before = History(Now.AddDays(-8));
        var selected = History(Now.AddDays(-1));
        var latest = History(Now);

        var comparison = ComparisonSelection.ResolveAtScan(
            [latest, before, selected],
            FolderChangePeriod.Last7Days,
            selected,
            Japan
        );

        Assert.Equal(selected, comparison.After);
        Assert.Equal(before, comparison.Before);
        Assert.Equal(selected.CompletedUtc.ToUniversalTime(), comparison.RequestedAfterUtc);
        Assert.Equal(
            selected.CompletedUtc.AddDays(-7).ToUniversalTime(),
            comparison.RequestedBeforeUtc
        );
        Assert.False(comparison.IsPartial);
    }

    [Fact]
    public void SelectedEndpointExcludesLaterIdsAtCoincidentCompletionTimes()
    {
        var first = History(Now) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var selected = first with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var latest = first with { Id = Guid.Parse("00000000-0000-0000-0000-000000000003") };

        var comparison = ComparisonSelection.ResolveAtScan(
            [latest, selected, first],
            FolderChangePeriod.Last24Hours,
            selected,
            Japan
        );

        Assert.Equal(first, comparison.Before);
        Assert.Equal(selected, comparison.After);
        Assert.True(comparison.IsPartial);
        Assert.Throws<ArgumentException>(() =>
            ComparisonSelection.ResolveAtScan([first], FolderChangePeriod.Today, selected, Japan)
        );
        Assert.Throws<ArgumentException>(() =>
            ComparisonSelection.ResolveAtScan(
                [selected, first with { VolumeKey = "other" }],
                FolderChangePeriod.Today,
                selected,
                Japan
            )
        );
    }

    [Theory]
    [InlineData(3, 8, 3, 8, 0)]
    [InlineData(9, 8, 9, 7, 23)]
    public void CalendarBoundaryHandlesSkippedAndRepeatedMidnight(
        int month,
        int day,
        int utcMonth,
        int utcDay,
        int utcHour
    )
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new(2026, 1, 1),
            new(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1, 1, 1, 0, 0, 0), 3, 8),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1, 1, 1, 1, 0, 0), 9, 8)
        );
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "MidnightTransition",
            TimeSpan.Zero,
            "MidnightTransition",
            "Standard",
            "Daylight",
            [rule]
        );
        var now = new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero);

        var selection = ComparisonSelection.Resolve([], FolderChangePeriod.Today, now, zone);

        Assert.Equal(
            new DateTimeOffset(2026, utcMonth, utcDay, utcHour, 0, 0, TimeSpan.Zero),
            selection.RequestedBeforeUtc
        );
    }

    [Fact]
    public void SignedDifferenceSupportsEntireUnsignedCapacityRangeAndKnownZero()
    {
        var rows = EntryChanges.Compare(
            [File(20, 1, "huge", UInt128.MaxValue), File(30, 1, "empty", 0)],
            [File(20, 1, "huge", 0), File(30, 1, "empty", 0)],
            Token
        );

        Assert.Equal(-BigInteger.CreateChecked(UInt128.MaxValue), rows[0].Delta);
        Assert.Equal(BigInteger.Zero, rows[1].Delta);
        Assert.All(rows, row => Assert.Equal(FolderChangeStatus.Comparable, row.Status));
    }

    [Fact]
    public void EntryComparisonPreservesLinksAndPairsUnambiguousRename()
    {
        var old = new[]
        {
            File(20, 1, "name", 100),
            File(20, 1, "link", 100),
            File(30, 1, "old", 500),
        };
        var current = new[]
        {
            File(20, 1, "name", 150),
            File(20, 1, "link", 150),
            File(30, 1, "renamed", 200),
        };

        var rows = EntryChanges.Compare(old, current, Token);

        Assert.Equal(3, rows.Length);
        Assert.Equal(new BigInteger(-300), rows[0].Delta);
        Assert.Equal("old", rows[0].Before!.Name);
        Assert.Equal("renamed", rows[0].After!.Name);
        Assert.Equal(2, rows.Count(row => row.Delta == 50));
    }

    [Fact]
    public void MissingCapacityAndReusedSequenceAreNotZeroOrDeletion()
    {
        var rows = EntryChanges.Compare(
            [File(20, 1, "reuse", 100), File(30, 1, "unknown", 200)],
            [
                File(20, 2, "reuse", 10),
                File(30, 1, "unknown", 300) with
                {
                    CapacityIncomplete = true,
                },
            ],
            Token
        );

        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.Null(row.Delta));
        Assert.Contains(rows, row => row.Status == FolderChangeStatus.NotObservedBefore);
        Assert.Contains(rows, row => row.Status == FolderChangeStatus.NotObservedAfter);
        Assert.Contains(rows, row => row.Status == FolderChangeStatus.UncertainCapacity);
        Assert.Null(new EntryChange(File(1, 1, "x", 0), File(1, 1, "x", 10), true).Delta);
    }

    [Fact]
    public void AmbiguousRenamesRemainSeparateAndCancellationStopsWork()
    {
        var rows = EntryChanges.Compare(
            [File(20, 1, "a", 10), File(20, 1, "b", 10)],
            [File(20, 1, "c", 20)],
            Token
        );

        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.Null(row.Delta));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            EntryChanges.Compare([], [], cancelled.Token)
        );
    }

    private static ScanHistoryEntry History(DateTimeOffset time) =>
        new(
            Guid.NewGuid(),
            "volume:A",
            "Demo",
            time,
            DateOnly.FromDateTime(time.DateTime),
            540,
            TimeSpan.Zero
        );

    private static FolderEntry File(ulong index, ushort sequence, string name, UInt128 size) =>
        new(
            FileReference.Create(index, sequence),
            0,
            name,
            false,
            new CapacityAmount(size, size),
            true,
            false,
            false
        );
}
