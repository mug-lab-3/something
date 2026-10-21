using Something.Application.History;
using Something.Application.Repositories;
using Something.Application.Scanning;

namespace Something.Storage;

internal sealed class TemporaryScanRepository : ITemporaryScanRepository
{
    public static TemporaryScanRepository Instance { get; } = new();

    public ITemporaryScanDatabase Create(
        bool inMemory,
        ulong? fileCacheBytes,
        CancellationToken token
    ) =>
        new Database(
            TemporaryScanDatabase.Create(
                token,
                inMemory,
                !inMemory,
                inMemory ? null : fileCacheBytes
            )
        );

    private sealed class Database(TemporaryScanDatabase database) : ITemporaryScanDatabase
    {
        public Guid Publish(
            CompletedScan scan,
            DateTimeOffset endedUtc,
            DatabaseMemoryGrowth? growth,
            Action<ScanWorkProgress>? progress,
            CancellationToken token
        ) =>
            database.Store.PublishBufferedScan(
                scan,
                endedUtc,
                token,
                progress,
                growth?.TryGrow,
                growth?.Account
            );

        public void ExportSnapshot(CancellationToken token) => database.ExportSnapshot(token);

        public void SaveTimings(Guid scanId, ScanTimingSnapshot timing, CancellationToken token) =>
            database.Store.SaveTimings(scanId, timing, token);

        public IScanSnapshot Open(Guid scanId) =>
            new StoreScanSnapshot(database, database.Store, scanId);

        public void Dispose() => database.Dispose();
    }
}
