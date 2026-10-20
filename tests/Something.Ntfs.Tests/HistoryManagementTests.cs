using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class HistoryManagementTests
{
    private static ScanHistoryEntry Entry(DateTimeOffset date) =>
        new(
            Guid.NewGuid(),
            "volume:A",
            "Demo",
            date,
            DateOnly.FromDateTime(date.DateTime),
            (int)date.Offset.TotalMinutes,
            TimeSpan.Zero
        );

    [Fact]
    public void RangeAndBeforeIncludeExactBoundariesUsingInstantsAcrossOffsets()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(9));
        var end = start.AddDays(1);
        var before = Entry(start.AddTicks(-1));
        var first = Entry(start.ToOffset(TimeSpan.Zero));
        var last = Entry(end.ToOffset(TimeSpan.FromHours(-5)));
        var after = Entry(end.AddTicks(1));
        var entries = new[] { before, first, last, after };
        Assert.Equal(new[] { first, last }, HistoryDeletion.SelectRange(entries, start, end));
        Assert.Equal(
            new[] { before, first, last },
            HistoryDeletion.SelectRange(entries, null, end)
        );
        Assert.Equal(new[] { first }, HistoryDeletion.SelectRange(entries, start, start));
        Assert.Empty(HistoryDeletion.SelectRange([], start, end));
        var reversed = Assert.Throws<ArgumentException>(() =>
            HistoryDeletion.SelectRange(entries, end, start)
        );
        Assert.Equal(HistoryFailure.StartAfterEnd, HistoryFailures.Of(reversed));
    }

    [Fact]
    public void LocalBoundaryRejectsMissingAndRepeatedDaylightSavingTimes()
    {
        var spring = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new(1, 1, 1, 2, 0, 0),
            3,
            2,
            DayOfWeek.Sunday
        );
        var autumn = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new(1, 1, 1, 2, 0, 0),
            11,
            1,
            DayOfWeek.Sunday
        );
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new(2020, 1, 1),
            new(2030, 12, 31),
            TimeSpan.FromHours(1),
            spring,
            autumn
        );
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test",
            TimeSpan.FromHours(-5),
            "Test",
            "Test",
            "Test DST",
            [rule]
        );
        var skipped = Assert.Throws<ArgumentException>(() =>
            HistoryDeletion.LocalInstant(new(2026, 3, 8), new(2, 30), zone)
        );
        var repeated = Assert.Throws<ArgumentException>(() =>
            HistoryDeletion.LocalInstant(new(2026, 11, 1), new(1, 30), zone)
        );
        Assert.Equal(HistoryFailure.InvalidLocalTime, HistoryFailures.Of(skipped));
        Assert.Equal(HistoryFailure.InvalidLocalTime, HistoryFailures.Of(repeated));
        Assert.Equal(
            TimeSpan.FromHours(-4),
            HistoryDeletion.LocalInstant(new(2026, 7, 1), new(12, 0), zone).Offset
        );
        Assert.Equal(
            TimeSpan.FromHours(-5),
            HistoryDeletion.LocalInstant(new(2026, 1, 1), new(12, 0), zone).Offset
        );
    }
}
