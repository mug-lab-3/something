using Something.Storage;

namespace Something.Application;

/// <summary>The scan history of every drive, for the user interface. Runs database work off the
/// calling thread and owns the database location: after a move, scans, comparisons, searches
/// and sessions opened later use the new location without being recreated.</summary>
public sealed class HistoryService
{
    private ScanHistoryArchive archive;

    private HistoryService(ScanHistoryArchive archive) => this.archive = archive;

    /// <summary>The history in <paramref name="directory"/>, or in the default location.</summary>
    public static HistoryService Open(string? directory = null) =>
        new(directory is null ? ScanHistoryArchive.CreateDefault() : new(directory));

    /// <summary>For tests and tools that build their archive with a fixed clock or time zone.</summary>
    internal static HistoryService ForArchive(ScanHistoryArchive archive) => new(archive);

    /// <summary>The archive current at the time of the call; callers never keep it.</summary>
    internal ScanHistoryArchive Archive => archive;

    public string DatabasePath => archive.DatabasePath;
    public string DirectoryPath => archive.DirectoryPath;

    /// <summary>Why the last pruning after a publication failed, if it did.</summary>
    public string? MaintenanceError => archive.MaintenanceError;

    /// <summary>File sizes only; cheap enough for the calling thread.</summary>
    public HistoryDatabaseInfo ReadDatabaseInfo() => archive.ReadDatabaseInfo();

    /// <summary>Retained scans of one volume, newest first.</summary>
    public Task<ScanHistoryEntry[]> ListAsync(string volumeKey, CancellationToken token) =>
        Run(history => history.List(volumeKey, token), token);

    public Task<ScanHistorySummary[]> ListSummariesAsync(
        string volumeKey,
        CancellationToken token
    ) => Run(history => history.ListSummaries(volumeKey, token), token);

    public Task<IReadOnlyDictionary<Guid, long>> ReadRecordCountsAsync(
        IReadOnlyCollection<Guid> scanIds,
        CancellationToken token
    ) => Run(history => history.ReadRecordCounts(scanIds, token), token);

    /// <summary>Opens one retained scan for browsing; it stays protected until disposed.</summary>
    public Task<ScanSession> OpenAsync(Guid id, string drive, CancellationToken token) =>
        Run(history => ScanSession.FromHistory(history.Open(id, token), drive, token), token);

    /// <summary>Retires the confirmed scans and returns those removed. Scans still open are
    /// collected later; see <see cref="CollectRetiredAsync"/>.</summary>
    public Task<Guid[]> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken token) =>
        Run(history => history.Delete(ids, token), token);

    /// <summary>Collects retired scans that are no longer open; returns how many remain open.</summary>
    public Task<int> CollectRetiredAsync(CancellationToken token) =>
        Run(history => history.CollectRetired(token), token);

    public Task CheckpointAsync(CancellationToken token) =>
        Run(history => history.Checkpoint(token), token);

    /// <summary>Rewrites the database compactly. Callers close every session first. Optimization
    /// runs alone, so it may use three quarters of the headroom instead of a shared budget.</summary>
    public Task<HistoryCompactionResult> OptimizeAsync(
        Action<HistoryCompactionStage>? progress,
        CancellationToken token
    )
    {
        ulong available = ScanMemoryBudget.ReadSystem() is { } memory
            ? MemoryHeadroom.Headroom(memory)
            : checked(
                (ulong)
                    Math.Max(
                        0,
                        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
                            - GC.GetGCMemoryInfo().MemoryLoadBytes
                    )
            );
        ulong limit = MemoryHeadroom.Align(available / 4 * 3);
        if (limit < MemoryHeadroom.Granularity)
            throw new InsufficientMemoryException();
        return Run(history => history.Compact(limit, token, progress), token);
    }

    /// <summary>Moves every drive's history to <paramref name="directory"/>. Callers close every
    /// session first. <paramref name="saveLocation"/> records the new location before the old
    /// database is deleted.</summary>
    public async Task MoveAsync(
        string directory,
        Action<string> saveLocation,
        CancellationToken token
    ) => archive = await Run(history => history.Relocate(directory, saveLocation, token), token);

    /// <summary>Deletes the current database only. Callers close every session first.</summary>
    public Task DeleteDatabaseAsync(CancellationToken token) =>
        Run(history => history.DeleteDatabase(token), token);

    private Task<T> Run<T>(Func<ScanHistoryArchive, T> work, CancellationToken token)
    {
        var current = archive;
        return Task.Run(() => work(current), token);
    }

    private Task Run(Action<ScanHistoryArchive> work, CancellationToken token)
    {
        var current = archive;
        return Task.Run(() => work(current), token);
    }
}
