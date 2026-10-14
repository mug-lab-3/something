using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Something.Storage;

internal sealed class ScanDbContext(DbContextOptions<ScanDbContext> options) : DbContext(options)
{
    internal DbSet<ScanEntity> Scans => Set<ScanEntity>();
    internal DbSet<ObservationEntity> Observations => Set<ObservationEntity>();
    internal DbSet<FileNameEntity> Names => Set<FileNameEntity>();
    internal DbSet<StreamEntity> Streams => Set<StreamEntity>();
    internal DbSet<FolderNameEntity> FolderNames => Set<FolderNameEntity>();
    internal DbSet<FolderSampleEntity> Folders => Set<FolderSampleEntity>();
    internal DbSet<VolumeSampleEntity> Volumes => Set<VolumeSampleEntity>();
    internal DbSet<PublishedGenerationEntity> Published => Set<PublishedGenerationEntity>();
    internal DbSet<HistorySnapshotEntity> HistorySnapshots => Set<HistorySnapshotEntity>();

    private static readonly ValueConverter<UInt128, byte[]> Capacity = new(
        v => StorageEncoding.Unsigned128(v),
        v => StorageEncoding.Unsigned128(v)
    );
    private static readonly ValueConverter<string, byte[]> Name = new(
        v => StorageEncoding.Name(v),
        v => StorageEncoding.Name(v)
    );
    private static readonly ValueConverter<DateTimeOffset, string> Utc = new(
        v => v.ToString("O", CultureInfo.InvariantCulture),
        v => DateTimeOffset.ParseExact(v, "O", CultureInfo.InvariantCulture)
    );

