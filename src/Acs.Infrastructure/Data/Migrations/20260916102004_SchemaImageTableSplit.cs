using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acs.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Sloupce <c>SchemaImage</c> / <c>SchemaContentType</c> v tabulkách <c>Floors</c> a <c>Buildings</c>
    /// se přesunuly do samostatných entit <c>FloorSchema</c> / <c>BuildingSchema</c> sdílejících řádek
    /// (table splitting). Schéma databáze se nemění — migrace je úmyslně bez operací, jen drží
    /// snapshot modelu v souladu (EF Core 9 při startu odmítne migrovat s nezapsanými změnami modelu).
    /// </summary>
    public partial class SchemaImageTableSplit : Migration
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
