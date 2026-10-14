using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

internal enum SqliteDatabaseProfile
{
    History,
    DiagnosticFile,
    DiagnosticMemory,
    DiagnosticBoundedFile,
}

// Raw commands are limited to SQLite identity, schema introspection and connection PRAGMAs.
// Application queries and updates use EF; prepared append inserts also derive from its model.
internal static class SqliteDatabaseBootstrap
{
    internal static ScanDbContext OpenSnapshotReadOnly(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString()
        );
        ScanDbContext? context = null;
        try
        {
            connection.Open();
            if (
                ScalarLong(connection, "PRAGMA application_id") != ApplicationId
                || ScalarLong(connection, "PRAGMA user_version") != Version
            )
                throw new InvalidDataException("Snapshot database identity is unsupported.");
            context = new(
                new DbContextOptionsBuilder<ScanDbContext>()
                    .UseSqlite(connection, contextOwnsConnection: true)
                    .Options
            );
            var applied = context.Database.GetAppliedMigrations().ToArray();
            var known = context.Database.GetMigrations().ToArray();
            if (!applied.SequenceEqual(known))
                throw new InvalidDataException("Snapshot database schema is unsupported.");
            token.ThrowIfCancellationRequested();
            return context;
        }
        catch
        {
            context?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    internal const int ApplicationId = 0x534F4D45;
    internal const int Version = 1;

    internal static ScanDbContext Open(
        string path,
        CancellationToken cancellationToken,
        SqliteDatabaseProfile profile = SqliteDatabaseProfile.History,
        ulong? fileCacheBytes = null
    )
    {
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (
            fileCacheBytes is not null
            && profile
                is not (
                    SqliteDatabaseProfile.DiagnosticBoundedFile
                    or SqliteDatabaseProfile.History
                )
        )
            throw new ArgumentException(
                "A cache budget requires the bounded file profile.",
                nameof(fileCacheBytes)
            );
        ulong cacheBytes = fileCacheBytes ?? 64UL * 1024 * 1024;
        if (
            cacheBytes is < 64UL * 1024 * 1024 or > 2UL * 1024 * 1024 * 1024
            || cacheBytes % 16384 != 0
        )
            throw new ArgumentOutOfRangeException(nameof(fileCacheBytes));
        bool memory = profile == SqliteDatabaseProfile.DiagnosticMemory;
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = memory ? ":memory:" : Path.GetFullPath(path),
                Mode = memory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            }.ToString()
        );
        ScanDbContext? context = null;
        try
        {
            connection.Open();
            if (profile != SqliteDatabaseProfile.History)
            {
                Execute(connection, "PRAGMA page_size=16384");
                Execute(
                    connection,
                    profile == SqliteDatabaseProfile.DiagnosticBoundedFile
                        ? "PRAGMA temp_store=FILE"
                        : "PRAGMA temp_store=MEMORY"
                );
            }
            if (profile == SqliteDatabaseProfile.History && fileCacheBytes is not null)
            {
                Execute(
                    connection,
                    $"PRAGMA cache_size=-{(cacheBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                );
            }
            if (profile == SqliteDatabaseProfile.DiagnosticBoundedFile)
            {
                long cacheKiB = checked((long)(cacheBytes / 1024));
                Execute(
                    connection,
                    $"PRAGMA cache_size=-{cacheKiB.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                );
                Execute(connection, "PRAGMA cache_spill=ON");
                Execute(connection, "PRAGMA locking_mode=EXCLUSIVE");
            }
            if (profile == SqliteDatabaseProfile.DiagnosticFile)
            {
                Execute(connection, "PRAGMA cache_spill=OFF");
                Execute(connection, "PRAGMA locking_mode=EXCLUSIVE");
            }
            long application = ScalarLong(connection, "PRAGMA application_id");
            long version = ScalarLong(connection, "PRAGMA user_version");
            bool empty = application == 0 && version == 0;
            if (empty)
            {
                if (
                    ScalarLong(
                        connection,
                        "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'"
                    ) != 0
                )
                    throw new InvalidDataException("Database is not an empty Something database.");
            }
            else if (application != ApplicationId || version != Version)
                throw new InvalidDataException(
                    "Database identity or schema version is unsupported."
                );

            context = new(
                new DbContextOptionsBuilder<ScanDbContext>()
                    .UseSqlite(connection, contextOwnsConnection: true)
                    .Options
            );
            if (!empty)
            {
                var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                var applied = context.Database.GetAppliedMigrations().ToArray();
                if (applied.Length == 0 || applied.Any(m => !known.Contains(m)))
                    throw new InvalidDataException("Database migration history is unsupported.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            context.Database.Migrate();
            using var command = connection.CreateCommand();
            string journal = profile switch
            {
                SqliteDatabaseProfile.History => "wal",
                SqliteDatabaseProfile.DiagnosticFile
                or SqliteDatabaseProfile.DiagnosticBoundedFile => "delete",
                _ => "memory",
            };
            command.CommandText = $"PRAGMA journal_mode={journal}";
            if (
                !string.Equals(
                    command.ExecuteScalar()?.ToString(),
                    journal,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidDataException(
                    "Database could not enable the requested journal mode."
                );
            Execute(connection, "PRAGMA synchronous=FULL");
            if (!empty)
                OptimizeForBrowsing(context, cancellationToken);
            return context;
        }
        catch
        {
            context?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Refresh bounded planner statistics after bulk loading, before readers or export.
    /// Without statistics SQLite may start folder joins from every observation in a scan,
    /// rather than the selective parent-name index. This only touches our owned database.</summary>
    internal static void OptimizeForBrowsing(ScanDbContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 0x10000 examines all tables even when this connection has no query history.
        // SQLite 3.46+ bounds the ANALYZE work performed by optimize automatically.
        context.Database.ExecuteSqlRaw("PRAGMA optimize=0x10002");
        token.ThrowIfCancellationRequested();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
