using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuildingSectionsAndCorridors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CorridorId",
                table: "Rooms",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CorridorId",
                table: "Readers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SectionId",
                table: "Floors",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BuildingSections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    BuildingId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingSections_Buildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "Buildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Corridors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ParentCorridorId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Corridors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Corridors_Corridors_ParentCorridorId",
                        column: x => x.ParentCorridorId,
                        principalTable: "Corridors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Corridors_Floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "Floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_CorridorId",
                table: "Rooms",
                column: "CorridorId");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_CorridorId",
                table: "Readers",
                column: "CorridorId");

            migrationBuilder.CreateIndex(
                name: "IX_Floors_SectionId",
                table: "Floors",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingSections_BuildingId",
                table: "BuildingSections",
                column: "BuildingId");

            migrationBuilder.CreateIndex(
                name: "IX_Corridors_FloorId",
                table: "Corridors",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_Corridors_ParentCorridorId",
                table: "Corridors",
                column: "ParentCorridorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Floors_BuildingSections_SectionId",
                table: "Floors",
                column: "SectionId",
                principalTable: "BuildingSections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Readers_Corridors_CorridorId",
                table: "Readers",
                column: "CorridorId",
                principalTable: "Corridors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Rooms_Corridors_CorridorId",
                table: "Rooms",
                column: "CorridorId",
                principalTable: "Corridors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Floors_BuildingSections_SectionId",
                table: "Floors");

            migrationBuilder.DropForeignKey(
                name: "FK_Readers_Corridors_CorridorId",
                table: "Readers");

            migrationBuilder.DropForeignKey(
                name: "FK_Rooms_Corridors_CorridorId",
                table: "Rooms");

            migrationBuilder.DropTable(
                name: "BuildingSections");

            migrationBuilder.DropTable(
                name: "Corridors");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_CorridorId",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Readers_CorridorId",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_Floors_SectionId",
                table: "Floors");

            migrationBuilder.DropColumn(
                name: "CorridorId",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "CorridorId",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "Floors");
        }
    }
}
