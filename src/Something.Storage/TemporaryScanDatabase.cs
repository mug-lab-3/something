namespace Something.Storage;

/// <summary>A disposable database for one diagnostic volume scan; never opens or replaces saved history.</summary>
public sealed class TemporaryScanDatabase : IDisposable
{
    private readonly string directory;
    private bool disposed;
    public ScanStore Store { get; }

    private TemporaryScanDatabase(string directory, ScanStore store)
    {
        this.directory = directory;
        Store = store;
    }

    public static TemporaryScanDatabase Create(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"something-diagnostic-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            return new(
                directory,
                ScanStore.OpenTrustedPath(
                    Path.Combine(directory, "scan.sqlite3"),
                    cancellationToken
                )
            );
        }
        catch
        {
            DeleteOwnedDirectory(directory);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Store.Dispose();
        DeleteOwnedDirectory(directory);
        disposed = true;
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        string root =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            + Path.DirectorySeparatorChar;
        string owned = Path.GetFullPath(directory);
        if (
            !owned.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(owned)
                .StartsWith("something-diagnostic-", StringComparison.Ordinal)
            || (File.GetAttributes(owned) & FileAttributes.ReparsePoint) != 0
        )
            throw new InvalidOperationException("Invalid diagnostic database cleanup path.");
        // Only files explicitly created for this database; no recursive deletion or inspected paths.
        foreach (
            string fileName in new[]
            {
                "scan.sqlite3",
                "scan.sqlite3-wal",
                "scan.sqlite3-shm",
                "scan.sqlite3-journal",
            }
        )
            File.Delete(Path.Combine(owned, fileName));
        Directory.Delete(owned);
    }
}
