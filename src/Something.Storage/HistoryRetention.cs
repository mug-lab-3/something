namespace Something.Storage;

public sealed record ScanHistoryEntry(
    Guid Id,
    string VolumeKey,
    string Drive,
    DateTimeOffset CompletedUtc,
    DateOnly LocalDay,
    int UtcOffsetMinutes,
    TimeSpan Elapsed
)
{
    public DateTimeOffset CompletedLocal =>
        CompletedUtc.ToOffset(TimeSpan.FromMinutes(UtcOffsetMinutes));
}

/// <summary>Keep every completion less than seven days old, plus the latest completion per volume/calendar day.
/// Calendar days are captured at publication, so changing the machine time zone does not rebucket history.</summary>
public static class HistoryRetention
{
    public static IReadOnlySet<Guid> Keep(IEnumerable<ScanHistoryEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.ToArray();
        var cutoff = now - TimeSpan.FromDays(7);
        var keep = rows.Where(row => row.CompletedUtc > cutoff).Select(row => row.Id).ToHashSet();
        foreach (var day in rows.GroupBy(row => (row.VolumeKey, row.LocalDay)))
            keep.Add(
                day.OrderByDescending(row => row.CompletedUtc)
                    .ThenByDescending(row => row.Id)
                    .First()
                    .Id
            );
        return keep;
    }
}

internal sealed class HistorySnapshotEntity
{
    public Guid Id { get; set; }
    public string VolumeKey { get; set; } = "";
    public string Drive { get; set; } = "";
    public long CompletedUtcTicks { get; set; }
    public int LocalDay { get; set; }
    public int UtcOffsetMinutes { get; set; }
    public long ElapsedTicks { get; set; }
    public bool Retired { get; set; }
}
