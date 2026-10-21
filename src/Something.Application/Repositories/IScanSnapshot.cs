using System.Collections.Immutable;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Application.Repositories;

/// <summary>One stored scan opened for reading. Reads run on the calling thread; callers keep them
/// off the UI thread and do not overlap them. Disposing releases the scan.</summary>
public interface IScanSnapshot : IDisposable
{
    Guid ScanId { get; }

    ScanSnapshotDescription Describe();

    (FolderCapacitySample Root, ulong MetadataUnresolved) ReadSummary(CancellationToken token);

    ScanTimingSnapshot? ReadTimings(CancellationToken token);

    FileReference? ReadParentFolder(
        FileReference folder,
        string visibleName,
        CancellationToken token
    );

    FolderEntryPage ReadFolderEntries(
        FileReference parent,
        int page,
        bool directoriesOnly,
        FolderEntrySort sort,
        bool includeTotalCount,
        bool descending,
        CancellationToken token
    );

    ImmutableArray<FolderEntry> ReadAllFolderEntries(FileReference parent, CancellationToken token);

    ImmutableArray<RankedFolder> ReadFolderAnalysis(
        FolderRankingFilter filter,
        FolderEntrySort sort,
        CancellationToken token
    );

    IEnumerable<FolderVisibilityObservation> ReadVisibilityFolders(CancellationToken token);

    IEnumerable<FileCapacityObservation> ReadFiles(CancellationToken token);

    IEnumerable<AnalysisFileName> ReadAnalysisFiles(CancellationToken token);
}

/// <summary>A retained scan opened from the history; it stays protected from deletion until disposed.</summary>
public interface IHistorySnapshot : IScanSnapshot
{
    ScanHistoryEntry Entry { get; }

    /// <summary>The size of the database file that holds the scan.</summary>
    ulong SizeBytes { get; }
}

/// <summary>The volume a stored scan came from and when it ended, if it completed.</summary>
public sealed record ScanSnapshotDescription(
    string VolumeKey,
    ulong VolumeSerial,
    DateTimeOffset? EndedUtc
);
