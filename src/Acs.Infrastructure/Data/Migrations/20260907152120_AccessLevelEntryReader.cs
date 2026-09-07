using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessLevelEntryReader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReaderId",
                table: "AccessLevelEntries",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessLevelEntries_ReaderId",
                table: "AccessLevelEntries",
                column: "ReaderId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessLevelEntries_Readers_ReaderId",
                table: "AccessLevelEntries",
                column: "ReaderId",
                principalTable: "Readers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessLevelEntries_Readers_ReaderId",
                table: "AccessLevelEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccessLevelEntries_ReaderId",
                table: "AccessLevelEntries");

            migrationBuilder.DropColumn(
                name: "ReaderId",
                table: "AccessLevelEntries");
        }
    }
}
