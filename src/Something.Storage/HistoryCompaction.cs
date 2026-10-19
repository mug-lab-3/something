using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

public sealed record HistoryCompactionResult(long BeforeBytes, long AfterBytes, bool Replaced)
{
    public long ReclaimedBytes => Math.Max(0, BeforeBytes - AfterBytes);
}

public sealed partial class ScanHistoryArchive
{
    private string CompactionBackup => DatabasePath + ".compaction-backup";

    // A backup exists only between atomic replacement and successful reopening. No writer is
    // admitted in that interval. After a crash, conservatively restore the original snapshot.
    private void RecoverCompaction()
    {
        if (!File.Exists(CompactionBackup))
            return;
        DuckDatabaseBootstrap.RequireClosed(DatabasePath);
        RequireOrdinaryFile(CompactionBackup);
        if (File.Exists(CompactionBackup + ".wal"))
            throw new InvalidDataException(
                "DB最適化のバックアップに予期しないWALがあります。復旧用ファイルを保持してください。"
            );
        // Never overwrite a surviving database with a malformed or unrelated backup file.
        using (
            var backup = ScanStore.OpenSharedHistoryReader(CompactionBackup, CancellationToken.None)
        )
        {
            _ = backup.HistoryContext.Scans.AsNoTracking().Select(row => row.Id).Take(1).ToArray();
            _ = backup
                .HistoryContext.HistorySnapshots.AsNoTracking()
                .Select(row => row.Id)
                .Take(1)
                .ToArray();
        }
        if (File.Exists(DatabasePath))
        {
            RequireOrdinaryFile(DatabasePath);
            if (File.Exists(DatabasePath + ".wal"))
                throw new InvalidDataException(
                    "DB最適化の復旧待ちです。元DBのバックアップとWALを保持してください。"
                );
            File.Replace(CompactionBackup, DatabasePath, null);
        }
        else
            File.Move(CompactionBackup, DatabasePath);
        DuckDatabaseBootstrap.ForgetValidation(DatabasePath);
    }

