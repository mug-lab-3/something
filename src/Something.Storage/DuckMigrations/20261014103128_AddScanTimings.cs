using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Something.Storage.DuckMigrations
{
    /// <inheritdoc />
    public partial class AddScanTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_timing",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    PlanVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSteps = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTicks = table.Column<long>(type: "BIGINT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_timing", x => x.ScanId);
                    table.CheckConstraint(
                        "timing_values",
                        "PlanVersion = 1 AND TotalSteps BETWEEN 1 AND 100 AND TotalTicks >= 0"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "scan_step_timing",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "UUID", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Step = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    Work = table.Column<int>(type: "INTEGER", nullable: true),
                    ElapsedTicks = table.Column<long>(type: "BIGINT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_step_timing", x => new { x.ScanId, x.Ordinal });
                    table.CheckConstraint(
                        "step_timing_values",
                        "Ordinal >= 0 AND Step BETWEEN 0 AND 100 AND Attempt >= 0 AND ElapsedTicks >= 0 AND (Work IS NULL OR Work BETWEEN 0 AND 12)"
                    );
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "scan_step_timing");

            migrationBuilder.DropTable(name: "scan_timing");
        }
    }
}
