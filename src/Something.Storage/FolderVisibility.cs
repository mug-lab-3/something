using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

public sealed partial class ScanStore
{
    /// <summary>Only the directory identities, attributes and parents needed for display policy.
    /// One query; no file DATA streams or per-file page lookups.</summary>
    public IEnumerable<FolderVisibilityObservation> ReadVisibilityFolders(
        Guid scanId,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        _ = GetScan(scanId);
        if (context is DuckScanDbContext)
        {
            foreach (var folder in ReadVisibilityFoldersNative(scanId, token))
                yield return folder;
            yield break;
        }
        foreach (
            var folder in context
                .Observations.AsNoTracking()
                .Where(o => o.ScanId == scanId && o.IsDirectory == true && o.Sequence != null)
                .Include(o => o.Names)
                .AsSingleQuery()
                .AsEnumerable()
        )
        {
            token.ThrowIfCancellationRequested();
            yield return new(
                FileReference.Create(checked((ulong)folder.RecordIndex), folder.Sequence!.Value),
                [
                    .. folder
                        .Names.Where(n => n.Namespace != FileNameNamespace.Dos)
                        .Select(n =>
                            FileReference.Create(checked((ulong)n.ParentIndex), n.ParentSequence)
                        )
                        .Distinct(),
                ],
                folder.StandardFileAttributes
            );
        }
    }
}