    public void Checkpoint(CancellationToken token = default)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            ExecuteMaintenance(
                store.HistoryContext.Database.GetDbConnection(),
                "CHECKPOINT",
                token
            );
        }
    }

    /// <summary>Requires all history sessions to be released. Never replaces a live engine file.
    /// Copy/validation cancellation leaves the original in place; the final atomic swap is non-cancellable.</summary>
    public HistoryCompactionResult Compact(
        ulong memoryLimitBytes,
        CancellationToken token = default,
        Action<string>? progress = null
    )
    {
        if (
            memoryLimitBytes is < 16384 or > DuckDatabaseBootstrap.MaximumMemoryLimit
            || memoryLimitBytes % 16384 != 0
        )
            throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            DuckDatabaseBootstrap.RequireClosed(DatabasePath);
            progress?.Invoke("未参照データを整理しています…");
            using (var store = OpenDatabase(token, memoryLimitBytes))
            {
                // This exclusive engine can use the exact available-memory budget, including
                // budgets below the ordinary shared-reader bootstrap's initial allowance.
                ExecuteMaintenance(
                    store.HistoryContext.Database.GetDbConnection(),
                    CompactionSettings(memoryLimitBytes),
                    token
                );
                CollectRetiredCore(store, token);
                if (store.HistoryContext.HistorySnapshots.Any(row => row.Retired))
                    throw new InvalidOperationException(
                        "別の履歴参照が残っているため最適化できません。"
                    );
                ExecuteMaintenance(
                    store.HistoryContext.Database.GetDbConnection(),
                    "CHECKPOINT",
                    token
                );
            }
            DuckDatabaseBootstrap.RequireClosed(DatabasePath);
            RequireOrdinaryFile(DatabasePath);
            long before = new FileInfo(DatabasePath).Length;
            long required = checked(before + before / 4 + (64L << 20));
            if (new DriveInfo(Path.GetPathRoot(DatabasePath)!).AvailableFreeSpace < required)
                throw new IOException(
                    "DB最適化用の空きディスク容量が不足しています。元のDBは保持されています。"
                );
            string work = Path.Combine(root, ".compaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string candidate = Path.Combine(work, "compacted.duckdb");
            bool replaced = false;
            try
            {
                progress?.Invoke("DBを詰め直しています…");
                using (var connection = new DuckDBConnection("Data Source=:memory:"))
                {
                    connection.Open();
                    ExecuteMaintenance(connection, CompactionSettings(memoryLimitBytes), token);
                    ExecuteMaintenance(
                        connection,
                        $"ATTACH {SqlPath(DatabasePath)} AS source (READ_ONLY); ATTACH {SqlPath(candidate)} AS compacted; COPY FROM DATABASE source TO compacted;",
                        token
                    );
                    progress?.Invoke("コピーしたデータを検証しています…");
                    ValidateCopy(connection, token);
                    ExecuteMaintenance(
                        connection,
                        "CHECKPOINT compacted; DETACH compacted; DETACH source;",
                        token
                    );
                }
                ValidateCompacted(candidate, memoryLimitBytes, token);
                using (
                    var durable = new FileStream(
                        candidate,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None
                    )
                )
                    durable.Flush(flushToDisk: true);
                long after = new FileInfo(candidate).Length;
                token.ThrowIfCancellationRequested();
                if (after >= before)
                    return new(before, before, false);
                progress?.Invoke("検証済みDBへ切り替えています…");
                token.ThrowIfCancellationRequested();
                // No cancellation/progress callback after this boundary until reopening succeeds.
                File.Replace(candidate, DatabasePath, CompactionBackup);
                replaced = true;
                DuckDatabaseBootstrap.ForgetValidation(DatabasePath);
                ValidateCompacted(DatabasePath, memoryLimitBytes, CancellationToken.None);
                File.Delete(CompactionBackup);
                return new(before, after, true);
            }
            catch
            {
                if (replaced)
                    RecoverCompaction();
                throw;
            }
            finally
            {
                // These paths belong to this invocation, never a caller-provided cleanup path.
                foreach (string file in new[] { candidate, candidate + ".wal" })
                    if (File.Exists(file))
                        File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(work).Any())
                    Directory.Delete(work);
            }
        }
    }

    private static void ValidateCopy(DuckDBConnection connection, CancellationToken token)
    {
        string[] Tables(string database)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT table_name FROM duckdb_tables() WHERE database_name='{database}' AND schema_name='main' AND NOT temporary ORDER BY table_name";
            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read())
                tables.Add(reader.GetString(0));
            return tables.ToArray();
        }
        var tables = Tables("source");
        if (!tables.SequenceEqual(Tables("compacted")))
            throw new InvalidDataException("コピー先のDBテーブル構成が一致しません。");
        foreach (string table in tables)
        {
            token.ThrowIfCancellationRequested();
            string name = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT EXISTS ((SELECT * FROM source.main.{name} EXCEPT ALL SELECT * FROM compacted.main.{name}) UNION ALL (SELECT * FROM compacted.main.{name} EXCEPT ALL SELECT * FROM source.main.{name}))";
            using var cancellation = token.Register(command.Cancel);
            try
            {
                if (
                    Convert.ToBoolean(
                        command.ExecuteScalar(),
                        System.Globalization.CultureInfo.InvariantCulture
                    )
                )
                    throw new InvalidDataException("コピー先のDB内容が一致しません。");
            }
            catch (DbException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
        }
    }

    private static void ValidateCompacted(
        string path,
        ulong memoryLimitBytes,
        CancellationToken token
    )
    {
        using var context = (SharedHistoryDbContext)
            DuckDatabaseBootstrap.Open(
                path,
                token,
                memoryLimitBytes,
                readOnly: true,
                sharedHistory: true
            );
        ExecuteMaintenance(
            context.Database.GetDbConnection(),
            CompactionSettings(memoryLimitBytes),
            token
        );
        using var transaction = context.Database.BeginTransaction();
        context.DirtyGenerations.UnionWith(context.Scans.Select(row => row.Id));
        SharedHistoryPublication.Validate(context, token, null);
        _ = context.HistorySnapshots.AsNoTracking().ToArray();
        transaction.Commit();
    }

    private static string SqlPath(string path) =>
        "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string CompactionSettings(ulong bytes) =>
        $"SET memory_limit='{bytes / 16384 * 16}KiB'; SET threads=2; SET preserve_insertion_order=false; SET temp_directory='';";

    private static void RequireOrdinaryFile(string path)
    {
        if (
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory))
            != 0
        )
            throw new InvalidOperationException(
                "DB paths must refer to ordinary files, not directories or links."
            );
    }

    private static void ExecuteMaintenance(
        DbConnection connection,
        string sql,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var cancellation = token.Register(command.Cancel);
        try
        {
            command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
        }
        catch (DbException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }
}
