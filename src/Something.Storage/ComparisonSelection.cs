namespace Something.Storage;

public sealed record ComparisonSelection(
    DateTimeOffset RequestedBeforeUtc,
    DateTimeOffset RequestedAfterUtc,
    ScanHistoryEntry? Before,
    ScanHistoryEntry? After
)
{
    public bool Available => Before is not null && After is not null && Before.Id != After.Id;
    public bool IsPartial => Available && Before!.CompletedUtc > RequestedBeforeUtc;

    /// <summary>Anchor presets to the selected observation, excluding all later observations.
    /// Equal completion times use the same deterministic ID ordering as history selection.</summary>
    public static ComparisonSelection ResolveAtScan(
        IEnumerable<ScanHistoryEntry> entries,
        FolderChangePeriod period,
        ScanHistoryEntry endpoint,
        TimeZoneInfo zone
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(endpoint);
        var rows = entries.ToArray();
        if (
            rows.Any(row => row.VolumeKey != endpoint.VolumeKey)
            || !rows.Any(row => row.Id == endpoint.Id)
        )
            throw new ArgumentException(
                "The endpoint must be retained in the selected volume.",
                nameof(endpoint)
            );
        return Resolve(
            rows.Where(row =>
                row.CompletedUtc < endpoint.CompletedUtc
                || row.CompletedUtc == endpoint.CompletedUtc && row.Id.CompareTo(endpoint.Id) <= 0
            ),
            period,
            endpoint.CompletedUtc,
            zone
        );
    }

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
        var after = At(end);
        var before = At(start);
        if (before is null && after is not null)
            before = rows.Where(row =>
                    row.CompletedUtc > start && row.CompletedUtc <= end && row.Id != after.Id
                )
                .OrderBy(row => row.CompletedUtc)
                .ThenBy(row => row.Id)
                .FirstOrDefault();
        // A lone observation is a starting point, not a completed comparison.
        // Keep the missing second observation explicit instead of reusing it at both ends.
        return before is null || before.Id == after?.Id
            ? new(start, end, after, null)
            : new(start, end, before, after);
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
