using Something.Storage;

namespace Something.Application.Repositories;

/// <summary>The file that holds the history. Methods run on the calling thread. Compaction,
/// relocation and deletion require that no scan or search result is open.</summary>
public interface IHistoryDatabase
{
    string DatabasePath { get; }
    string DirectoryPath { get; }

    /// <summary>Why the last pruning after a publication failed, if it did.</summary>
    string? MaintenanceError { get; }

    /// <summary>File sizes only; cheap enough for the calling thread.</summary>
    HistoryDatabaseInfo ReadInfo();

    void Checkpoint(CancellationToken token);

    HistoryCompactionResult Compact(
        ulong memoryLimitBytes,
        Action<HistoryCompactionStage>? progress,
        CancellationToken token
    );

    /// <summary>Moves every drive's history to <paramref name="directory"/> and returns the
    /// repositories there. <paramref name="saveLocation"/> records the new location before the
    /// old database is deleted.</summary>
    HistoryRepositories Relocate(
        string directory,
        Action<string> saveLocation,
        CancellationToken token
    );

    void DeleteDatabase(CancellationToken token);
}

/// <summary>The repositories of one history location; replaced as a whole when it moves.</summary>
public sealed record HistoryRepositories(
    IScanHistoryRepository Scans,
    IFolderChangeRepository Changes,
    IHistorySearchRepository Searches,
    IHistoryDatabase Database
);
