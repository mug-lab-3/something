using Something.Application.History;
using Something.Application.Memory;
using Something.Application.Scanning;

namespace Something.Application.Repositories;

/// <summary>The retained scans of every drive. Methods run on the calling thread.</summary>
public interface IScanHistoryRepository
{
    /// <summary>Retained scans of one volume, newest first.</summary>
    ScanHistoryEntry[] List(string volumeKey, CancellationToken token);

    ScanHistorySummary[] ListSummaries(string volumeKey, CancellationToken token);

    IReadOnlyDictionary<Guid, long> ReadRecordCounts(
        IReadOnlyCollection<Guid> scanIds,
        CancellationToken token
    );

    /// <summary>Stores a completed scan as the newest generation and prunes the history.</summary>
    ScanHistoryEntry Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        string drive,
        TimeSpan elapsed,
        IMemoryAllowance allowance,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    );

    /// <summary>Copies a completed scan from a temporary database into the history.</summary>
    ScanHistoryEntry Save(
        IScanSnapshot source,
        string drive,
        TimeSpan elapsed,
        CancellationToken token
    );

    void SaveTimings(Guid scanId, ScanTimingSnapshot timing, CancellationToken token);

    /// <summary>Opens one retained scan for browsing.</summary>
    IHistorySnapshot Open(Guid id, CancellationToken token);

    /// <summary>Retires the confirmed scans and returns those removed. Scans still open are
    /// collected later by <see cref="CollectRetired"/>.</summary>
    Guid[] Delete(IReadOnlyCollection<Guid> ids, CancellationToken token);

    /// <summary>Collects retired scans that are no longer open; returns how many remain open.</summary>
    int CollectRetired(CancellationToken token);
}
