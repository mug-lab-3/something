using System.Collections.Concurrent;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

internal static class DuckDatabaseBootstrap
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly ConcurrentDictionary<string, byte> Validated = new(
        StringComparer.OrdinalIgnoreCase
    );

    internal static object Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new());

    internal static ScanDbContext Open(
        string path,
        CancellationToken token,
        ulong? budget = null,
        bool readOnly = false
    )
    {
        if (path != ":memory:")
            lock (Gate(path))
                return OpenCore(path, token, budget, readOnly);
        return OpenCore(path, token, budget, readOnly);
    }

    private static ScanDbContext OpenCore(
        string path,
        CancellationToken token,
        ulong? budget,
        bool readOnly
    )
    {
        token.ThrowIfCancellationRequested();
        if (
            budget is { } requested
            && (
                requested is < 64UL * 1024 * 1024 or > 2UL * 1024 * 1024 * 1024
                || requested % 16384 != 0
            )
        )
            throw new ArgumentOutOfRangeException(nameof(budget));
        bool memory = path == ":memory:";
        path = memory ? path : Path.GetFullPath(path);
        bool exists = !memory && File.Exists(path);
        if (readOnly && !exists)
            throw new FileNotFoundException("History database does not exist.");
        if (exists)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Database files must not be reparse points.");
            try
            {
                using var file = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                Span<byte> prefix = stackalloc byte[16];
                int read = file.Read(prefix);
                if (read >= 15 && prefix[..15].SequenceEqual("SQLite format 3"u8))
                    throw new InvalidDataException(
                        "SQLite history is preserved; it cannot be opened as DuckDB."
                    );
            }
            catch (IOException error)
                when ((error.HResult & 0xffff) is 32 or 33 && Validated.ContainsKey(path))
            {
                // A validated DuckDB connection owns the native exclusive file handle.
                // Other contexts reuse the engine instance, not a second raw file handle.
            }
        }
        var native = new DuckDBConnection(
            new DuckDBConnectionStringBuilder { DataSource = path }.ConnectionString
        );
        var context = new DuckScanDbContext(DuckScanDbContextFactory.Options(native))
        {
            ReadOnly = readOnly,
        };
        try
        {
            context.Database.OpenConnection();
            if (!exists)
                context.Database.Migrate();
            else if (
                !context
                    .Database.GetAppliedMigrations()
                    .SequenceEqual(context.Database.GetMigrations())
            )
                throw new InvalidDataException(
                    "DuckDB history schema is unsupported. Preserve the file and use a new history directory."
                );
            using (var identity = context.Database.GetDbConnection().CreateCommand())
            {
                identity.CommandText =
                    "SELECT Version FROM something_format WHERE Application='Something';";
                if (Convert.ToInt64(identity.ExecuteScalar()) != 1)
                    throw new InvalidDataException("DuckDB database identity is unsupported.");
            }
            if (budget is not null || !exists)
            {
                // Legacy callers supplied a SQLite page-cache budget. DuckDB needs space
                // for indexes and query workspace across every volume in this shared file.
                ulong bytes = Math.Max(budget ?? 1024UL * 1024 * 1024, 1UL << 30);
                if (
                    bytes is < 64UL * 1024 * 1024 or > 2UL * 1024 * 1024 * 1024
                    || bytes % 16384 != 0
                )
                    throw new ArgumentOutOfRangeException(nameof(budget));
                using var settings = context.Database.GetDbConnection().CreateCommand();
                settings.CommandText =
                    $"SET memory_limit='{bytes / 1048576}MiB'; SET threads=4; SET preserve_insertion_order=false;";
                settings.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested();
            if (!memory)
                Validated.TryAdd(path, 0);
            return context;
        }
        catch
        {
            context.Dispose();
            native.Dispose();
            throw;
        }
    }
}
