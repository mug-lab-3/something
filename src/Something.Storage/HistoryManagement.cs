using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

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
            throw new ArgumentException("開始日時は終了日時以前にしてください。");
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
                "夏時間の切り替えで存在しない、または重複する日時です。別の日時を指定してください。"
            );
        return new(local, zone.GetUtcOffset(local));
    }
}

public sealed partial class ScanHistoryArchive
{
    /// <summary>Read counts only for the requested published scans, for a day in the history picker.</summary>
    public IReadOnlyDictionary<Guid, long> ReadRecordCounts(
        IReadOnlyCollection<Guid> scanIds,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(scanIds);
        token.ThrowIfCancellationRequested();
        if (scanIds.Count == 0)
            return new Dictionary<Guid, long>();
        var ids = scanIds.Distinct().ToArray();
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            var context = store.HistoryContext;
            var snapshots = context
                .HistorySnapshots.AsNoTracking()
                .Where(row => !row.Retired && ids.Contains(row.Id));
            var counts = (
                from snapshot in snapshots
                join observation in context.Observations.AsNoTracking()
                    on snapshot.Id equals observation.ScanId
                    into observations
                select new { snapshot.Id, Count = observations.LongCount() }
            ).ToDictionary(row => row.Id, row => row.Count);
            token.ThrowIfCancellationRequested();
            return counts;
        }
    }

    public ScanHistorySummary[] ListSummaries(string volumeKey, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            var context = store.HistoryContext;
            var snapshots = context
                .HistorySnapshots.AsNoTracking()
                .Where(row => !row.Retired && row.VolumeKey == volumeKey);
            var entries = snapshots
                .OrderByDescending(row => row.CompletedUtcTicks)
                .ThenByDescending(row => row.Id)
                .AsEnumerable()
                .Select(ToEntry)
                .ToArray();
            if (entries.Length == 0)
                return [];
            var counts = (
                from observation in context.Observations.AsNoTracking()
                join snapshot in snapshots on observation.ScanId equals snapshot.Id
                group observation by observation.ScanId into scan
                select new { Id = scan.Key, Count = scan.LongCount() }
            ).ToDictionary(row => row.Id, row => row.Count);
            token.ThrowIfCancellationRequested();
            return entries
                .Select(row => new ScanHistorySummary(row, counts.GetValueOrDefault(row.Id)))
                .ToArray();
        }
    }

    public ScanHistoryEntry[] ListAll(CancellationToken token = default)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            return store
                .HistoryContext.HistorySnapshots.AsNoTracking()
                .Where(row => !row.Retired)
                .OrderByDescending(row => row.CompletedUtcTicks)
                .ThenByDescending(row => row.Id)
                .AsEnumerable()
                .Select(ToEntry)
                .ToArray();
        }
    }

    /// <summary>Atomically hide only the confirmed IDs. Active readers remain valid until released.
    /// CollectRetired removes unleased data later; shared facts still used by other scans are retained.</summary>
    public Guid[] Delete(IReadOnlyCollection<Guid> confirmedIds, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedIds);
        var ids = confirmedIds.ToHashSet();
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            var context = store.HistoryContext;
            using var transaction = context.Database.BeginTransaction();
            var removed = new List<Guid>();
            foreach (var batch in ids.Chunk(256))
            {
                token.ThrowIfCancellationRequested();
                foreach (
                    var row in context.HistorySnapshots.Where(row =>
                        !row.Retired && batch.Contains(row.Id)
                    )
                )
                {
                    row.Retired = true;
                    removed.Add(row.Id);
                }
            }
            context.SaveChanges();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return removed.ToArray();
        }
    }

    /// <summary>Collect deleted generations without changing the retention policy of remaining history.
    /// Returns the number still protected by readers in this or another process.</summary>
    public int CollectRetired(CancellationToken token = default)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            CollectRetiredCore(store, token);
            return store.HistoryContext.HistorySnapshots.Count(row => row.Retired);
        }
    }
}
