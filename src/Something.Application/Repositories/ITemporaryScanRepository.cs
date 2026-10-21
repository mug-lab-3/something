using Something.Application.History;
using Something.Application.Memory;
using Something.Application.Scanning;

namespace Something.Application.Repositories;

/// <summary>Private databases for scans that are not kept in the history, such as demos and
/// diagnostics. They never open or replace the history.</summary>
public interface ITemporaryScanRepository
{
    /// <summary>A database in a temporary file that is deleted when disposed. Its engine takes
    /// the memory it needs from <paramref name="allowance"/>.</summary>
    ITemporaryScanDatabase Create(IMemoryAllowance allowance, CancellationToken token);
}

/// <summary>Methods run on the calling thread.</summary>
public interface ITemporaryScanDatabase : IDisposable
{
    Guid Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    );

    void SaveTimings(Guid scanId, ScanTimingSnapshot timing, CancellationToken token);

    /// <summary>Opens a published scan. The snapshot takes over this database: dispose the
    /// snapshot instead of the database.</summary>
    IScanSnapshot Open(Guid scanId);
}
