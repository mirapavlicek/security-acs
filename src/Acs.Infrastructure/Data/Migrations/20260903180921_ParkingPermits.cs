using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ParkingPermits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParkingPermitId",
                table: "AccessRequestItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ParkingPermitTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Binding = table.Column<int>(type: "int", nullable: false),
                    ApprovalMatrixId = table.Column<int>(type: "int", nullable: true),
                    MaxPlates = table.Column<int>(type: "int", nullable: false),
                    DefaultValidityMonths = table.Column<int>(type: "int", nullable: true),
                    AllSitesByDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PrintsWindshieldCard = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CardTitle = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CardScopeText = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingPermitTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingPermitTypes_ApprovalMatrices_ApprovalMatrixId",
                        column: x => x.ApprovalMatrixId,
                        principalTable: "ApprovalMatrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ApprovalMatrixId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sites_ApprovalMatrices_ApprovalMatrixId",
                        column: x => x.ApprovalMatrixId,
                        principalTable: "ApprovalMatrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ParkingPermits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    PermitTypeId = table.Column<int>(type: "int", nullable: false),
                    FunctionTitle = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AllSites = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PermitNumber = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IssuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedByUserId = table.Column<int>(type: "int", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevokeReason = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingPermits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingPermits_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParkingPermits_ParkingPermitTypes_PermitTypeId",
                        column: x => x.PermitTypeId,
                        principalTable: "ParkingPermitTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParkingPermits_Users_IssuedByUserId",
                        column: x => x.IssuedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ParkingPermitPlates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PermitId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Note = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EmployeeIdentifierId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingPermitPlates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingPermitPlates_EmployeeIdentifiers_EmployeeIdentifierId",
                        column: x => x.EmployeeIdentifierId,
                        principalTable: "EmployeeIdentifiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ParkingPermitPlates_ParkingPermits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "ParkingPermits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ParkingPermitSites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PermitId = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingPermitSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingPermitSites_ParkingPermits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "ParkingPermits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParkingPermitSites_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequestItems_ParkingPermitId",
                table: "AccessRequestItems",
                column: "ParkingPermitId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermitPlates_EmployeeIdentifierId",
                table: "ParkingPermitPlates",
                column: "EmployeeIdentifierId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermitPlates_PermitId_Value",
                table: "ParkingPermitPlates",
                columns: new[] { "PermitId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermitPlates_Value",
                table: "ParkingPermitPlates",
                column: "Value");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermits_EmployeeId",
                table: "ParkingPermits",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermits_IssuedByUserId",
                table: "ParkingPermits",
                column: "IssuedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermits_PermitNumber",
                table: "ParkingPermits",
                column: "PermitNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermits_PermitTypeId",
                table: "ParkingPermits",
                column: "PermitTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermitSites_PermitId_SiteId",
                table: "ParkingPermitSites",
                columns: new[] { "PermitId", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermitSites_SiteId",
                table: "ParkingPermitSites",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingPermitTypes_ApprovalMatrixId",
                table: "ParkingPermitTypes",
                column: "ApprovalMatrixId");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_ApprovalMatrixId",
                table: "Sites",
                column: "ApprovalMatrixId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRequestItems_ParkingPermits_ParkingPermitId",
                table: "AccessRequestItems",
                column: "ParkingPermitId",
                principalTable: "ParkingPermits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessRequestItems_ParkingPermits_ParkingPermitId",
                table: "AccessRequestItems");

            migrationBuilder.DropTable(
                name: "ParkingPermitPlates");

            migrationBuilder.DropTable(
                name: "ParkingPermitSites");

            migrationBuilder.DropTable(
                name: "ParkingPermits");

            migrationBuilder.DropTable(
                name: "Sites");

            migrationBuilder.DropTable(
                name: "ParkingPermitTypes");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequestItems_ParkingPermitId",
                table: "AccessRequestItems");

            migrationBuilder.DropColumn(
                name: "ParkingPermitId",
                table: "AccessRequestItems");
        }
    }
}
