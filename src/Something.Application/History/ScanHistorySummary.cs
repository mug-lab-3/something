namespace Something.Application.History;

/// <summary>Logical observation count for one scan, not the sum of physical rows across shared tables.</summary>
public sealed record ScanHistorySummary(ScanHistoryEntry Entry, long RecordCount);

/// <summary>Inclusive completion-time boundaries; callers confirm a frozen set of IDs before deletion.</summary>
public static class HistoryDeletion
{
    public static ScanHistoryEntry[] SelectRange(
        IEnumerable<ScanHistoryEntry> entries,
        DateTimeOffset? startInclusive,
        DateTimeOffset endInclusive
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (startInclusive > endInclusive)
            throw new ArgumentException("The start must not be later than the end.").WithFailure(
                HistoryFailure.StartAfterEnd
            );
        return entries
            .Where(row =>
                (!startInclusive.HasValue || row.CompletedUtc >= startInclusive.Value)
                && row.CompletedUtc <= endInclusive
            )
            .ToArray();
    }

    public static DateTimeOffset LocalInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new ArgumentException(
                "The local time is skipped or repeated by a daylight saving transition."
            ).WithFailure(HistoryFailure.InvalidLocalTime);
        return new(local, zone.GetUtcOffset(local));
    }
}
