using DuckDB.EFCoreProvider.Extensions;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Something.Storage;

internal sealed class SharedHistoryDbContext(DbContextOptions<SharedHistoryDbContext> options)
    : DuckScanDbContext(options)
{
    internal DbSet<SharedFolderReferenceEntity> SharedFolders => Set<SharedFolderReferenceEntity>();
    internal DbSet<SharedAuxiliaryReferenceEntity> Auxiliary =>
        Set<SharedAuxiliaryReferenceEntity>();
    internal DbSet<SharedListingReferenceEntity> Listings => Set<SharedListingReferenceEntity>();
    internal DbSet<SharedEntryEntity> Entries => Set<SharedEntryEntity>();
    internal DbSet<StateObservationEntity> States => Set<StateObservationEntity>();
    internal DbSet<StateFileNameEntity> StateNames => Set<StateFileNameEntity>();
    internal DbSet<StateStreamEntity> StateStreams => Set<StateStreamEntity>();
    internal DbSet<StateFolderSampleEntity> Samples => Set<StateFolderSampleEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        CopyState<ObservationEntity, StateObservationEntity>(model, "state_observation");
        CopyState<FileNameEntity, StateFileNameEntity>(model, "state_file_name");
        CopyState<StreamEntity, StateStreamEntity>(model, "state_stream");
        CopyState<FolderSampleEntity, StateFolderSampleEntity>(model, "state_folder_sample");
        ReadView<ObservationEntity>(model, "observation");
        ReadView<FileNameEntity>(model, "file_name");
        ReadView<StreamEntity>(model, "stream");
        ReadView<FolderSampleEntity>(model, "folder_sample");
        ReadView<FolderNameEntity>(model, "folder_name_history");
        ReadView<BrowserEntity>(model, "browser");
        var folders = model.Entity<SharedFolderReferenceEntity>();
        folders.ToTable(
            "scan_folder_ref",
            t =>
                t.HasCheckConstraint(
                    "shared_folder_identity",
                    "RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535"
                )
        );
        folders.HasKey(x => new { x.ScanId, x.RecordIndex });
        var auxiliary = model.Entity<SharedAuxiliaryReferenceEntity>();
        auxiliary.ToTable(
            "auxiliary_ref",
            t =>
                t.HasCheckConstraint(
                    "auxiliary_identity",
                    "RecordIndex BETWEEN 0 AND 281474976710655"
                )
        );
        auxiliary.HasKey(x => new { x.ScanId, x.RecordIndex });
        var listings = model.Entity<SharedListingReferenceEntity>();
        listings.ToTable(
            "scan_listing_ref",
            t =>
                t.HasCheckConstraint(
                    "listing_identity",
                    "ParentIndex BETWEEN 0 AND 281474976710655 AND ParentSequence BETWEEN 0 AND 65535 AND Bucket >= 0"
                )
        );
        listings.HasKey(x => new
        {
            x.ScanId,
            x.ParentIndex,
            x.ParentSequence,
            x.Bucket,
        });
        listings.HasIndex(x => x.ParentIndex);
        var entries = model.Entity<SharedEntryEntity>();
        entries.ToTable(
            "shared_entry",
            t =>
            {
                t.HasCheckConstraint(
                    "entry_identity",
                    "RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535 AND Ordinal >= 0"
                );
                t.HasCheckConstraint(
                    "entry_value",
                    "octet_length(Name) > 0 AND octet_length(Name) % 2 = 0 AND Resolution IN (0,1) AND (StreamLogical IS NULL OR StreamLogical >= 0) AND (StreamAllocated IS NULL OR StreamAllocated >= 0)"
                );
            }
        );
        entries.HasKey(x => new
        {
            x.ScanId,
            x.RecordIndex,
            x.Ordinal,
        });
        entries
            .Property(x => x.Name)
            .HasConversion(v => StorageEncoding.Name(v), v => StorageEncoding.Name(v));
    }

    // Keep codecs, scalar checks and explicit keys identical to the validated fact models.
    // Immutable states deliberately have no dependency on a generation's lifetime.
    private static void CopyState<TSource, TState>(ModelBuilder model, string table)
        where TSource : class
        where TState : class
    {
        var source = model.Model.FindEntityType(typeof(TSource))!;
        var state = model.Entity<TState>();
        state.ToTable(table);
        foreach (var property in source.GetProperties())
        {
            var mapped = state
                .Property(property.ClrType, property.Name)
                .IsRequired(!property.IsNullable);
            if (property.GetValueConverter() is { } converter)
                mapped.HasConversion(converter);
            else if (property.GetProviderClrType() is { } providerType)
                mapped.HasConversion(providerType);
            if (property.GetColumnType() is { } columnType)
                mapped.HasColumnType(columnType);
        }
        state.HasKey(source.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        foreach (var check in source.GetCheckConstraints())
            state.ToTable(table, t => t.HasCheckConstraint("state_" + check.Name, check.Sql));
        foreach (var index in source.GetIndexes())
            state.HasIndex(index.Properties.Select(p => p.Name).ToArray()).IsUnique(index.IsUnique);
    }

    private static void ReadView<T>(ModelBuilder model, string view)
        where T : class
    {
        var entity = model.Entity<T>();
        foreach (var check in entity.Metadata.GetCheckConstraints().ToArray())
            entity.Metadata.RemoveCheckConstraint(check.Name!);
        foreach (var index in entity.Metadata.GetIndexes().ToArray())
            entity.Metadata.RemoveIndex(index.Properties);
        entity.Metadata.SetTableName(null);
        entity.ToView(view);
    }
}

internal sealed class SharedHistoryDbContextFactory
    : IDesignTimeDbContextFactory<SharedHistoryDbContext>
{
    internal static DbContextOptions<SharedHistoryDbContext> Options(DuckDBConnection connection) =>
        new DbContextOptionsBuilder<SharedHistoryDbContext>()
            .UseDuckDB(connection, contextOwnsConnection: true)
            .UseInternalServiceProvider(DuckScanDbContextFactory.Services)
            .Options;

    public SharedHistoryDbContext CreateDbContext(string[] args) =>
        new(Options(new DuckDBConnection("Data Source=:memory:")));
}
