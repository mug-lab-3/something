using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

public sealed partial class ScanStore
{
    /// <summary>Exports only to a new owned file, never to an existing history database.
    /// BackupDatabase is synchronous; cancellation is checked before and after the copy.</summary>
    internal long SaveNewSnapshot(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (
            context.Database.CurrentTransaction is not null
            || !context.Scans.Any(s => s.State == StoredScanState.Completed)
            || context.Scans.Any(s => s.State == StoredScanState.Running)
        )
            throw new InvalidOperationException(
                "Snapshot export requires completed, committed scans."
            );
        // Reserve a new file. Do not allow the backup API to overwrite any existing file.
        using (
            var reservation = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )
        ) { }
        try
        {
            using (
                var destination = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false,
                        ForeignKeys = true,
                        DefaultTimeout = 5,
                    }.ToString()
                )
            )
            {
                destination.Open();
                using var settings = destination.CreateCommand();
                settings.CommandText = "PRAGMA synchronous=FULL";
                settings.ExecuteNonQuery();
                token.ThrowIfCancellationRequested();
                ((SqliteConnection)context.Database.GetDbConnection()).BackupDatabase(destination);
            }
            // Report success only after the finished snapshot is flushed to the file system.
            using var snapshot = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );
            snapshot.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            return snapshot.Length;
        }
        catch
        {
            // Only the file successfully reserved above belongs to this operation.
            File.Delete(path);
            File.Delete(path + "-journal");
            throw;
        }
    }
}
