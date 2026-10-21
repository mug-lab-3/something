using Something.Application;
using Something.Storage;

namespace Something.Ntfs.Tests;

/// <summary>The layer boundaries the repositories exist to keep.</summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void StorageExposesOnlyItsCompositionEntryPoint()
    {
        // Act: EF Core generates its migrations as public classes; they are not part of any API.
        var exported = typeof(StorageRepositories)
            .Assembly.GetExportedTypes()
            .Where(type => !(type.Namespace ?? "").EndsWith("Migrations", StringComparison.Ordinal))
            .ToArray();

        // Assert
        Assert.Equal([typeof(StorageRepositories)], exported);
    }

    [Theory]
    [InlineData("Something.Storage")]
    [InlineData("DuckDB.NET.Data")]
    [InlineData("DuckDB.EFCoreProvider")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Data.Sqlite")]
    public void TheApplicationLayerDoesNotReferenceStorageTechnology(string assembly)
    {
        // Act
        string[] references =
        [
            .. typeof(HistoryService)
                .Assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!),
        ];

        // Assert
        Assert.DoesNotContain(assembly, references);
    }

    [Fact]
    public void TheApplicationLayerDefinesTheRepositoryInterfaces()
    {
        // Act
        var interfaces = typeof(HistoryService)
            .Assembly.GetExportedTypes()
            .Where(type =>
                type.IsInterface && type.Namespace == "Something.Application.Repositories"
            )
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);

        // Assert
        Assert.Equal(
            [
                "IFolderChangeRepository",
                "IHistoryDatabase",
                "IHistorySearchRepository",
                "IHistorySearchSession",
                "IHistorySnapshot",
                "IScanHistoryRepository",
                "IScanPublisher",
                "IScanSnapshot",
                "ITemporaryScanRepository",
            ],
            interfaces
        );
    }

    [Fact]
    public void EveryRepositoryInterfaceHasAStorageImplementationReachableFromTheEntryPoint()
    {
        // Act
        var history = StorageRepositories.OpenHistory(
            Path.Combine(
                Path.GetTempPath(),
                "something-architecture-" + Guid.NewGuid().ToString("N")
            )
        );

        // Assert
        Assert.NotNull(history.Scans);
        Assert.NotNull(history.Publisher);
        Assert.NotNull(history.Changes);
        Assert.NotNull(history.Searches);
        Assert.NotNull(history.Database);
        Assert.NotNull(StorageRepositories.TemporaryScans);
        Assert.NotNull(StorageRepositories.Parquet);
    }
}
