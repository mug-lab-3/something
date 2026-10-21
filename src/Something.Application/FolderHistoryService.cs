using Something.Storage;

namespace Something.Application;

/// <summary>Runs history queries off the UI thread. The archive serializes its database operations.</summary>
public sealed class FolderHistoryService(HistoryService history)
{
    public Task<FolderChangeReport> ReadChangesAsync(
        string volumeKey,
        Guid beforeId,
        Guid afterId,
        CancellationToken token
    )
    {
        var archive = history.Archive;
        return Task.Run(
            () => archive.ReadFolderChanges(volumeKey, beforeId, afterId, token),
            token
        );
    }

    public Task<FolderChangeReport> ReadChangesAsync(
        string volumeKey,
        FolderChangePeriod period,
        CancellationToken token,
        DateTimeOffset? asOfUtc = null
    )
    {
        var archive = history.Archive;
        return Task.Run(() => archive.ReadFolderChanges(volumeKey, period, asOfUtc, token), token);
    }
}
