using Something.Application.Comparison;
using Something.Application.History;
using Something.Application.Memory;
using Something.Application.Repositories;
using Something.Application.Scanning;
using Something.Application.Search;

namespace Something.Storage;

/// <summary>Builds the repositories over one <see cref="ScanHistoryArchive"/>, which serializes
/// its own database operations.</summary>
internal static class ArchiveRepositories
{
    /// <summary>The history in <paramref name="directory"/>, or in the default location.</summary>
    public static HistoryRepositories Open(string? directory) =>
        For(directory is null ? ScanHistoryArchive.CreateDefault() : new(directory));

    public static HistoryRepositories For(ScanHistoryArchive archive) =>
        new(
            new ArchiveScanHistory(archive),
            new ArchiveScanPublisher(archive),
            new ArchiveFolderChanges(archive),
            new ArchiveHistorySearches(archive),
            new ArchiveDatabase(archive)
        );
}

internal sealed class ArchiveScanHistory(ScanHistoryArchive archive) : IScanHistoryRepository
{
    public ScanHistoryEntry[] List(string volumeKey, CancellationToken token) =>
        archive.List(volumeKey, token);

    public ScanHistorySummary[] ListSummaries(string volumeKey, CancellationToken token) =>
        archive.ListSummaries(volumeKey, token);

    public IReadOnlyDictionary<Guid, long> ReadRecordCounts(
        IReadOnlyCollection<Guid> scanIds,
        CancellationToken token
    ) => archive.ReadRecordCounts(scanIds, token);

    public IHistorySnapshot Open(Guid id, CancellationToken token) =>
        new ArchiveScanSnapshot(archive, archive.Open(id, token));

    public Guid[] Delete(IReadOnlyCollection<Guid> ids, CancellationToken token) =>
        archive.Delete(ids, token);

    public int CollectRetired(CancellationToken token) => archive.CollectRetired(token);
}

internal sealed class ArchiveScanPublisher(ScanHistoryArchive archive) : IScanPublisher
{
    public IHistorySnapshot Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        string drive,
        TimeSpan elapsed,
        IMemoryAllowance allowance,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    )
    {
        var (limit, grow, account) = DuckMemoryPolicy.Acquire(allowance);
        return new ArchiveScanSnapshot(
            archive,
            archive.PublishAndOpen(
                scan,
                endedUtc,
                drive,
                elapsed,
                limit,
                token,
                progress,
                grow,
                account
            )
        );
    }
}

internal sealed class ArchiveFolderChanges(ScanHistoryArchive archive) : IFolderChangeRepository
{
    public FolderChangeReport ReadFolderChanges(
        string volumeKey,
        Guid beforeId,
        Guid afterId,
        CancellationToken token
    ) => archive.ReadFolderChanges(volumeKey, beforeId, afterId, token);

    public FolderChangeReport ReadFolderChanges(
        string volumeKey,
        FolderChangePeriod period,
        DateTimeOffset? asOfUtc,
        CancellationToken token
    ) => archive.ReadFolderChanges(volumeKey, period, asOfUtc, token);
}

internal sealed class ArchiveHistorySearches(ScanHistoryArchive archive) : IHistorySearchRepository
{
    public IHistorySearchSession Search(
        string volumeKey,
        HistorySearchCriteria criteria,
        IMemoryAllowance allowance,
        CancellationToken token
    )
    {
        var (limit, grow, account) = DuckMemoryPolicy.Acquire(allowance);
        return archive.Search(volumeKey, criteria, token, limit, grow, account);
    }
}

internal sealed class ArchiveDatabase(ScanHistoryArchive archive) : IHistoryDatabase
{
    public string DatabasePath => archive.DatabasePath;
    public string DirectoryPath => archive.DirectoryPath;
    public string? MaintenanceError => archive.MaintenanceError;

    public HistoryDatabaseInfo ReadInfo() => archive.ReadDatabaseInfo();

    public void Checkpoint(CancellationToken token) => archive.Checkpoint(token);

    public HistoryCompactionResult Compact(
        IMemoryAllowance allowance,
        Action<HistoryCompactionStage>? progress,
        CancellationToken token
    )
    {
        // Compaction runs alone, so it may use three quarters of the headroom.
        ulong limit = allowance.Charge<ulong>(available =>
            new(
                DuckMemoryPolicy.Align(
                    (available is null ? FreeManagedMemory() : MemoryHeadroom.Headroom(available))
                        / 4
                        * 3
                ),
                0
            )
        );
        if (limit < DuckMemoryPolicy.Granularity)
            throw new InsufficientMemoryException();
        return archive.Compact(limit, token, progress);
    }

    private static ulong FreeManagedMemory()
    {
        var info = GC.GetGCMemoryInfo();
        return (ulong)Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
    }

    public HistoryRepositories Relocate(
        string directory,
        Action<string> saveLocation,
        CancellationToken token
    ) => ArchiveRepositories.For(archive.Relocate(directory, saveLocation, token));

    public void DeleteDatabase(CancellationToken token) => archive.DeleteDatabase(token);
}
