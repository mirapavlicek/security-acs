using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "Rooms",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleEmployeeId",
                table: "Rooms",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "Readers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleEmployeeId",
                table: "Readers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "ReaderGroups",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleEmployeeId",
                table: "ReaderGroups",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "Floors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleEmployeeId",
                table: "Floors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "Employees",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OrgUnitManual",
                table: "Employees",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Rank",
                table: "Employees",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RankManual",
                table: "Employees",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "Corridors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleEmployeeId",
                table: "Corridors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                table: "Buildings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleEmployeeId",
                table: "Buildings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveWhenNoLevels",
                table: "ApprovalMatrices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TreatUnknownUnitAsOutside",
                table: "ApprovalMatrices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AppliesToRanks",
                table: "ApprovalLevels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "ApprovalLevels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AutoApproved",
                table: "AccessRequestItems",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SecurityRequestId",
                table: "AccessRequestItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrgUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    HeadEmployeeId = table.Column<int>(type: "int", nullable: true),
                    DepartmentPatterns = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgUnits_Employees_HeadEmployeeId",
                        column: x => x.HeadEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrgUnits_OrgUnits_ParentId",
                        column: x => x.ParentId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SecurityRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BuildingId = table.Column<int>(type: "int", nullable: true),
                    FloorId = table.Column<int>(type: "int", nullable: true),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    LocationText = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrgUnitId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ImplementedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ImplementedByUserId = table.Column<int>(type: "int", nullable: true),
                    ImplementationNote = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityRequests_Buildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "Buildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityRequests_Floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "Floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityRequests_OrgUnits_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityRequests_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityRequests_Users_ImplementedByUserId",
                        column: x => x.ImplementedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_OrgUnitId",
                table: "Rooms",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_ResponsibleEmployeeId",
                table: "Rooms",
                column: "ResponsibleEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_OrgUnitId",
                table: "Readers",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_ResponsibleEmployeeId",
                table: "Readers",
                column: "ResponsibleEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderGroups_OrgUnitId",
                table: "ReaderGroups",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderGroups_ResponsibleEmployeeId",
                table: "ReaderGroups",
                column: "ResponsibleEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Floors_OrgUnitId",
                table: "Floors",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Floors_ResponsibleEmployeeId",
                table: "Floors",
                column: "ResponsibleEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_OrgUnitId",
                table: "Employees",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Corridors_OrgUnitId",
                table: "Corridors",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Corridors_ResponsibleEmployeeId",
                table: "Corridors",
                column: "ResponsibleEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Buildings_OrgUnitId",
                table: "Buildings",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Buildings_ResponsibleEmployeeId",
                table: "Buildings",
                column: "ResponsibleEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequestItems_SecurityRequestId",
                table: "AccessRequestItems",
                column: "SecurityRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_HeadEmployeeId",
                table: "OrgUnits",
                column: "HeadEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_ParentId",
                table: "OrgUnits",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRequests_BuildingId",
                table: "SecurityRequests",
                column: "BuildingId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRequests_FloorId",
                table: "SecurityRequests",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRequests_ImplementedByUserId",
                table: "SecurityRequests",
                column: "ImplementedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRequests_OrgUnitId",
                table: "SecurityRequests",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRequests_RoomId",
                table: "SecurityRequests",
                column: "RoomId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRequestItems_SecurityRequests_SecurityRequestId",
                table: "AccessRequestItems",
                column: "SecurityRequestId",
                principalTable: "SecurityRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Buildings_Employees_ResponsibleEmployeeId",
                table: "Buildings",
                column: "ResponsibleEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Buildings_OrgUnits_OrgUnitId",
                table: "Buildings",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Corridors_Employees_ResponsibleEmployeeId",
                table: "Corridors",
                column: "ResponsibleEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Corridors_OrgUnits_OrgUnitId",
                table: "Corridors",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_OrgUnits_OrgUnitId",
                table: "Employees",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Floors_Employees_ResponsibleEmployeeId",
                table: "Floors",
                column: "ResponsibleEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Floors_OrgUnits_OrgUnitId",
                table: "Floors",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ReaderGroups_Employees_ResponsibleEmployeeId",
                table: "ReaderGroups",
                column: "ResponsibleEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ReaderGroups_OrgUnits_OrgUnitId",
                table: "ReaderGroups",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Readers_Employees_ResponsibleEmployeeId",
                table: "Readers",
                column: "ResponsibleEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Readers_OrgUnits_OrgUnitId",
                table: "Readers",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Rooms_Employees_ResponsibleEmployeeId",
                table: "Rooms",
                column: "ResponsibleEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Rooms_OrgUnits_OrgUnitId",
                table: "Rooms",
                column: "OrgUnitId",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessRequestItems_SecurityRequests_SecurityRequestId",
                table: "AccessRequestItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Buildings_Employees_ResponsibleEmployeeId",
                table: "Buildings");

            migrationBuilder.DropForeignKey(
                name: "FK_Buildings_OrgUnits_OrgUnitId",
                table: "Buildings");

            migrationBuilder.DropForeignKey(
                name: "FK_Corridors_Employees_ResponsibleEmployeeId",
                table: "Corridors");

            migrationBuilder.DropForeignKey(
                name: "FK_Corridors_OrgUnits_OrgUnitId",
                table: "Corridors");

            migrationBuilder.DropForeignKey(
                name: "FK_Employees_OrgUnits_OrgUnitId",
                table: "Employees");

            migrationBuilder.DropForeignKey(
                name: "FK_Floors_Employees_ResponsibleEmployeeId",
                table: "Floors");

            migrationBuilder.DropForeignKey(
                name: "FK_Floors_OrgUnits_OrgUnitId",
                table: "Floors");

            migrationBuilder.DropForeignKey(
                name: "FK_ReaderGroups_Employees_ResponsibleEmployeeId",
                table: "ReaderGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_ReaderGroups_OrgUnits_OrgUnitId",
                table: "ReaderGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_Readers_Employees_ResponsibleEmployeeId",
                table: "Readers");

            migrationBuilder.DropForeignKey(
                name: "FK_Readers_OrgUnits_OrgUnitId",
                table: "Readers");

            migrationBuilder.DropForeignKey(
                name: "FK_Rooms_Employees_ResponsibleEmployeeId",
                table: "Rooms");

            migrationBuilder.DropForeignKey(
                name: "FK_Rooms_OrgUnits_OrgUnitId",
                table: "Rooms");

            migrationBuilder.DropTable(
                name: "SecurityRequests");

            migrationBuilder.DropTable(
                name: "OrgUnits");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_OrgUnitId",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_ResponsibleEmployeeId",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Readers_OrgUnitId",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_Readers_ResponsibleEmployeeId",
                table: "Readers");

            migrationBuilder.DropIndex(
                name: "IX_ReaderGroups_OrgUnitId",
                table: "ReaderGroups");

            migrationBuilder.DropIndex(
                name: "IX_ReaderGroups_ResponsibleEmployeeId",
                table: "ReaderGroups");

            migrationBuilder.DropIndex(
                name: "IX_Floors_OrgUnitId",
                table: "Floors");

            migrationBuilder.DropIndex(
                name: "IX_Floors_ResponsibleEmployeeId",
                table: "Floors");

            migrationBuilder.DropIndex(
                name: "IX_Employees_OrgUnitId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Corridors_OrgUnitId",
                table: "Corridors");

            migrationBuilder.DropIndex(
                name: "IX_Corridors_ResponsibleEmployeeId",
                table: "Corridors");

            migrationBuilder.DropIndex(
                name: "IX_Buildings_OrgUnitId",
                table: "Buildings");

            migrationBuilder.DropIndex(
                name: "IX_Buildings_ResponsibleEmployeeId",
                table: "Buildings");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequestItems_SecurityRequestId",
                table: "AccessRequestItems");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "ResponsibleEmployeeId",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "ResponsibleEmployeeId",
                table: "Readers");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "ReaderGroups");

            migrationBuilder.DropColumn(
                name: "ResponsibleEmployeeId",
                table: "ReaderGroups");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "Floors");

            migrationBuilder.DropColumn(
                name: "ResponsibleEmployeeId",
                table: "Floors");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "OrgUnitManual",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Rank",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "RankManual",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "Corridors");

            migrationBuilder.DropColumn(
                name: "ResponsibleEmployeeId",
                table: "Corridors");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                table: "Buildings");

            migrationBuilder.DropColumn(
                name: "ResponsibleEmployeeId",
                table: "Buildings");

            migrationBuilder.DropColumn(
                name: "AutoApproveWhenNoLevels",
                table: "ApprovalMatrices");

            migrationBuilder.DropColumn(
                name: "TreatUnknownUnitAsOutside",
                table: "ApprovalMatrices");

            migrationBuilder.DropColumn(
                name: "AppliesToRanks",
                table: "ApprovalLevels");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "ApprovalLevels");

            migrationBuilder.DropColumn(
                name: "AutoApproved",
                table: "AccessRequestItems");

            migrationBuilder.DropColumn(
                name: "SecurityRequestId",
                table: "AccessRequestItems");
        }
    }
}
