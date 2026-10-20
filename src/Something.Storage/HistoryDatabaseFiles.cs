using System.Buffers;

namespace Something.Storage;

public sealed record HistoryDatabaseInfo(string Path, long DatabaseBytes, long WalBytes)
{
    public long TotalBytes => checked(DatabaseBytes + WalBytes);
}

public sealed partial class ScanHistoryArchive
{
    private static readonly object LocationOperations = new();
    private bool HasDatabaseFiles =>
        new[] { DatabasePath, CompactionBackup, DatabasePath + ".wal" }.Any(path =>
            File.Exists(path) || Directory.Exists(path)
        );

    public HistoryDatabaseInfo ReadDatabaseInfo() =>
        new(DatabasePath, Length(DatabasePath), Length(DatabasePath + ".wal"));

    private static long Length(string path)
    {
        try
        {
            RequireOrdinaryFile(path);
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private void RequireIdle()
    {
        DuckDatabaseBootstrap.RequireClosed(DatabasePath);
        if (readers.Count != 0)
            throw new InvalidOperationException(
                "データベースを使用中です。履歴の表示が終わってから操作してください。"
            );
    }

    private void RequireDatabaseFiles()
    {
        foreach (string path in new[] { DatabasePath, CompactionBackup, DatabasePath + ".wal" })
            if (File.Exists(path) || Directory.Exists(path))
                RequireOrdinaryFile(path);
        if (
            !File.Exists(DatabasePath)
            && !File.Exists(CompactionBackup)
            && File.Exists(DatabasePath + ".wal")
        )
            throw new InvalidDataException(
                "データベース本体がないため、残っているWALを保持して操作を中止しました。"
            );
    }

    /// <summary>Deletes only this catalog after recovering/checkpointing it and verifying its identity.</summary>
    public void DeleteDatabase(CancellationToken token = default)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            RequireIdle();
            RequireDatabaseFiles();
            if (!HasDatabaseFiles)
                return;
            Checkpoint(token);
            RequireIdle();
            RequireOrdinaryFile(DatabasePath);
            if (File.Exists(DatabasePath + ".wal"))
                throw new IOException("WALを確定できないため、データベースを削除できません。");
            token.ThrowIfCancellationRequested();
            File.Delete(DatabasePath);
            DuckDatabaseBootstrap.ForgetValidation(DatabasePath);
        }
    }

    /// <summary>Copy/validate without overwrite, persist the new location, then remove the source.
    /// The callback must atomically save settings only. Failure before it succeeds preserves the source.</summary>
    public ScanHistoryArchive Relocate(
        string directory,
        Action<string> saveLocation,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(saveLocation);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException(
                "データベースの保存先は絶対パスで指定してください。",
                nameof(directory)
            );
        var target = new ScanHistoryArchive(directory, clock, timeZone);
        lock (LocationOperations)
            lock (sync)
                lock (target.sync)
                {
                    token.ThrowIfCancellationRequested();
                    RequireIdle();
                    RequireDatabaseFiles();
                    if (
                        string.Equals(
                            Path.TrimEndingDirectorySeparator(root),
                            Path.TrimEndingDirectorySeparator(target.root),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        return this;
                    target.RequireIdle();
                    Directory.CreateDirectory(target.root);
                    if ((File.GetAttributes(target.root) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(
                            "データベースの保存先にリンクフォルダーは指定できません。"
                        );
                    foreach (
                        string path in new[]
                        {
                            target.DatabasePath,
                            target.DatabasePath + ".wal",
                            target.CompactionBackup,
                        }
                    )
                        if (File.Exists(path) || Directory.Exists(path))
                            throw new IOException(
                                "移動先にデータベースまたは復旧用ファイルがあるため、上書きしません。"
                            );
                    if (!HasDatabaseFiles)
                    {
                        saveLocation(target.root);
                        return target;
                    }
                    Checkpoint(token);
                    RequireIdle();
                    if (File.Exists(DatabasePath + ".wal"))
                        throw new IOException(
                            "WALを確定できないため、データベースを移動できません。"
                        );
                    string temporary = Path.Combine(
                        target.root,
                        $".moving-{Guid.NewGuid():N}.duckdb"
                    );
                    bool installed = false;
                    bool committed = false;
                    try
                    {
                        CopyCatalog(temporary, token);
                        using (
                            DuckDatabaseBootstrap.Open(
                                temporary,
                                token,
                                readOnly: true,
                                sharedHistory: true
                            )
                        ) { }
                        DuckDatabaseBootstrap.ForgetValidation(temporary);
                        token.ThrowIfCancellationRequested();
                        File.Move(temporary, target.DatabasePath);
                        installed = true;
                        saveLocation(target.root);
                        committed = true;
                        // Once settings point to the verified copy, cancellation must not undo publication.
                        // If source cleanup fails, both copies remain and the destination is still usable.
                        try
                        {
                            File.Delete(DatabasePath);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        DuckDatabaseBootstrap.ForgetValidation(DatabasePath);
                        return target;
                    }
                    finally
                    {
                        DuckDatabaseBootstrap.ForgetValidation(temporary);
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                        if (installed && !committed)
                            File.Delete(target.DatabasePath);
                    }
                }
    }

    private void CopyCatalog(string path, CancellationToken token)
    {
        RequireOrdinaryFile(DatabasePath);
        using var source = new FileStream(
            DatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
        );
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            destination.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
