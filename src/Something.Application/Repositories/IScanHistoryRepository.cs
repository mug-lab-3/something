using Something.Application.History;
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
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        string drive,
        TimeSpan elapsed,
        ulong memoryLimitBytes,
        DatabaseMemoryGrowth? growth,
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

/// <summary>How a database operation may raise its memory limit through the shared budget.</summary>
/// <param name="TryGrow">Returns a larger limit for the current one, or null when none fits.</param>
/// <param name="Account">Charges a limit the operation chose itself, such as one after a retry.</param>
public sealed record DatabaseMemoryGrowth(Func<ulong, ulong?> TryGrow, Action<ulong> Account);
