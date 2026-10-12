using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Something.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialScanStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VolumeKey = table.Column<string>(type: "TEXT", nullable: false),
                    Epoch = table.Column<Guid>(type: "TEXT", nullable: false),
                    Serial = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RangeStart = table.Column<long>(type: "INTEGER", nullable: false),
                    RangeCount = table.Column<long>(type: "INTEGER", nullable: false),
                    Started = table.Column<string>(type: "TEXT", nullable: false),
                    Ended = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservationMode = table.Column<string>(type: "TEXT", nullable: false),
                    AttributionPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    CapacityPolicy = table.Column<string>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan", x => x.Id);
                    table.CheckConstraint(
                        "scan_identity",
                        "length(VolumeKey) > 0 AND length(Serial) = 8 AND ObservationMode = 'OptimisticLive'"
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
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<ushort>(type: "INTEGER", nullable: false),
                    DirectLogical = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DirectAllocated = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SharedLogical = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SharedAllocated = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DirectoryLogical = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DirectoryAllocated = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SubtreeLogical = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SubtreeAllocated = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AssignedCount = table.Column<long>(type: "INTEGER", nullable: false),
                    SubtreeCount = table.Column<long>(type: "INTEGER", nullable: false),
                    DirectUnresolved = table.Column<long>(type: "INTEGER", nullable: false),
                    SubtreeUnresolved = table.Column<long>(type: "INTEGER", nullable: false),
                    DirectoryObserved = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnobservedDirectories = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_sample", x => new { x.ScanId, x.RecordIndex });
                    table.CheckConstraint(
                        "folder_capacities",
                        "length(DirectLogical)=16 AND length(DirectAllocated)=16 AND length(SharedLogical)=16 AND length(SharedAllocated)=16 AND length(DirectoryLogical)=16 AND length(DirectoryAllocated)=16 AND length(SubtreeLogical)=16 AND length(SubtreeAllocated)=16"
                    );
                    table.CheckConstraint(
                        "folder_counts",
                        "AssignedCount >= 0 AND SubtreeCount >= 0 AND DirectUnresolved >= 0 AND SubtreeUnresolved >= 0 AND UnobservedDirectories >= 0 AND DirectoryObserved IN (0,1)"
                    );
                    table.CheckConstraint(
                        "folder_identity",
                        "RecordIndex BETWEEN 0 AND 281474976710655 AND Sequence BETWEEN 0 AND 65535"
                    );
                    table.ForeignKey(
                        name: "FK_folder_sample_scan_ScanId",
                        column: x => x.ScanId,
                        principalTable: "scan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "observation",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<ushort>(type: "INTEGER", nullable: true),
                    IsDirectory = table.Column<bool>(type: "INTEGER", nullable: true),
                    Domain = table.Column<int>(type: "INTEGER", nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                    LinksComplete = table.Column<bool>(type: "INTEGER", nullable: true),
                    ObservedLinkCount = table.Column<ushort>(type: "INTEGER", nullable: true),
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
                    table.ForeignKey(
                        name: "FK_observation_scan_ScanId",
                        column: x => x.ScanId,
                        principalTable: "scan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "published_generation",
                columns: table => new
                {
                    VolumeKey = table.Column<string>(type: "TEXT", nullable: false),
                    RangeStart = table.Column<long>(type: "INTEGER", nullable: false),
                    RangeCount = table.Column<long>(type: "INTEGER", nullable: false),
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.ForeignKey(
                        name: "FK_published_generation_scan_ScanId",
                        column: x => x.ScanId,
                        principalTable: "scan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "volume_sample",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MetadataLogical = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MetadataAllocated = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MetadataResolved = table.Column<long>(type: "INTEGER", nullable: false),
                    MetadataUnresolved = table.Column<long>(type: "INTEGER", nullable: false),
                    UnattributedLogical = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UnattributedAllocated = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UnattributedResolved = table.Column<long>(type: "INTEGER", nullable: false),
                    UnattributedUnresolved = table.Column<long>(type: "INTEGER", nullable: false),
                    UnknownAttribution = table.Column<bool>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_sample", x => x.ScanId);
                    table.CheckConstraint(
                        "volume_capacities",
                        "length(MetadataLogical)=16 AND length(MetadataAllocated)=16 AND length(UnattributedLogical)=16 AND length(UnattributedAllocated)=16"
                    );
                    table.CheckConstraint(
                        "volume_counts",
                        "MetadataResolved >= 0 AND MetadataUnresolved >= 0 AND UnattributedResolved >= 0 AND UnattributedUnresolved >= 0 AND UnknownAttribution IN (0,1)"
                    );
                    table.ForeignKey(
                        name: "FK_volume_sample_scan_ScanId",
                        column: x => x.ScanId,
                        principalTable: "scan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "folder_name_history",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentSequence = table.Column<ushort>(type: "INTEGER", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Namespace = table.Column<byte>(type: "INTEGER", nullable: false),
                    Attributes = table.Column<long>(type: "INTEGER", nullable: false),
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
                        "name_identity",
                        "RecordIndex BETWEEN 0 AND 281474976710655 AND ParentIndex BETWEEN 0 AND 281474976710655 AND ParentSequence BETWEEN 0 AND 65535 AND Ordinal >= 0"
                    );
                    table.CheckConstraint(
                        "name_value",
                        "length(Name) > 0 AND length(Name) % 2 = 0 AND Namespace BETWEEN 0 AND 3 AND Attributes BETWEEN 0 AND 4294967295"
                    );
                    table.ForeignKey(
                        name: "FK_folder_name_history_folder_sample_ScanId_RecordIndex",
                        columns: x => new { x.ScanId, x.RecordIndex },
                        principalTable: "folder_sample",
                        principalColumns: new[] { "ScanId", "RecordIndex" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "file_name",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentSequence = table.Column<ushort>(type: "INTEGER", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Namespace = table.Column<byte>(type: "INTEGER", nullable: false),
                    Attributes = table.Column<long>(type: "INTEGER", nullable: false),
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
                        "length(Name) > 0 AND length(Name) % 2 = 0 AND Namespace BETWEEN 0 AND 3 AND Attributes BETWEEN 0 AND 4294967295"
                    );
                    table.ForeignKey(
                        name: "FK_file_name_observation_ScanId_RecordIndex",
                        columns: x => new { x.ScanId, x.RecordIndex },
                        principalTable: "observation",
                        principalColumns: new[] { "ScanId", "RecordIndex" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "stream",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Logical = table.Column<long>(type: "INTEGER", nullable: false),
                    Allocated = table.Column<long>(type: "INTEGER", nullable: false),
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
                        "Logical >= 0 AND Allocated >= 0 AND Ordinal >= 0 AND length(Name) % 2 = 0"
                    );
                    table.ForeignKey(
                        name: "FK_stream_observation_ScanId_RecordIndex",
                        columns: x => new { x.ScanId, x.RecordIndex },
                        principalTable: "observation",
                        principalColumns: new[] { "ScanId", "RecordIndex" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_file_name_ScanId_ParentIndex_ParentSequence",
                table: "file_name",
                columns: new[] { "ScanId", "ParentIndex", "ParentSequence" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_published_generation_ScanId",
                table: "published_generation",
                column: "ScanId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_scan_VolumeKey",
                table: "scan",
                column: "VolumeKey",
                unique: true,
                filter: "State = 0"
            );

            migrationBuilder.CreateIndex(
                name: "IX_stream_ScanId_RecordIndex_Name",
                table: "stream",
                columns: new[] { "ScanId", "RecordIndex", "Name" },
                unique: true
            );

            // Stamp ownership inside the same migration transaction as the schema.
            migrationBuilder.Sql("PRAGMA application_id=1397706053;");
            migrationBuilder.Sql("PRAGMA user_version=1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "file_name");

            migrationBuilder.DropTable(name: "folder_name_history");

            migrationBuilder.DropTable(name: "published_generation");

            migrationBuilder.DropTable(name: "stream");

            migrationBuilder.DropTable(name: "volume_sample");

            migrationBuilder.DropTable(name: "folder_sample");

            migrationBuilder.DropTable(name: "observation");

            migrationBuilder.DropTable(name: "scan");
        }
    }
}
