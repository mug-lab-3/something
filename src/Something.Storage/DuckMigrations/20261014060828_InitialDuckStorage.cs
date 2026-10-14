using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Something.Storage.DuckMigrations
{
    /// <inheritdoc />
    public partial class InitialDuckStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This schema uses logical EF relationships, validated with set operations
            // before publication. Native foreign keys/cascades are intentionally omitted.
            migrationBuilder.Sql(
                "CREATE TABLE something_format(Application VARCHAR PRIMARY KEY, Version INTEGER NOT NULL CHECK(Version=2)); INSERT INTO something_format VALUES ('Something',2);"
            );
            migrationBuilder.CreateTable(
                name: "browser",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    RecordIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    ParentSequence = table.Column<ushort>(type: "USMALLINT", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Sequence = table.Column<ushort>(type: "USMALLINT", nullable: true),
                    IsDirectory = table.Column<bool>(type: "BOOLEAN", nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                    LinksComplete = table.Column<bool>(type: "BOOLEAN", nullable: true),
                    EntrySubtreeLogical = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: true
                    ),
                    EntrySubtreeAllocated = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: true
                    ),
                    FolderSequence = table.Column<ushort>(type: "USMALLINT", nullable: true),
                    EntrySubtreeUnresolved = table.Column<long>(type: "BIGINT", nullable: true),
                    UnobservedDirectories = table.Column<long>(type: "BIGINT", nullable: true),
                    StreamLogical = table.Column<long>(type: "BIGINT", nullable: true),
                    StreamAllocated = table.Column<long>(type: "BIGINT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_browser",
                        x => new
                        {
                            x.ScanId,
                            x.RecordIndex,
                            x.Ordinal,
                        }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "history_snapshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "UUID", nullable: false),
                    VolumeKey = table.Column<string>(type: "VARCHAR", nullable: false),
                    Drive = table.Column<string>(type: "VARCHAR", nullable: false),
                    CompletedUtcTicks = table.Column<long>(type: "BIGINT", nullable: false),
                    LocalDay = table.Column<int>(type: "INTEGER", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedTicks = table.Column<long>(type: "BIGINT", nullable: false),
                    Retired = table.Column<bool>(type: "BOOLEAN", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_history_snapshot", x => x.Id);
                    table.CheckConstraint(
                        "history_identity",
                        "length(VolumeKey) > 0 AND length(Drive) > 0 AND LocalDay BETWEEN 0 AND 3652058 AND Retired IN (0,1)"
                    );
                    table.CheckConstraint(
                        "history_times",
                        "CompletedUtcTicks >= 0 AND ElapsedTicks >= 0 AND UtcOffsetMinutes BETWEEN -840 AND 840"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "scan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "UUID", nullable: false),
                    VolumeKey = table.Column<string>(type: "VARCHAR", nullable: false),
                    Epoch = table.Column<Guid>(type: "UUID", nullable: false),
                    Serial = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RangeStart = table.Column<long>(type: "BIGINT", nullable: false),
                    RangeCount = table.Column<long>(type: "BIGINT", nullable: false),
                    Started = table.Column<string>(type: "VARCHAR", nullable: false),
                    Ended = table.Column<string>(type: "VARCHAR", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservationMode = table.Column<string>(type: "VARCHAR", nullable: false),
                    AttributionPolicy = table.Column<string>(type: "VARCHAR", nullable: false),
                    CapacityPolicy = table.Column<string>(type: "VARCHAR", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan", x => x.Id);
                    table.CheckConstraint(
                        "scan_identity",
                        "length(VolumeKey) > 0 AND octet_length(Serial) = 8 AND ObservationMode = 'OptimisticLive'"
                    );
                    table.CheckConstraint(
                        "scan_range",
                        "RangeStart BETWEEN 0 AND 281474976710655 AND RangeCount > 0 AND RangeCount <= 281474976710656 - RangeStart"
                    );
                    table.CheckConstraint(
                        "scan_state",
                        "State BETWEEN 0 AND 4 AND ((State = 0 AND Ended IS NULL) OR (State <> 0 AND Ended IS NOT NULL))"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "folder_sample",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    RecordIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    Sequence = table.Column<ushort>(type: "USMALLINT", nullable: false),
                    DirectLogical = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    DirectAllocated = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    SharedLogical = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    SharedAllocated = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    DirectoryLogical = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    DirectoryAllocated = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: false
                    ),
                    SubtreeLogical = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    SubtreeAllocated = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    AssignedCount = table.Column<long>(type: "BIGINT", nullable: false),
                    SubtreeCount = table.Column<long>(type: "BIGINT", nullable: false),
                    DirectUnresolved = table.Column<long>(type: "BIGINT", nullable: false),
                    SubtreeUnresolved = table.Column<long>(type: "BIGINT", nullable: false),
                    DirectoryObserved = table.Column<bool>(type: "BOOLEAN", nullable: false),
                    UnobservedDirectories = table.Column<long>(type: "BIGINT", nullable: false),
                    EntryDirectLogical = table.Column<BigInteger>(type: "UHUGEINT", nullable: true),
                    EntryDirectAllocated = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: true
                    ),
                    EntrySubtreeLogical = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: true
                    ),
                    EntrySubtreeAllocated = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: true
                    ),
                    EntryDirectFiles = table.Column<long>(type: "BIGINT", nullable: true),
                    EntrySubtreeFiles = table.Column<long>(type: "BIGINT", nullable: true),
                    EntryDirectUnresolved = table.Column<long>(type: "BIGINT", nullable: true),
                    EntrySubtreeUnresolved = table.Column<long>(type: "BIGINT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_sample", x => new { x.ScanId, x.RecordIndex });
                    table.CheckConstraint(
                        "folder_capacities",
                        "TRUE AND TRUE AND TRUE AND TRUE AND TRUE AND TRUE AND TRUE AND TRUE"
                    );
                    table.CheckConstraint(
                        "folder_counts",
                        "AssignedCount >= 0 AND SubtreeCount >= 0 AND DirectUnresolved >= 0 AND SubtreeUnresolved >= 0 AND UnobservedDirectories >= 0 AND DirectoryObserved IN (0,1)"
                    );
                    table.CheckConstraint(
                        "folder_entries",
                        "(EntryDirectLogical IS NULL AND EntryDirectAllocated IS NULL AND EntrySubtreeLogical IS NULL AND EntrySubtreeAllocated IS NULL AND EntryDirectFiles IS NULL AND EntrySubtreeFiles IS NULL AND EntryDirectUnresolved IS NULL AND EntrySubtreeUnresolved IS NULL) OR (EntryDirectLogical IS NOT NULL AND EntryDirectAllocated IS NOT NULL AND EntrySubtreeLogical IS NOT NULL AND EntrySubtreeAllocated IS NOT NULL AND EntryDirectFiles IS NOT NULL AND EntrySubtreeFiles IS NOT NULL AND EntryDirectUnresolved IS NOT NULL AND EntrySubtreeUnresolved IS NOT NULL AND TRUE AND TRUE AND TRUE AND TRUE AND EntryDirectFiles >= 0 AND EntrySubtreeFiles >= 0 AND EntryDirectUnresolved >= 0 AND EntrySubtreeUnresolved >= 0)"
                    );
                    table.CheckConstraint(
                        "folder_identity",
                        "RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "observation",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    RecordIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<ushort>(type: "USMALLINT", nullable: true),
                    IsDirectory = table.Column<bool>(type: "BOOLEAN", nullable: true),
                    Domain = table.Column<int>(type: "INTEGER", nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                    LinksComplete = table.Column<bool>(type: "BOOLEAN", nullable: true),
                    ObservedLinkCount = table.Column<ushort>(type: "USMALLINT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observation", x => new { x.ScanId, x.RecordIndex });
                    table.CheckConstraint(
                        "observation_identity",
                        "(Sequence IS NULL AND IsDirectory IS NULL AND Domain IS NULL AND Resolution IS NULL AND LinksComplete IS NULL AND ObservedLinkCount IS NULL) OR (Sequence BETWEEN 0 AND 65535 AND IsDirectory IN (0,1) AND Domain BETWEEN 0 AND 2 AND Resolution IN (0,1) AND LinksComplete IN (0,1) AND Sequence IS NOT NULL AND IsDirectory IS NOT NULL AND Domain IS NOT NULL AND Resolution IS NOT NULL AND LinksComplete IS NOT NULL)"
                    );
                    table.CheckConstraint(
                        "observation_index",
                        "RecordIndex BETWEEN 0 AND 281474976710655"
                    );
                    table.CheckConstraint(
                        "observation_links",
                        "ObservedLinkCount IS NULL OR ObservedLinkCount BETWEEN 0 AND 65535"
                    );
                    table.CheckConstraint(
                        "observation_state",
                        "Kind BETWEEN 0 AND 2 AND Status BETWEEN 0 AND 8"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "published_generation",
                columns: table => new
                {
                    VolumeKey = table.Column<string>(type: "VARCHAR", nullable: false),
                    RangeStart = table.Column<long>(type: "BIGINT", nullable: false),
                    RangeCount = table.Column<long>(type: "BIGINT", nullable: false),
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_published_generation",
                        x => new
                        {
                            x.VolumeKey,
                            x.RangeStart,
                            x.RangeCount,
                        }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "volume_sample",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    MetadataLogical = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    MetadataAllocated = table.Column<BigInteger>(type: "UHUGEINT", nullable: false),
                    MetadataResolved = table.Column<long>(type: "BIGINT", nullable: false),
                    MetadataUnresolved = table.Column<long>(type: "BIGINT", nullable: false),
                    UnattributedLogical = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: false
                    ),
                    UnattributedAllocated = table.Column<BigInteger>(
                        type: "UHUGEINT",
                        nullable: false
                    ),
                    UnattributedResolved = table.Column<long>(type: "BIGINT", nullable: false),
                    UnattributedUnresolved = table.Column<long>(type: "BIGINT", nullable: false),
                    UnknownAttribution = table.Column<bool>(type: "BOOLEAN", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_sample", x => x.ScanId);
                    table.CheckConstraint("volume_capacities", "TRUE AND TRUE AND TRUE AND TRUE");
                    table.CheckConstraint(
                        "volume_counts",
                        "MetadataResolved >= 0 AND MetadataUnresolved >= 0 AND UnattributedResolved >= 0 AND UnattributedUnresolved >= 0 AND UnknownAttribution IN (0,1)"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "folder_name_history",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    RecordIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    ParentSequence = table.Column<ushort>(type: "USMALLINT", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Namespace = table.Column<byte>(type: "UTINYINT", nullable: false),
                    Attributes = table.Column<long>(type: "BIGINT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_folder_name_history",
                        x => new
                        {
                            x.ScanId,
                            x.RecordIndex,
                            x.Ordinal,
                        }
                    );
                    table.CheckConstraint(
                        "name_identity1",
                        "RecordIndex BETWEEN 0 AND 281474976710655 AND ParentIndex BETWEEN 0 AND 281474976710655 AND ParentSequence BETWEEN 0 AND 65535 AND Ordinal >= 0"
                    );
                    table.CheckConstraint(
                        "name_value1",
                        "octet_length(Name) > 0 AND octet_length(Name) % 2 = 0 AND Namespace BETWEEN 0 AND 3 AND Attributes BETWEEN 0 AND 4294967295"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "file_name",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    RecordIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    ParentSequence = table.Column<ushort>(type: "USMALLINT", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Namespace = table.Column<byte>(type: "UTINYINT", nullable: false),
                    Attributes = table.Column<long>(type: "BIGINT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_file_name",
                        x => new
                        {
                            x.ScanId,
                            x.RecordIndex,
                            x.Ordinal,
                        }
                    );
                    table.CheckConstraint(
                        "name_identity",
                        "RecordIndex BETWEEN 0 AND 281474976710655 AND ParentIndex BETWEEN 0 AND 281474976710655 AND ParentSequence BETWEEN 0 AND 65535 AND Ordinal >= 0"
                    );
                    table.CheckConstraint(
                        "name_value",
                        "octet_length(Name) > 0 AND octet_length(Name) % 2 = 0 AND Namespace BETWEEN 0 AND 3 AND Attributes BETWEEN 0 AND 4294967295"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "stream",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    RecordIndex = table.Column<long>(type: "BIGINT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Logical = table.Column<long>(type: "BIGINT", nullable: false),
                    Allocated = table.Column<long>(type: "BIGINT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_stream",
                        x => new
                        {
                            x.ScanId,
                            x.RecordIndex,
                            x.Ordinal,
                        }
                    );
                    table.CheckConstraint(
                        "stream_sizes",
                        "Logical >= 0 AND Allocated >= 0 AND Ordinal >= 0 AND octet_length(Name) % 2 = 0"
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_browser_ParentIndex",
                table: "browser",
                column: "ParentIndex"
            );

            migrationBuilder.CreateIndex(
                name: "IX_file_name_ScanId_ParentIndex_ParentSequence",
                table: "file_name",
                columns: new[] { "ScanId", "ParentIndex", "ParentSequence" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_history_snapshot_VolumeKey_CompletedUtcTicks",
                table: "history_snapshot",
                columns: new[] { "VolumeKey", "CompletedUtcTicks" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_scan_VolumeKey",
                table: "scan",
                column: "VolumeKey"
            );

            migrationBuilder.CreateIndex(
                name: "IX_stream_ScanId_RecordIndex_Name",
                table: "stream",
                columns: new[] { "ScanId", "RecordIndex", "Name" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE something_format;");
            migrationBuilder.DropTable(name: "browser");

            migrationBuilder.DropTable(name: "file_name");

            migrationBuilder.DropTable(name: "folder_name_history");

            migrationBuilder.DropTable(name: "history_snapshot");

            migrationBuilder.DropTable(name: "published_generation");

            migrationBuilder.DropTable(name: "stream");

            migrationBuilder.DropTable(name: "volume_sample");

            migrationBuilder.DropTable(name: "folder_sample");

            migrationBuilder.DropTable(name: "observation");

            migrationBuilder.DropTable(name: "scan");
        }
    }
}
