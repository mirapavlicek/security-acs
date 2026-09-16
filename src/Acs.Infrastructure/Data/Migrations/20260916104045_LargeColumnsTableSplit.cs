using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Velké sloupce se přesunuly do samostatných entit sdílejících řádek (table splitting):
    /// <c>Floors.SchemaImage</c> → <c>FloorSchema</c>, <c>Buildings.SchemaImage</c> → <c>BuildingSchema</c>,
    /// <c>AccessLevels.AccessTree</c> → <c>AccessLevelTree</c>. Schéma databáze se nemění — migrace je
    /// úmyslně bez operací, jen drží snapshot modelu v souladu (EF Core 9 při startu odmítne
    /// migrovat s nezapsanými změnami modelu).
    /// </summary>
    public partial class LargeColumnsTableSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
