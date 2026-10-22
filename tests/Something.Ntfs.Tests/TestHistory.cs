using Something.Application;
using Something.Storage;

namespace Something.Ntfs.Tests;

internal static class TestHistory
{
    /// <summary>The services over an archive built with a fixed clock or time zone.</summary>
    public static HistoryService For(ScanHistoryArchive archive) =>
        new(StorageRepositories.ForArchive(archive));
}
