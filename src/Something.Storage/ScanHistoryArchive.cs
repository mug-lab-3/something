using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

/// <summary>All volumes and generations share one DuckDB file. Typed generation imports and retention are atomic.</summary>
public sealed partial class ScanHistoryArchive
{
    private readonly string root;
    private readonly object sync;
    private readonly Dictionary<Guid, int> readers = [];
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo timeZone;
    public string? MaintenanceError { get; private set; }
    private string DatabasePath => Path.Combine(root, "catalog.duckdb");

    private string LeasePath(Guid id) => Path.Combine(root, "leases", $"{id:N}.lock");

    public ScanHistoryArchive(
        string root,
        TimeProvider? clock = null,
        TimeZoneInfo? timeZone = null
    )
    {
        this.root = Path.GetFullPath(root);
        sync = DuckDatabaseBootstrap.Gate(DatabasePath);
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

    private ScanStore OpenDatabase(CancellationToken token, ulong? cacheBytes = null)
    {
        foreach (string directory in new[] { root, Path.Combine(root, "leases") })
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "History directories must not be reparse points."
                );
        }
        var store = NewHistoryStore(cacheBytes, token);
        try
        {
            if (
                store.HistoryContext.HistorySnapshots.Any(row =>
                    !store.HistoryContext.Scans.Any(scan => scan.Id == row.Id)
                )
            )
                throw new InvalidDataException(
                    "This development history uses an unsupported format. Preserve the old history directory and start with an empty one; automatic conversion is disabled."
                );
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private ScanStore NewHistoryStore(ulong? cacheBytes, CancellationToken token) =>
        ScanStore.OpenHistoryWriter(DatabasePath, cacheBytes, token);

    public ScanHistoryEntry Publish(
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        string drive,
        TimeSpan elapsed,
        ulong cacheBytes,
        CancellationToken token = default
    )
    {
        ValidateArguments(buffer.Descriptor.VolumeKey, drive, elapsed);
        lock (sync)
        {
            using var target = OpenDatabase(token, cacheBytes);
            var metadata = ToEntity(
                NewEntry(Guid.Empty, buffer.Descriptor.VolumeKey, drive, endedUtc, elapsed)
            );
            target.PublishHistoryBuffer(buffer, endedUtc, metadata, token);
            MaintainAfterPublication(target);
            return ToEntry(metadata);
        }
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
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            var scan = source.GetScan(scanId);
            if (scan.State != StoredScanState.Completed || scan.EndedUtc is not { } ended)
                throw new InvalidOperationException("Only a completed scan can be archived.");
            ValidateArguments(scan.Descriptor.VolumeKey, drive, elapsed);
            var entry = NewEntry(scanId, scan.Descriptor.VolumeKey, drive, ended, elapsed);
            using var target = OpenDatabase(token);
            if (target.HistoryContext.HistorySnapshots.Any(row => row.Id == scanId))
                throw new InvalidOperationException("This scan has already been archived.");
            var metadata = ToEntity(entry);
            target.CopyHistoryGeneration(source, metadata, token);
            MaintainAfterPublication(target);
            return entry;
        }
    }

    public ScanHistoryEntry[] List(string volumeKey, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        lock (sync)
        {
            using var store = OpenDatabase(token);
            return store
                .HistoryContext.HistorySnapshots.AsNoTracking()
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
            using var store = OpenDatabase(token);
            token.ThrowIfCancellationRequested();
            MaintainCore(store);
            MaintenanceError = null;
        }
    }

    private void MaintainAfterPublication(ScanStore store)
    {
        try
        {
            MaintainCore(store);
            MaintenanceError = null;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or SqliteException
                        or DuckDBException
                        or DbUpdateException
            )
        {
            MaintenanceError = error.Message;
        }
    }

    private void MaintainCore(ScanStore store)
    {
        var context = store.HistoryContext;
        using (var transaction = context.Database.BeginTransaction())
        {
            var rows = context.HistorySnapshots.Where(row => !row.Retired).ToArray();
            var keep = HistoryRetention.Keep(rows.Select(ToEntry), clock.GetUtcNow());
            foreach (var row in rows)
                if (!keep.Contains(row.Id))
                    row.Retired = true;
            context.SaveChanges();
            transaction.Commit();
        }
        context.ChangeTracker.Clear();
        foreach (
            var row in context.HistorySnapshots.AsNoTracking().Where(row => row.Retired).ToArray()
        )
        {
            if (readers.ContainsKey(row.Id))
                continue;
            try
            {
                using (var transaction = context.Database.BeginTransaction())
                {
                    // A tiny shared-read lease protects a generation in another process.
                    // Do not pin a long-lived WAL read transaction for the lifetime of the UI.
                    using var lease = new FileStream(
                        LeasePath(row.Id),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None
                    );
                    store.DeleteHistoryGeneration(row.Id);
                    transaction.Commit();
                }
                File.Delete(LeasePath(row.Id));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public HistorySnapshot Open(Guid id, CancellationToken token = default)
    {
        lock (sync)
        {
            using var writer = OpenDatabase(token);
            using var transaction = writer.HistoryContext.Database.BeginTransaction();
            var row =
                writer
                    .HistoryContext.HistorySnapshots.AsNoTracking()
                    .SingleOrDefault(row => row.Id == id && !row.Retired)
                ?? throw new InvalidOperationException("History generation is unavailable.");
            var lease = new FileStream(
                LeasePath(id),
                FileMode.OpenOrCreate,
                FileAccess.Read,
                FileShare.Read
            );
            ScanStore? reader = null;
            try
            {
                reader = ScanStore.OpenDuckPath(DatabasePath, token, readOnly: true);
                var scan = reader.GetScan(id);
                if (
                    scan.State != StoredScanState.Completed
                    || scan.Descriptor.VolumeKey != row.VolumeKey
                    || scan.EndedUtc?.UtcTicks != row.CompletedUtcTicks
                )
                    throw new InvalidDataException(
                        "History metadata and generation identities disagree."
                    );
                transaction.Commit();
                readers[id] = readers.GetValueOrDefault(id) + 1;
                return new(
                    reader,
                    ToEntry(row),
                    Math.Max(1, (ulong)new FileInfo(DatabasePath).Length),
                    () =>
                    {
                        lease.Dispose();
                        lock (sync)
                        {
                            if (--readers[id] == 0)
                                readers.Remove(id);
                        }
                    }
                );
            }
            catch
            {
                reader?.Dispose();
                lease.Dispose();
                throw;
            }
        }
    }

    private ScanHistoryEntry NewEntry(
        Guid id,
        string key,
        string drive,
        DateTimeOffset ended,
        TimeSpan elapsed
    )
    {
        var local = TimeZoneInfo.ConvertTime(ended, timeZone);
        return new(
            id,
            key,
            drive,
            ended,
            DateOnly.FromDateTime(local.DateTime),
            (int)local.Offset.TotalMinutes,
            elapsed
        );
    }

    private static void ValidateArguments(string key, string drive, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(drive);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed.Ticks);
        if (key.StartsWith("diagnostic:", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Persistent history requires a stable volume identity."
            );
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

    internal static ScanHistoryEntry ToEntry(HistorySnapshotEntity row) =>
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
