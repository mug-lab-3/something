using Something.Application.Comparison;

namespace Something.Application.Repositories;

/// <summary>Differences between retained scans of one volume. Methods run on the calling thread.</summary>
public interface IFolderChangeRepository
{
    FolderChangeReport ReadFolderChanges(
        string volumeKey,
        Guid beforeId,
        Guid afterId,
        CancellationToken token
    );

    FolderChangeReport ReadFolderChanges(
        string volumeKey,
        FolderChangePeriod period,
        DateTimeOffset? asOfUtc,
        CancellationToken token
    );
}
