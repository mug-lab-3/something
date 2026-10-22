using Something.Application.Comparison;

namespace Something.Application;

/// <summary>Runs history queries off the UI thread. The repository serializes its database operations.</summary>
public sealed class FolderHistoryService(HistoryService history)
{
    public Task<FolderChangeReport> ReadChangesAsync(
        string volumeKey,
        Guid beforeId,
        Guid afterId,
        CancellationToken token
    )
    {
        var changes = history.Repositories.Changes;
        return Task.Run(
            () => changes.ReadFolderChanges(volumeKey, beforeId, afterId, token),
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
        var changes = history.Repositories.Changes;
        return Task.Run(() => changes.ReadFolderChanges(volumeKey, period, asOfUtc, token), token);
    }
}
