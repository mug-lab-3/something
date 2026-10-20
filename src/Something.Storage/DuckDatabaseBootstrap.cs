using System.Collections.Concurrent;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

internal static class DuckDatabaseBootstrap
{
    // Native memory limits are signed byte counts. This is a representation bound,
    // not an application RAM budget; budgets come from current OS availability.
    internal const ulong MaximumMemoryLimit = (ulong)long.MaxValue / 16384 * 16384;

    private sealed class EngineMemory(ulong limit)
    {
        internal ulong Limit = limit;
        internal int Connections;
        internal bool Spilling;
    }

    // Last-resort spill area beside the database (or its private memory key).
    // Memory stays the normal path; this directory exists only after a memory fallback.
    internal static string SpillDirectory(string key) => key + ".spill";

    private static readonly Dictionary<string, EngineMemory> Engines = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly ConcurrentDictionary<string, byte> Validated = new(
        StringComparer.OrdinalIgnoreCase
    );

    internal static object Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new());

    // Call while holding Gate(path); every managed opener uses the same gate.
    internal static void RequireClosed(string path)
    {
        lock (Engines)
            if (Engines.ContainsKey(Path.GetFullPath(path)))
                throw new InvalidOperationException(
                    "データベースを使用中です。履歴の表示が終わってから最適化してください。"
                );
    }

    internal static void ForgetValidation(string path) =>
        Validated.TryRemove(Path.GetFullPath(path), out _);

    internal static ScanDbContext Open(
        string path,
        CancellationToken token,
        ulong? budget = null,
        bool readOnly = false,
        bool sharedHistory = false
    )
    {
        if (path != ":memory:")
            lock (Gate(path))
                return OpenCore(path, token, budget, readOnly, sharedHistory);
        return OpenCore(path, token, budget, readOnly, sharedHistory);
    }

    private static ScanDbContext OpenCore(
        string path,
        CancellationToken token,
        ulong? budget,
        bool readOnly,
        bool sharedHistory
    )
    {
        token.ThrowIfCancellationRequested();
        if (
            budget is { } requested
            && (requested is < 16384 or > MaximumMemoryLimit || requested % 16384 != 0)
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
        DuckScanDbContext context = sharedHistory
            ? new SharedHistoryDbContext(SharedHistoryDbContextFactory.Options(native))
            {
                ReadOnly = readOnly,
            }
            : new DuckScanDbContext(DuckScanDbContextFactory.Options(native))
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
                if (Convert.ToInt64(identity.ExecuteScalar()) != (sharedHistory ? 6 : 3))
                    throw new InvalidDataException("DuckDB database identity is unsupported.");
            }
            RegisterMemory(context, path, budget);
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

    // Connections to one file share a native engine. Keep its limit while any of our
    // contexts remain open; release the remembered budget when the last one closes.
    private static void RegisterMemory(DuckScanDbContext context, string path, ulong? budget)
    {
        string key =
            path == ":memory:"
                ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
                : path;
        lock (Gate(key))
        {
            lock (Engines)
            {
                bool known = Engines.TryGetValue(key, out var state);
                if (!known)
                    DeleteSpill(key); // Left by an earlier process that ended abnormally.
                state ??= new(Math.Max(budget ?? 1UL << 30, 1UL << 30));
                ulong desired = Math.Max(state.Limit, budget ?? state.Limit);
                if (!known || desired > state.Limit)
                    SetMemoryLimit(context, desired, state.Spilling);
                state.Limit = desired;
                state.Connections++;
                Engines[key] = state;
            }
            context.ReleaseEngine = () =>
            {
                lock (Gate(key))
                    lock (Engines)
                        if (--Engines[key].Connections == 0)
                        {
                            Engines.Remove(key);
                            DeleteSpill(key);
                        }
            };
            context.MemoryKey = key;
        }
    }

    internal static ulong ReadMemoryLimit(DuckScanDbContext context)
    {
        lock (Gate(context.MemoryKey))
            lock (Engines)
                return Engines[context.MemoryKey].Limit;
    }

    internal static void IncreaseMemoryLimit(DuckScanDbContext context, ulong bytes)
    {
        if (bytes is < 16384 or > MaximumMemoryLimit || bytes % 16384 != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (Gate(context.MemoryKey))
            lock (Engines)
            {
                var state = Engines[context.MemoryKey];
                if (bytes <= state.Limit)
                    throw new ArgumentOutOfRangeException(nameof(bytes));
                SetMemoryLimit(context, bytes, state.Spilling);
                state.Limit = bytes;
            }
    }

    internal static bool IsSpilling(DuckScanDbContext context)
    {
        lock (Gate(context.MemoryKey))
            lock (Engines)
                return Engines[context.MemoryKey].Spilling;
    }

    /// <summary>Lets the engine offload blocks to disk for the rest of its lifetime.
    /// Used only after memory-only attempts failed; the directory is removed when the engine closes.</summary>
    internal static void EnableSpill(DuckScanDbContext context)
    {
        lock (Gate(context.MemoryKey))
            lock (Engines)
            {
                var state = Engines[context.MemoryKey];
                if (state.Spilling)
                    return;
                using (var current = context.Database.GetDbConnection().CreateCommand())
                {
                    // Diagnostic file databases keep DuckDB's default spill directory.
                    current.CommandText = "SELECT current_setting('temp_directory');";
                    if (current.ExecuteScalar() is string { Length: > 0 })
                    {
                        state.Spilling = true;
                        return;
                    }
                }
                string directory = SpillDirectory(context.MemoryKey);
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        "Spill directories must not be reparse points."
                    );
                using var settings = context.Database.GetDbConnection().CreateCommand();
                settings.CommandText = $"SET temp_directory='{directory.Replace("'", "''")}';";
                settings.ExecuteNonQuery();
                state.Spilling = true;
            }
    }

    private static void DeleteSpill(string key)
    {
        string directory = SpillDirectory(key);
        try
        {
            if (
                Directory.Exists(directory)
                && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0
            )
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SetMemoryLimit(DuckScanDbContext context, ulong bytes, bool spilling)
    {
        // Shared history keeps insertion order: only then does DuckDB write completed row groups
        // of an uncommitted append to the database file (measured 54 MiB vs 453 MiB resident for
        // 3 million rows). Publication appends; it no longer bulk-copies with INSERT ... SELECT.
        bool shared = context is SharedHistoryDbContext;
        using var settings = context.Database.GetDbConnection().CreateCommand();
        settings.CommandText =
            $"SET memory_limit='{bytes / 16384 * 16}KiB'; SET threads=4; SET preserve_insertion_order={(shared ? "true" : "false")};"
            + (shared && !spilling ? " SET temp_directory='';" : "");
        settings.ExecuteNonQuery();
    }
}
