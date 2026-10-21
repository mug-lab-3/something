using Something.Application.Memory;
using Something.Application.Scanning;

namespace Something.Application.Repositories;

/// <summary>Private databases for scans that are not kept in the history, such as diagnostics and
/// demos without a history. They never open or replace the history.</summary>
public interface ITemporaryScanRepository
{
    /// <summary>Publishes the scan to a new database in a temporary file and opens it. The
    /// engine takes the memory it needs from <paramref name="allowance"/>. Disposing the snapshot
    /// deletes the file; a failed publication deletes it at once.</summary>
    IScanSnapshot Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        IMemoryAllowance allowance,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    );
}
