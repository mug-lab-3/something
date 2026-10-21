using System.Collections.Immutable;
using Something.Application.Browsing;
using Something.Application.History;
using Something.Application.Repositories;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>One scan in a <see cref="ScanStore"/>; disposing releases <paramref name="owner"/>.</summary>
internal class StoreScanSnapshot(IDisposable owner, ScanStore store, Guid scanId) : IScanSnapshot
{
    public Guid ScanId { get; } = scanId;
    internal ScanStore Store { get; } = store;

    public ScanSnapshotDescription Describe()
    {
        var scan = Store.GetScan(ScanId);
        return new(scan.Descriptor.VolumeKey, scan.Descriptor.VolumeSerial, scan.EndedUtc);
    }

    public (FolderCapacitySample Root, ulong MetadataUnresolved) ReadSummary(
        CancellationToken token
    ) => Store.ReadSnapshotSummary(ScanId, token);

    public ScanTimingSnapshot? ReadTimings(CancellationToken token) =>
        Store.ReadTimings(ScanId, token);

    public FileReference? ReadParentFolder(
        FileReference folder,
        string visibleName,
        CancellationToken token
    ) => Store.ReadParentFolder(ScanId, folder, visibleName, token);

    public FolderEntryPage ReadFolderEntries(
        FileReference parent,
        int page,
        bool directoriesOnly,
        FolderEntrySort sort,
        bool includeTotalCount,
        bool descending,
        CancellationToken token
    ) =>
        Store.ReadFolderEntries(
            ScanId,
            parent,
            page,
            directoriesOnly: directoriesOnly,
            cancellationToken: token,
            sort: sort,
            includeTotalCount: includeTotalCount,
            descending: descending
        );

    public ImmutableArray<FolderEntry> ReadAllFolderEntries(
        FileReference parent,
        CancellationToken token
    ) => Store.ReadAllFolderEntries(ScanId, parent, token);

    public ImmutableArray<RankedFolder> ReadFolderAnalysis(
        FolderRankingFilter filter,
        FolderEntrySort sort,
        CancellationToken token
    ) => Store.ReadFolderAnalysis(ScanId, filter, token, sort);

    public IEnumerable<FolderVisibilityObservation> ReadVisibilityFolders(
        CancellationToken token
    ) => Store.ReadVisibilityFolders(ScanId, token);

    public IEnumerable<FileCapacityObservation> ReadFiles(CancellationToken token) =>
        Store.ReadFiles(ScanId, token);

    public IEnumerable<AnalysisFileName> ReadAnalysisFiles(CancellationToken token) =>
        Store.ReadAnalysisFiles(ScanId, token);

    public void Dispose() => owner.Dispose();
}

internal sealed class ArchiveScanSnapshot(HistorySnapshot snapshot)
    : StoreScanSnapshot(snapshot, snapshot.Store, snapshot.Entry.Id),
        IHistorySnapshot
{
    public ScanHistoryEntry Entry => snapshot.Entry;
    public ulong SizeBytes => snapshot.SizeBytes;
}
