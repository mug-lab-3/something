using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

/// <summary>Owns immutable full scan databases and a small EF catalog. Inspected file paths never select archive paths.</summary>
public sealed class ScanHistoryArchive
{
    private readonly string root;
    private readonly object sync = new();
    private readonly Dictionary<Guid, int> readers = [];
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo timeZone;
    public string? MaintenanceError { get; private set; }

    public ScanHistoryArchive(
        string root,
        TimeProvider? clock = null,
        TimeZoneInfo? timeZone = null
    )
    {
        this.root = Path.GetFullPath(root);
        this.clock = clock ?? TimeProvider.System;
        this.timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public static ScanHistoryArchive CreateDefault()
    {
        string directory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Application data directory is unavailable.");
        return new(Path.Combine(directory, "Something", "History"));
    }

    private string SnapshotPath(Guid id) => Path.Combine(root, "snapshots", $"{id:N}.sqlite3");

    private ScanDbContext OpenCatalog(CancellationToken token)
    {
        foreach (string directory in new[] { root, Path.Combine(root, "snapshots") })
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "History directories must not be reparse points."
                );
        }
        return SqliteDatabaseBootstrap.Open(Path.Combine(root, "catalog.sqlite3"), token);
    }

    public ScanHistoryEntry Save(
        ScanStore source,
        Guid scanId,
        string drive,
        TimeSpan elapsed,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(drive);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed.Ticks);
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            var scan = source.GetScan(scanId);
            if (scan.State != StoredScanState.Completed || scan.EndedUtc is not { } ended)
                throw new InvalidOperationException("Only a completed scan can be archived.");
            if (scan.Descriptor.VolumeKey.StartsWith("diagnostic:", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Persistent history requires a stable volume identity."
                );
            var local = TimeZoneInfo.ConvertTime(ended, timeZone);
            var entry = new ScanHistoryEntry(
                scanId,
                scan.Descriptor.VolumeKey,
                drive,
                ended,
                DateOnly.FromDateTime(local.DateTime),
                (int)local.Offset.TotalMinutes,
                elapsed
            );
            using var catalog = OpenCatalog(token);
            if (catalog.HistorySnapshots.Any(row => row.Id == scanId))
                throw new InvalidOperationException("This scan has already been archived.");
            string final = SnapshotPath(scanId);
            string pending = final + ".pending";
            bool pendingOwned = false;
            bool finalOwned = false;
            bool committed = false;
            try
            {
                source.SaveNewSnapshot(pending, token);
                pendingOwned = true;
                token.ThrowIfCancellationRequested();
                File.Move(pending, final, overwrite: false);
                pendingOwned = false;
                finalOwned = true;
                using var transaction = catalog.Database.BeginTransaction();
                catalog.HistorySnapshots.Add(ToEntity(entry));
                token.ThrowIfCancellationRequested();
                catalog.SaveChanges();
                transaction.Commit();
                committed = true;
            }
            finally
            {
                if (pendingOwned)
                    File.Delete(pending);
                if (finalOwned && !committed)
                    File.Delete(final);
            }
            // Publication is already durable. Retention maintenance must not report that
            // the new snapshot failed or remove it when a later cleanup fails.
            try
            {
                MaintainCore(catalog);
                MaintenanceError = null;
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or SqliteException
                            or DbUpdateException
                )
            {
                MaintenanceError = error.Message;
            }
            return entry;
        }
    }

    public ScanHistoryEntry[] List(string volumeKey, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        lock (sync)
        {
            using var catalog = OpenCatalog(token);
            return catalog
                .HistorySnapshots.AsNoTracking()
                .Where(row => row.VolumeKey == volumeKey && !row.Retired)
                .OrderByDescending(row => row.CompletedUtcTicks)
                .ThenByDescending(row => row.Id)
                .AsEnumerable()
                .Select(ToEntry)
                .ToArray();
        }
    }

    public void Maintain(CancellationToken token = default)
    {
        lock (sync)
        {
            using var catalog = OpenCatalog(token);
            token.ThrowIfCancellationRequested();
            MaintainCore(catalog);
            MaintenanceError = null;
        }
    }

    private void MaintainCore(ScanDbContext catalog)
    {
        using (var transaction = catalog.Database.BeginTransaction())
        {
            var rows = catalog.HistorySnapshots.Where(row => !row.Retired).ToArray();
            var keep = HistoryRetention.Keep(rows.Select(ToEntry), clock.GetUtcNow());
            foreach (var row in rows)
                if (!keep.Contains(row.Id))
                    row.Retired = true;
            catalog.SaveChanges();
            transaction.Commit();
        }
        foreach (var row in catalog.HistorySnapshots.Where(row => row.Retired).ToArray())
        {
            if (readers.ContainsKey(row.Id))
                continue;
            try
            {
                string path = SnapshotPath(row.Id);
                if (
                    File.Exists(path)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                )
                    continue;
                File.Delete(path);
                catalog.HistorySnapshots.Remove(row);
                catalog.SaveChanges();
            }
            catch (IOException) { } // A reader in another app instance may still own the file.
            catch (UnauthorizedAccessException) { }
        }
    }

    public HistorySnapshot Open(Guid id, CancellationToken token = default)
    {
        lock (sync)
        {
            using var catalog = OpenCatalog(token);
            var row =
                catalog
                    .HistorySnapshots.AsNoTracking()
                    .SingleOrDefault(row => row.Id == id && !row.Retired)
                ?? throw new InvalidOperationException("History snapshot is unavailable.");
            string path = SnapshotPath(id);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("History snapshot must not be a reparse point.");
            var store = ScanStore.OpenSnapshotReadOnly(path, token);
            try
            {
                var scan = store.GetScan(id);
                if (
                    scan.State != StoredScanState.Completed
                    || scan.Descriptor.VolumeKey != row.VolumeKey
                    || scan.EndedUtc?.UtcTicks != row.CompletedUtcTicks
                )
                    throw new InvalidDataException(
                        "History catalog and snapshot identities disagree."
                    );
                readers[id] = readers.GetValueOrDefault(id) + 1;
                return new(
                    store,
                    ToEntry(row),
                    (ulong)new FileInfo(path).Length,
                    () => Release(id)
                );
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }
    }

    private void Release(Guid id)
    {
        lock (sync)
        {
            if (--readers[id] == 0)
                readers.Remove(id);
        }
    }

    private static HistorySnapshotEntity ToEntity(ScanHistoryEntry entry) =>
        new()
        {
            Id = entry.Id,
            VolumeKey = entry.VolumeKey,
            Drive = entry.Drive,
            CompletedUtcTicks = entry.CompletedUtc.UtcTicks,
            LocalDay = entry.LocalDay.DayNumber,
            UtcOffsetMinutes = entry.UtcOffsetMinutes,
            ElapsedTicks = entry.Elapsed.Ticks,
        };

    private static ScanHistoryEntry ToEntry(HistorySnapshotEntity row) =>
        new(
            row.Id,
            row.VolumeKey,
            row.Drive,
            new(row.CompletedUtcTicks, TimeSpan.Zero),
            DateOnly.FromDayNumber(row.LocalDay),
            row.UtcOffsetMinutes,
            TimeSpan.FromTicks(row.ElapsedTicks)
        );
}

public sealed class HistorySnapshot(
    ScanStore store,
    ScanHistoryEntry entry,
    ulong sizeBytes,
    Action release
) : IDisposable
{
    private bool disposed;
    public ScanStore Store { get; } = store;
    public ScanHistoryEntry Entry { get; } = entry;
    public ulong SizeBytes { get; } = sizeBytes;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            Store.Dispose();
        }
        finally
        {
            release();
        }
    }
}
