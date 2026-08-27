using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InteractiveFloorPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PlanH",
                table: "Rooms",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlanW",
                table: "Rooms",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlanX",
                table: "Rooms",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlanY",
                table: "Rooms",
                type: "double",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlanDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    X = table.Column<double>(type: "double", nullable: false),
                    Y = table.Column<double>(type: "double", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanDevices_Floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "Floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PlanDevices_FloorId",
                table: "PlanDevices",
                column: "FloorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanDevices");

            migrationBuilder.DropColumn(
                name: "PlanH",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "PlanW",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "PlanX",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "PlanY",
                table: "Rooms");
        }
    }
}
