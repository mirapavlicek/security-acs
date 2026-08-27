using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupsAndAutoAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MatrixId",
                table: "ApprovalDecisions",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ReaderId",
                table: "AccessRequestItems",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "CurrentStageOrder",
                table: "AccessRequestItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReaderGroupId",
                table: "AccessRequestItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessRequestItemStages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    MatrixId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRequestItemStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRequestItemStages_AccessRequestItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "AccessRequestItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessRequestItemStages_ApprovalMatrices_MatrixId",
                        column: x => x.MatrixId,
                        principalTable: "ApprovalMatrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ReaderGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ApprovalMatrixId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReaderGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReaderGroups_ApprovalMatrices_ApprovalMatrixId",
                        column: x => x.ApprovalMatrixId,
                        principalTable: "ApprovalMatrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AutoAssignmentRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Department = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReaderGroupId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoAssignmentRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoAssignmentRules_ReaderGroups_ReaderGroupId",
                        column: x => x.ReaderGroupId,
                        principalTable: "ReaderGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ReaderGroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    ReaderId = table.Column<int>(type: "int", nullable: true),
                    ChildGroupId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReaderGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReaderGroupMembers_ReaderGroups_ChildGroupId",
                        column: x => x.ChildGroupId,
                        principalTable: "ReaderGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReaderGroupMembers_ReaderGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ReaderGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReaderGroupMembers_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequestItems_ReaderGroupId",
                table: "AccessRequestItems",
                column: "ReaderGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequestItemStages_ItemId_Order",
                table: "AccessRequestItemStages",
                columns: new[] { "ItemId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequestItemStages_MatrixId",
                table: "AccessRequestItemStages",
                column: "MatrixId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoAssignmentRules_ReaderGroupId",
                table: "AutoAssignmentRules",
                column: "ReaderGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderGroupMembers_ChildGroupId",
                table: "ReaderGroupMembers",
                column: "ChildGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderGroupMembers_GroupId",
                table: "ReaderGroupMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderGroupMembers_ReaderId",
                table: "ReaderGroupMembers",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderGroups_ApprovalMatrixId",
                table: "ReaderGroups",
                column: "ApprovalMatrixId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRequestItems_ReaderGroups_ReaderGroupId",
                table: "AccessRequestItems",
                column: "ReaderGroupId",
                principalTable: "ReaderGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessRequestItems_ReaderGroups_ReaderGroupId",
                table: "AccessRequestItems");

            migrationBuilder.DropTable(
                name: "AccessRequestItemStages");

            migrationBuilder.DropTable(
                name: "AutoAssignmentRules");

            migrationBuilder.DropTable(
                name: "ReaderGroupMembers");

            migrationBuilder.DropTable(
                name: "ReaderGroups");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequestItems_ReaderGroupId",
                table: "AccessRequestItems");

            migrationBuilder.DropColumn(
                name: "MatrixId",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "CurrentStageOrder",
                table: "AccessRequestItems");

            migrationBuilder.DropColumn(
                name: "ReaderGroupId",
                table: "AccessRequestItems");

            migrationBuilder.AlterColumn<int>(
                name: "ReaderId",
                table: "AccessRequestItems",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
