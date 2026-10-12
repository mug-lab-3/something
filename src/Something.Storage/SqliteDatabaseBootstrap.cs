using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

// Raw commands are limited to SQLite identity, schema introspection and connection PRAGMAs.
// Application data is handled by the EF model and LINQ in ScanStore.
internal static class SqliteDatabaseBootstrap
{
    internal const int ApplicationId = 0x534F4D45;
    internal const int Version = 1;

    internal static ScanDbContext Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            }.ToString()
        );
        ScanDbContext? context = null;
        try
        {
            connection.Open();
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
            command.CommandText = "PRAGMA journal_mode=WAL";
            if (
                !string.Equals(
                    command.ExecuteScalar()?.ToString(),
                    "wal",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidDataException("Database could not enable WAL mode.");
            Execute(connection, "PRAGMA synchronous=FULL");
            return context;
        }
        catch
        {
            context?.Dispose();
            connection.Dispose();
            throw;
        }
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