    protected override void OnModelCreating(ModelBuilder model)
    {
        var snapshots = model.Entity<HistorySnapshotEntity>();
        snapshots.ToTable(
            "history_snapshot",
            table =>
            {
                table.HasCheckConstraint(
                    "history_times",
                    "CompletedUtcTicks >= 0 AND ElapsedTicks >= 0 AND UtcOffsetMinutes BETWEEN -840 AND 840"
                );
                table.HasCheckConstraint(
                    "history_identity",
                    "length(VolumeKey) > 0 AND length(Drive) > 0 AND LocalDay BETWEEN 0 AND 3652058 AND Retired IN (0,1)"
                );
            }
        );
        snapshots.HasKey(row => row.Id);
        snapshots.HasIndex(row => new { row.VolumeKey, row.CompletedUtcTicks });
        var scan = model.Entity<ScanEntity>();
        scan.ToTable(
            "scan",
            t =>
            {
                t.HasCheckConstraint(
                    "scan_range",
                    "RangeStart BETWEEN 0 AND 281474976710655 AND RangeCount > 0 AND RangeCount <= 281474976710656 - RangeStart"
                );
                t.HasCheckConstraint(
                    "scan_state",
                    "State BETWEEN 0 AND 4 AND ((State = 0 AND Ended IS NULL) OR (State <> 0 AND Ended IS NOT NULL))"
                );
                t.HasCheckConstraint(
                    "scan_identity",
                    "length(VolumeKey) > 0 AND length(Serial) = 8 AND ObservationMode = 'OptimisticLive'"
                );
            }
        );
        scan.HasKey(x => x.Id);
        scan.Property(x => x.Serial)
            .HasConversion(v => StorageEncoding.Unsigned64(v), v => StorageEncoding.Unsigned64(v));
        scan.Property(x => x.Started).HasConversion(Utc);
        scan.Property(x => x.Ended).HasConversion(Utc);
        scan.HasIndex(x => x.VolumeKey).IsUnique().HasFilter("State = 0");

        var observation = model.Entity<ObservationEntity>();
        observation.ToTable(
            "observation",
            t =>
            {
                t.HasCheckConstraint(
                    "observation_index",
                    "RecordIndex BETWEEN 0 AND 281474976710655"
                );
                t.HasCheckConstraint(
                    "observation_state",
                    "Kind BETWEEN 0 AND 2 AND Status BETWEEN 0 AND 8"
                );
                t.HasCheckConstraint(
                    "observation_identity",
                    "(Sequence IS NULL AND IsDirectory IS NULL AND Domain IS NULL AND Resolution IS NULL AND LinksComplete IS NULL AND ObservedLinkCount IS NULL) OR (Sequence BETWEEN 0 AND 65535 AND IsDirectory IN (0,1) AND Domain BETWEEN 0 AND 2 AND Resolution IN (0,1) AND LinksComplete IN (0,1) AND Sequence IS NOT NULL AND IsDirectory IS NOT NULL AND Domain IS NOT NULL AND Resolution IS NOT NULL AND LinksComplete IS NOT NULL)"
                );
                t.HasCheckConstraint(
                    "observation_links",
                    "ObservedLinkCount IS NULL OR ObservedLinkCount BETWEEN 0 AND 65535"
                );
            }
        );
        observation.HasKey(x => new { x.ScanId, x.RecordIndex });
        observation
            .HasOne<ScanEntity>()
            .WithMany()
            .HasForeignKey(x => x.ScanId)
            .OnDelete(DeleteBehavior.Cascade);
        var names = model.Entity<FileNameEntity>();
        names.ToTable("file_name", t => NameChecks(t));
        names.HasKey(x => new
        {
            x.ScanId,
            x.RecordIndex,
            x.Ordinal,
        });
        names.Property(x => x.Name).HasConversion(Name);
        names.Property(x => x.Attributes).HasConversion<long>();
        names.HasIndex(x => new
        {
            x.ScanId,
            x.ParentIndex,
            x.ParentSequence,
        });
        names
            .HasOne<ObservationEntity>()
            .WithMany(x => x.Names)
            .HasForeignKey(x => new { x.ScanId, x.RecordIndex })
            .OnDelete(DeleteBehavior.Cascade);
        var streams = model.Entity<StreamEntity>();
        streams.ToTable(
            "stream",
            t =>
            {
                t.HasCheckConstraint(
                    "stream_sizes",
                    "Logical >= 0 AND Allocated >= 0 AND Ordinal >= 0 AND length(Name) % 2 = 0"
                );
            }
        );
        streams.HasKey(x => new
        {
            x.ScanId,
            x.RecordIndex,
            x.Ordinal,
        });
        streams.Property(x => x.Name).HasConversion(Name);
        streams
            .HasIndex(x => new
            {
                x.ScanId,
                x.RecordIndex,
                x.Name,
            })
            .IsUnique();
        streams
            .HasOne<ObservationEntity>()
            .WithMany(x => x.Streams)
            .HasForeignKey(x => new { x.ScanId, x.RecordIndex })
            .OnDelete(DeleteBehavior.Cascade);

        var folders = model.Entity<FolderSampleEntity>();
        folders.ToTable(
            "folder_sample",
            t =>
            {
                t.HasCheckConstraint(
                    "folder_identity",
                    "RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535"
                );
                t.HasCheckConstraint(
                    "folder_counts",
                    "AssignedCount >= 0 AND SubtreeCount >= 0 AND DirectUnresolved >= 0 AND SubtreeUnresolved >= 0 AND UnobservedDirectories >= 0 AND DirectoryObserved IN (0,1)"
                );
                t.HasCheckConstraint(
                    "folder_capacities",
                    "length(DirectLogical)=16 AND length(DirectAllocated)=16 AND length(SharedLogical)=16 AND length(SharedAllocated)=16 AND length(DirectoryLogical)=16 AND length(DirectoryAllocated)=16 AND length(SubtreeLogical)=16 AND length(SubtreeAllocated)=16"
                );
                t.HasCheckConstraint(
                    "folder_entries",
                    "(EntryDirectLogical IS NULL AND EntryDirectAllocated IS NULL AND EntrySubtreeLogical IS NULL AND EntrySubtreeAllocated IS NULL AND EntryDirectFiles IS NULL AND EntrySubtreeFiles IS NULL AND EntryDirectUnresolved IS NULL AND EntrySubtreeUnresolved IS NULL) OR (EntryDirectLogical IS NOT NULL AND EntryDirectAllocated IS NOT NULL AND EntrySubtreeLogical IS NOT NULL AND EntrySubtreeAllocated IS NOT NULL AND EntryDirectFiles IS NOT NULL AND EntrySubtreeFiles IS NOT NULL AND EntryDirectUnresolved IS NOT NULL AND EntrySubtreeUnresolved IS NOT NULL AND length(EntryDirectLogical)=16 AND length(EntryDirectAllocated)=16 AND length(EntrySubtreeLogical)=16 AND length(EntrySubtreeAllocated)=16 AND EntryDirectFiles >= 0 AND EntrySubtreeFiles >= 0 AND EntryDirectUnresolved >= 0 AND EntrySubtreeUnresolved >= 0)"
                );
            }
        );
        folders.HasKey(x => new { x.ScanId, x.RecordIndex });
        folders
            .HasOne<ScanEntity>()
            .WithMany()
            .HasForeignKey(x => x.ScanId)
            .OnDelete(DeleteBehavior.Cascade);
        folders.Property(x => x.DirectLogical).HasConversion(Capacity);
        folders.Property(x => x.DirectAllocated).HasConversion(Capacity);
        folders.Property(x => x.SharedLogical).HasConversion(Capacity);
        folders.Property(x => x.SharedAllocated).HasConversion(Capacity);
        folders.Property(x => x.DirectoryLogical).HasConversion(Capacity);
        folders.Property(x => x.DirectoryAllocated).HasConversion(Capacity);
        folders.Property(x => x.SubtreeLogical).HasConversion(Capacity);
        folders.Property(x => x.SubtreeAllocated).HasConversion(Capacity);
        folders.Property(x => x.EntryDirectLogical).HasConversion(Capacity);
        folders.Property(x => x.EntryDirectAllocated).HasConversion(Capacity);
        folders.Property(x => x.EntrySubtreeLogical).HasConversion(Capacity);
        folders.Property(x => x.EntrySubtreeAllocated).HasConversion(Capacity);
        var history = model.Entity<FolderNameEntity>();
        history.ToTable("folder_name_history", t => NameChecks(t));
        history.HasKey(x => new
        {
            x.ScanId,
            x.RecordIndex,
            x.Ordinal,
        });
        history.Property(x => x.Name).HasConversion(Name);
        history.Property(x => x.Attributes).HasConversion<long>();
        history
            .HasOne<FolderSampleEntity>()
            .WithMany()
            .HasForeignKey(x => new { x.ScanId, x.RecordIndex })
            .OnDelete(DeleteBehavior.Cascade);

        var volume = model.Entity<VolumeSampleEntity>();
        volume.ToTable(
            "volume_sample",
            t =>
            {
                t.HasCheckConstraint(
                    "volume_counts",
                    "MetadataResolved >= 0 AND MetadataUnresolved >= 0 AND UnattributedResolved >= 0 AND UnattributedUnresolved >= 0 AND UnknownAttribution IN (0,1)"
                );
                t.HasCheckConstraint(
                    "volume_capacities",
                    "length(MetadataLogical)=16 AND length(MetadataAllocated)=16 AND length(UnattributedLogical)=16 AND length(UnattributedAllocated)=16"
                );
            }
        );
        volume.HasKey(x => x.ScanId);
        volume
            .HasOne<ScanEntity>()
            .WithOne()
            .HasForeignKey<VolumeSampleEntity>(x => x.ScanId)
            .OnDelete(DeleteBehavior.Cascade);
        volume.Property(x => x.MetadataLogical).HasConversion(Capacity);
        volume.Property(x => x.MetadataAllocated).HasConversion(Capacity);
        volume.Property(x => x.UnattributedLogical).HasConversion(Capacity);
        volume.Property(x => x.UnattributedAllocated).HasConversion(Capacity);
        var published = model.Entity<PublishedGenerationEntity>();
        published.ToTable("published_generation");
        published.HasKey(x => new
        {
            x.VolumeKey,
            x.RangeStart,
            x.RangeCount,
        });
        published
            .HasOne<ScanEntity>()
            .WithMany()
            .HasForeignKey(x => x.ScanId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void NameChecks<TEntity>(TableBuilder<TEntity> table)
        where TEntity : class
    {
        table.HasCheckConstraint(
            "name_identity",
            "RecordIndex BETWEEN 0 AND 281474976710655 AND ParentIndex BETWEEN 0 AND 281474976710655 AND ParentSequence BETWEEN 0 AND 65535 AND Ordinal >= 0"
        );
        table.HasCheckConstraint(
            "name_value",
            "length(Name) > 0 AND length(Name) % 2 = 0 AND Namespace BETWEEN 0 AND 3 AND Attributes BETWEEN 0 AND 4294967295"
        );
    }
}
