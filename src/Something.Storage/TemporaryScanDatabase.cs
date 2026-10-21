namespace Something.Storage;

/// <summary>A disposable database for one diagnostic volume scan; never opens or replaces saved history.</summary>
public sealed class TemporaryScanDatabase : IDisposable
{
    private readonly string directory;
    private bool disposed;
    private bool exported;
    public ScanStore Store { get; }
    public bool IsInMemory { get; }
    private readonly bool sqlite;
    public string SnapshotPath => Path.Combine(directory, sqlite ? "scan.sqlite3" : "scan.duckdb");

    private TemporaryScanDatabase(
        string directory,
        ScanStore store,
        bool inMemory,
        bool sqlite = false
    )
    {
        this.directory = directory;
        Store = store;
        IsInMemory = inMemory;
        this.sqlite = sqlite;
    }

    public static TemporaryScanDatabase Create(
        CancellationToken cancellationToken = default,
        bool inMemory = false,
        bool boundedMemory = false,
        ulong? fileCacheBytes = null
    ) => CreateCore(cancellationToken, inMemory, boundedMemory, fileCacheBytes, false);

    internal static TemporaryScanDatabase CreateSqlite(
        CancellationToken cancellationToken = default,
        bool inMemory = false,
        bool boundedMemory = false,
        ulong? fileCacheBytes = null
    ) => CreateCore(cancellationToken, inMemory, boundedMemory, fileCacheBytes, true);

    private static TemporaryScanDatabase CreateCore(
        CancellationToken cancellationToken,
        bool inMemory,
        bool boundedMemory,
        ulong? fileCacheBytes,
        bool sqlite
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inMemory && boundedMemory)
            throw new ArgumentException(
                "A bounded file cache cannot be applied to a memory database."
            );
        if (fileCacheBytes is not null && !boundedMemory)
            throw new ArgumentException(
                "A cache budget requires a bounded file database.",
                nameof(fileCacheBytes)
            );
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"something-diagnostic-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            return new(
                directory,
                !sqlite
                    ? inMemory
                        ? ScanStore.OpenDuckMemory(cancellationToken)
                        : ScanStore.OpenDuckDiagnostic(
                            Path.Combine(directory, "scan.duckdb"),
                            cancellationToken,
                            fileCacheBytes
                        )
                    : inMemory
                        ? ScanStore.OpenInMemory(cancellationToken)
                        : ScanStore.OpenDiagnosticFile(
                            Path.Combine(directory, "scan.sqlite3"),
                            cancellationToken,
                            boundedMemory,
                            fileCacheBytes
                        ),
                inMemory,
                sqlite
            );
        }
        catch
        {
            DeleteOwnedDirectory(directory);
            throw;
        }
    }

    /// <summary>Copies a completed private memory database to its owned temporary file.
    /// The file is removed on Dispose; this is not retained history.</summary>
    public long ExportSnapshot(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsInMemory)
            throw new InvalidOperationException(
                "Only an in-memory diagnostic database needs export."
            );
        long bytes = Store.SaveNewSnapshot(SnapshotPath, token);
        exported = true;
        return bytes;
    }

    /// <summary>Caller owns the returned store. Used to verify the completed file export.</summary>
    public ScanStore OpenSnapshot(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!exported)
            throw new InvalidOperationException("The memory snapshot has not been exported.");
        return sqlite
            ? ScanStore.OpenTrustedPath(SnapshotPath, token)
            : ScanStore.OpenDuckPath(SnapshotPath, token, readOnly: true);
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
                "scan.duckdb",
                "scan.duckdb.wal",
            }
        )
            File.Delete(Path.Combine(owned, fileName));
        Directory.Delete(owned);
    }
}
