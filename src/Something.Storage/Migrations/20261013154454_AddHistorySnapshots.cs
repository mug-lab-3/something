using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Something.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddHistorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "history_snapshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VolumeKey = table.Column<string>(type: "TEXT", nullable: false),
                    Drive = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalDay = table.Column<int>(type: "INTEGER", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Retired = table.Column<bool>(type: "INTEGER", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_history_snapshot_VolumeKey_CompletedUtcTicks",
                table: "history_snapshot",
                columns: new[] { "VolumeKey", "CompletedUtcTicks" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "history_snapshot");
        }
    }
}
