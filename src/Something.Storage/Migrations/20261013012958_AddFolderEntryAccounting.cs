using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Something.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderEntryAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "EntryDirectAllocated",
                table: "folder_sample",
                type: "BLOB",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "EntryDirectFiles",
                table: "folder_sample",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<byte[]>(
                name: "EntryDirectLogical",
                table: "folder_sample",
                type: "BLOB",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "EntryDirectUnresolved",
                table: "folder_sample",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<byte[]>(
                name: "EntrySubtreeAllocated",
                table: "folder_sample",
                type: "BLOB",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "EntrySubtreeFiles",
                table: "folder_sample",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<byte[]>(
                name: "EntrySubtreeLogical",
                table: "folder_sample",
                type: "BLOB",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "EntrySubtreeUnresolved",
                table: "folder_sample",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddCheckConstraint(
                name: "folder_entries",
                table: "folder_sample",
                sql: "(EntryDirectLogical IS NULL AND EntryDirectAllocated IS NULL AND EntrySubtreeLogical IS NULL AND EntrySubtreeAllocated IS NULL AND EntryDirectFiles IS NULL AND EntrySubtreeFiles IS NULL AND EntryDirectUnresolved IS NULL AND EntrySubtreeUnresolved IS NULL) OR (EntryDirectLogical IS NOT NULL AND EntryDirectAllocated IS NOT NULL AND EntrySubtreeLogical IS NOT NULL AND EntrySubtreeAllocated IS NOT NULL AND EntryDirectFiles IS NOT NULL AND EntrySubtreeFiles IS NOT NULL AND EntryDirectUnresolved IS NOT NULL AND EntrySubtreeUnresolved IS NOT NULL AND length(EntryDirectLogical)=16 AND length(EntryDirectAllocated)=16 AND length(EntrySubtreeLogical)=16 AND length(EntrySubtreeAllocated)=16 AND EntryDirectFiles >= 0 AND EntrySubtreeFiles >= 0 AND EntryDirectUnresolved >= 0 AND EntrySubtreeUnresolved >= 0)"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "folder_entries", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntryDirectAllocated", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntryDirectFiles", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntryDirectLogical", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntryDirectUnresolved", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntrySubtreeAllocated", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntrySubtreeFiles", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntrySubtreeLogical", table: "folder_sample");

            migrationBuilder.DropColumn(name: "EntrySubtreeUnresolved", table: "folder_sample");
        }
    }
}
