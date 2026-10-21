using Something.Application.History;
using Something.Application.Scanning;

namespace Something.Application.Repositories;

/// <summary>Private databases for scans that are not kept in the history, such as demos and
/// diagnostics. They never open or replace the history.</summary>
public interface ITemporaryScanRepository
{
    /// <summary>A database in memory, or in a temporary file limited to
    /// <paramref name="fileCacheBytes"/>; it is deleted when disposed.</summary>
    ITemporaryScanDatabase Create(bool inMemory, ulong? fileCacheBytes, CancellationToken token);
}

/// <summary>Methods run on the calling thread.</summary>
public interface ITemporaryScanDatabase : IDisposable
{
    Guid Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        DatabaseMemoryGrowth? growth,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    );

    /// <summary>Copies an in-memory database to its temporary file, so that browsing does not
    /// hold the whole scan in memory.</summary>
    void ExportSnapshot(CancellationToken token);

    void SaveTimings(Guid scanId, ScanTimingSnapshot timing, CancellationToken token);

    /// <summary>Opens a published scan. The snapshot takes over this database: dispose the
    /// snapshot instead of the database.</summary>
    IScanSnapshot Open(Guid scanId);
}
