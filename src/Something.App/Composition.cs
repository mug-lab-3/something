using Something.Application;
using Something.Application.Export;
using Something.Storage;

namespace Something.App;

/// <summary>The only place that picks the storage implementation behind the application's
/// repositories; the rest of the user interface uses the application services.</summary>
internal static class Composition
{
    /// <summary>The history in <paramref name="directory"/>, or in the default location.</summary>
    public static HistoryService OpenHistory(string? directory) =>
        new(StorageRepositories.OpenHistory(directory));

    public static DriveScanner CreateScanner(HistoryService? history) =>
        new(StorageRepositories.TemporaryScans, history);

    public static IParquetTableWriter Parquet => StorageRepositories.Parquet;
}
