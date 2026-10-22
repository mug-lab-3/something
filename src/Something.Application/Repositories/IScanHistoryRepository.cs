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

    /// <summary>Opens one retained scan for browsing.</summary>
    IHistorySnapshot Open(Guid id, CancellationToken token);

    /// <summary>Retires the confirmed scans and returns those removed. Scans still open are
    /// collected later by <see cref="CollectRetired"/>.</summary>
    Guid[] Delete(IReadOnlyCollection<Guid> ids, CancellationToken token);

    /// <summary>Collects retired scans that are no longer open; returns how many remain open.</summary>
    int CollectRetired(CancellationToken token);
}

/// <summary>Stores completed scans as the newest generation of the history.</summary>
public interface IScanPublisher
{
    /// <summary>Publishes the scan, prunes the history and opens the new generation. Nothing else
    /// can retire the generation between publication and opening. The caller disposes the snapshot.</summary>
    IHistorySnapshot Publish(
        CompletedScan scan,
        DateTimeOffset endedUtc,
        string drive,
        TimeSpan elapsed,
        IMemoryAllowance allowance,
        Action<ScanWorkProgress>? progress,
        CancellationToken token
    );
}
