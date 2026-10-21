using DuckDB.EFCoreProvider.Extensions;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Something.Storage;

internal sealed class SharedHistoryDbContext(DbContextOptions<SharedHistoryDbContext> options)
    : DuckScanDbContext(options)
{
    internal DbSet<SharedFolderReferenceEntity> SharedFolders => Set<SharedFolderReferenceEntity>();
    internal DbSet<GenerationObservationEntity> Members => Set<GenerationObservationEntity>();
    internal DbSet<SharedListingReferenceEntity> Listings => Set<SharedListingReferenceEntity>();
    internal DbSet<SharedEntryEntity> Entries => Set<SharedEntryEntity>();
    internal DbSet<StateObservationEntity> States => Set<StateObservationEntity>();
    internal DbSet<StateFileNameEntity> StateNames => Set<StateFileNameEntity>();
    internal DbSet<StateStreamEntity> StateStreams => Set<StateStreamEntity>();
    internal DbSet<StateFolderSampleEntity> Samples => Set<StateFolderSampleEntity>();

    internal const string GenerationNumber = "Number";

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        // Internal generation number, local to shared history. scan.Id stays the external
        // identity for history entries, leases and the application.
        var scans = model.Entity<ScanEntity>();
        scans.Property<int>(GenerationNumber);
        scans.HasIndex(GenerationNumber).IsUnique();
        scans.ToTable(t => t.HasCheckConstraint("scan_number", "Number > 0"));
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
        // Large tables are keyless: native PK/ART indexes stay resident in memory and cannot
        // spill. Publication constructs and validates their logical keys instead.
        var folders = model.Entity<SharedFolderReferenceEntity>();
        folders.ToTable(
            "scan_folder_ref",
            t =>
                t.HasCheckConstraint(
                    "shared_folder_identity",
                    "Generation > 0 AND SampleVersion > 0 AND (ObservationVersion IS NULL OR ObservationVersion > 0) AND RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535"
                )
        );
        folders.HasNoKey();
        var members = model.Entity<GenerationObservationEntity>();
        members.ToTable(
            "generation_observation",
            t =>
                t.HasCheckConstraint(
                    "member_identity",
                    "Generation > 0 AND Version > 0 AND RecordIndex BETWEEN 0 AND 281474976710655"
                )
        );
        members.HasNoKey();
        var listings = model.Entity<SharedListingReferenceEntity>();
        listings.ToTable(
            "scan_listing_ref",
            t =>
                t.HasCheckConstraint(
                    "listing_identity",
                    "Generation > 0 AND Listing > 0 AND ParentIndex BETWEEN 0 AND 281474976710655 AND ParentSequence BETWEEN 0 AND 65535 AND Bucket >= 0"
                )
        );
        listings.HasNoKey();
        var entries = model.Entity<SharedEntryEntity>();
        entries.ToTable(
            "shared_entry",
            t =>
            {
                t.HasCheckConstraint(
                    "entry_identity",
                    "Listing > 0 AND ObservationVersion > 0 AND RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535 AND Ordinal >= 0"
                );
                t.HasCheckConstraint(
                    "entry_value",
                    "octet_length(Name) > 0 AND octet_length(Name) % 2 = 0 AND Resolution IN (0,1) AND (StreamLogical IS NULL OR StreamLogical >= 0) AND (StreamAllocated IS NULL OR StreamAllocated >= 0)"
                );
            }
        );
        entries.HasNoKey();
        entries.Property(x => x.StandardFileAttributes).HasConversion<long?>();
        entries
            .Property(x => x.Name)
            .HasConversion(v => StorageEncoding.Name(v), v => StorageEncoding.Name(v));
    }

    // Keep codecs and scalar checks identical to the validated fact models. States are keyless
    // and unindexed; immutable states deliberately have no dependency on a generation's lifetime.
    private static void CopyState<TSource, TState>(ModelBuilder model, string table)
        where TSource : class
        where TState : class
    {
        var source = model.Model.FindEntityType(typeof(TSource))!;
        var state = model.Entity<TState>();
        state.ToTable(table);
        state.Property<int>("Version");
        state.ToTable(table, t => t.HasCheckConstraint(table + "_version", "Version > 0"));
        foreach (var property in source.GetProperties().Where(p => p.Name != "ScanId"))
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
        state.HasNoKey();
        foreach (var check in source.GetCheckConstraints())
            state.ToTable(table, t => t.HasCheckConstraint("state_" + check.Name, check.Sql));
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
