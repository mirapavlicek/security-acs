using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlanSourceCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SourceX",
                table: "Rooms",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SourceY",
                table: "Rooms",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SourceX",
                table: "Readers",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SourceY",
                table: "Readers",
                type: "double",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceX",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SourceY",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SourceX",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "SourceY",
                table: "Readers");
        }
    }
}
