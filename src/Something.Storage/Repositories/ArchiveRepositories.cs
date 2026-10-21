using System.Collections.Immutable;
using Something.Application.Comparison;
using Something.Application.History;
using Something.Application.Repositories;
using Something.Application.Scanning;
using Something.Application.Search;

namespace Something.Storage;

/// <summary>The repositories over one <see cref="ScanHistoryArchive"/>, which serializes its own
/// database operations.</summary>
internal sealed class ArchiveRepositories(ScanHistoryArchive archive)
    : IScanHistoryRepository,
        IFolderChangeRepository,
        IHistorySearchRepository,
        IHistoryDatabase
{
    /// <summary>The history in <paramref name="directory"/>, or in the default location.</summary>
    public static HistoryRepositories Open(string? directory) =>
        For(directory is null ? ScanHistoryArchive.CreateDefault() : new(directory));

    public static HistoryRepositories For(ScanHistoryArchive archive)
    {
        var repositories = new ArchiveRepositories(archive);
        return new(repositories, repositories, repositories, repositories);
    }

    public ScanHistoryEntry[] List(string volumeKey, CancellationToken token) =>
        archive.List(volumeKey, token);

    public ScanHistorySummary[] ListSummaries(string volumeKey, CancellationToken token) =>
        archive.ListSummaries(volumeKey, token);

    public IReadOnlyDictionary<Guid, long> ReadRecordCounts(
        IReadOnlyCollection<Guid> scanIds,
        CancellationToken token
    ) => archive.ReadRecordCounts(scanIds, token);

    public ScanHistoryEntry Publish(
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        string drive,
        TimeSpan elapsed,
        ulong memoryLimitBytes,
        DatabaseMemoryGrowth? growth,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    ) =>
        archive.Publish(
            buffer,
            endedUtc,
            drive,
            elapsed,
            memoryLimitBytes,
            token,
            progress,
            growth?.TryGrow,
            growth?.Account
        );

    public ScanHistoryEntry Save(
        IScanSnapshot source,
        string drive,
        TimeSpan elapsed,
        CancellationToken token
    ) =>
        source is StoreScanSnapshot stored
            ? archive.Save(stored.Store, stored.ScanId, drive, elapsed, token)
            : throw new ArgumentException(
                "The scan belongs to another storage implementation.",
                nameof(source)
            );

    public void SaveTimings(Guid scanId, ScanTimingSnapshot timing, CancellationToken token) =>
        archive.SaveTimings(scanId, timing, token);

    public IHistorySnapshot Open(Guid id, CancellationToken token) =>
        new ArchiveScanSnapshot(archive.Open(id, token));

    public Guid[] Delete(IReadOnlyCollection<Guid> ids, CancellationToken token) =>
        archive.Delete(ids, token);

    public int CollectRetired(CancellationToken token) => archive.CollectRetired(token);

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

    public HistorySearchResult Search(
        string volumeKey,
        HistorySearchCriteria criteria,
        ulong memoryLimitBytes,
        DatabaseMemoryGrowth? growth,
        CancellationToken token
    ) =>
        archive.Search(
            volumeKey,
            criteria,
            token,
            memoryLimitBytes,
            growth?.TryGrow,
            growth?.Account
        );

    public ImmutableArray<SearchSizeObservation> ReadSeries(
        HistorySearchResult result,
        SearchItem item,
        CancellationToken token
    ) => archive.ReadSearchSeries(result, item, token);

    public string DatabasePath => archive.DatabasePath;
    public string DirectoryPath => archive.DirectoryPath;
    public string? MaintenanceError => archive.MaintenanceError;

    public HistoryDatabaseInfo ReadInfo() => archive.ReadDatabaseInfo();

    public void Checkpoint(CancellationToken token) => archive.Checkpoint(token);

    public HistoryCompactionResult Compact(
        ulong memoryLimitBytes,
        Action<HistoryCompactionStage>? progress,
        CancellationToken token
    ) => archive.Compact(memoryLimitBytes, token, progress);

    public HistoryRepositories Relocate(
        string directory,
        Action<string> saveLocation,
        CancellationToken token
    ) => For(archive.Relocate(directory, saveLocation, token));

    public void DeleteDatabase(CancellationToken token) => archive.DeleteDatabase(token);
}
