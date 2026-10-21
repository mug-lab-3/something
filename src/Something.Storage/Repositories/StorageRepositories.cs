using Something.Application.Export;
using Something.Application.Repositories;

namespace Something.Storage;

/// <summary>The storage implementations of the application's repositories, for the startup
/// composition. Everything else uses the interfaces.</summary>
public static class StorageRepositories
{
    /// <summary>The history in <paramref name="directory"/>, or in the default location.</summary>
    public static HistoryRepositories OpenHistory(string? directory = null) =>
        ArchiveRepositories.Open(directory);

    /// <summary>For tests and tools that build their archive with a fixed clock or time zone.</summary>
    public static HistoryRepositories ForArchive(ScanHistoryArchive archive) =>
        ArchiveRepositories.For(archive);

    public static ITemporaryScanRepository TemporaryScans => TemporaryScanRepository.Instance;

    public static IParquetTableWriter Parquet => ParquetTableWriter.Instance;
}
