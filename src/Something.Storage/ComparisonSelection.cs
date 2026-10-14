namespace Something.Storage;

public sealed record ComparisonSelection(
    DateTimeOffset RequestedBeforeUtc,
    DateTimeOffset RequestedAfterUtc,
    ScanHistoryEntry? Before,
    ScanHistoryEntry? After
)
{
    public bool Available => Before is not null && After is not null && Before.Id != After.Id;

    public static ComparisonSelection Resolve(
        IEnumerable<ScanHistoryEntry> entries,
        FolderChangePeriod period,
        DateTimeOffset now,
        TimeZoneInfo zone
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(zone);
        var end = now.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        var date = local.Date;
        DateTimeOffset start = period switch
        {
            FolderChangePeriod.Last24Hours => end.AddHours(-24),
            FolderChangePeriod.Last7Days => end.AddDays(-7),
            FolderChangePeriod.Last30Days => end.AddDays(-30),
            FolderChangePeriod.Last365Days => end.AddDays(-365),
            FolderChangePeriod.Today => Boundary(date, zone),
            FolderChangePeriod.ThisWeek => Boundary(
                date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
                zone
            ),
            FolderChangePeriod.ThisMonth => Boundary(new(local.Year, local.Month, 1), zone),
            FolderChangePeriod.ThisYear => Boundary(new(local.Year, 1, 1), zone),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };
        var rows = entries.ToArray();
        if (rows.Select(row => row.VolumeKey).Distinct().Take(2).Count() > 1)
            throw new ArgumentException("Comparison requires a single volume.", nameof(entries));
        ScanHistoryEntry? At(DateTimeOffset instant) =>
            rows.Where(row => row.CompletedUtc <= instant)
                .OrderByDescending(row => row.CompletedUtc)
                .ThenByDescending(row => row.Id)
                .FirstOrDefault();
        return new(start, end, At(start), At(end));
    }

    private static DateTimeOffset Boundary(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Some time zones advance their clocks at midnight. Use the first valid instant.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
