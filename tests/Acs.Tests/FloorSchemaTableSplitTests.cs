using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Obrázek schématu patra / budovy (longblob) je oddělená entita ve stejném řádku: běžné dotazy
/// s <c>Include(Floor)</c> / <c>Include(Building)</c> ho nesmí tahat z DB — právě to plnilo paměť
/// procesu (výpis 1 600 čteček × podklad patra na každém řádku) až k OOM.
/// </summary>
public sealed class FloorSchemaTableSplitTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sql = [];
    private readonly int _floorId;
    private readonly int _buildingId;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    public FloorSchemaTableSplitTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var db = NewContext();
        db.Database.EnsureCreated();

        var building = new Building { Name = "MOC" };
        db.Buildings.Add(building);
        db.SaveChanges();
        _buildingId = building.Id;

        var floor = new Floor { BuildingId = building.Id, Name = "2NP", SortOrder = 2 };
        db.Floors.Add(floor);
        db.SaveChanges();
        _floorId = floor.Id;

        var room = new Room { FloorId = floor.Id, Name = "M2001" };
        db.Rooms.Add(room);
        db.SaveChanges();
        db.Readers.Add(new Reader { Name = "Čtečka M2001", RoomId = room.Id });
        db.SaveChanges();
    }

    private AcsDbContext NewContext() => new(new DbContextOptionsBuilder<AcsDbContext>()
        .UseSqlite(_connection)
        .LogTo(message => _sql.Add(message), [RelationalEventId.CommandExecuted])
        .Options);

    public void Dispose() => _connection.Dispose();

    private async Task UploadAsync()
    {
        using var db = NewContext();
        var floor = await db.Floors.Include(f => f.Schema).FirstAsync(f => f.Id == _floorId);
        floor.Schema ??= new FloorSchema { FloorId = floor.Id };
        floor.Schema.SchemaImage = Png;
        floor.Schema.SchemaContentType = "image/png";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Patro_bez_schematu_ma_Schema_null_a_Where_na_obrazek_ho_nevrati()
    {
        using var db = NewContext();
        var floor = await db.Floors.Include(f => f.Schema).FirstAsync(f => f.Id == _floorId);
        Assert.Null(floor.Schema);
        Assert.False(await db.Floors.AnyAsync(f => f.Id == _floorId && f.Schema!.SchemaImage != null));
    }

    [Fact]
    public async Task Nahrani_schematu_je_UPDATE_stejneho_radku_a_obrazek_jde_precist_zvlast()
    {
        await UploadAsync();

        using var db = NewContext();
        Assert.Equal(1, await db.Floors.CountAsync(f => f.Id == _floorId));
        Assert.True(await db.Floors.AnyAsync(f => f.Id == _floorId && f.Schema!.SchemaImage != null));

        // Endpoint /floors/{id}/schema čte jen dependent entitu — bez patra, budovy a spol.
        var schema = await db.Set<FloorSchema>().AsNoTracking().FirstOrDefaultAsync(s => s.FloorId == _floorId);
        Assert.NotNull(schema);
        Assert.Equal(Png, schema.SchemaImage);
        Assert.Equal("image/png", schema.SchemaContentType);
    }

    [Fact]
    public async Task Opakovane_nahrani_prepise_obrazek_bez_duplicity()
    {
        await UploadAsync();

        using (var db = NewContext())
        {
            var floor = await db.Floors.Include(f => f.Schema).FirstAsync(f => f.Id == _floorId);
            floor.Schema!.SchemaImage = [9, 9, 9];
            floor.Schema.SchemaContentType = "image/jpeg";
            await db.SaveChangesAsync();
        }

        using var check = NewContext();
        var schema = await check.Set<FloorSchema>().SingleAsync(s => s.FloorId == _floorId);
        Assert.Equal([9, 9, 9], schema.SchemaImage);
        Assert.Equal("image/jpeg", schema.SchemaContentType);
    }

    [Fact]
    public async Task Vypis_ctecek_s_Include_patra_a_budovy_netaha_obrazky()
    {
        await UploadAsync();
        _sql.Clear();

        using var db = NewContext();
        var readers = await db.Readers
            .Include(r => r.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .ToListAsync();
        var floors = await db.Floors.Include(f => f.Building).ToListAsync();

        Assert.Single(readers);
        Assert.Single(floors);
        Assert.NotNull(readers[0].Room!.Floor!.Building);
        Assert.Null(floors[0].Schema);

        var executed = string.Join("\n", _sql);
        Assert.DoesNotContain("SchemaImage", executed);
        Assert.DoesNotContain("SchemaContentType", executed);
    }

    [Fact]
    public async Task Include_Schema_obrazek_nacte()
    {
        await UploadAsync();
        _sql.Clear();

        using var db = NewContext();
        var floor = await db.Floors.Include(f => f.Schema).FirstAsync(f => f.Id == _floorId);

        Assert.NotNull(floor.Schema);
        Assert.Equal(Png, floor.Schema.SchemaImage);
        Assert.Contains("SchemaImage", string.Join("\n", _sql));
    }

    [Fact]
    public async Task Smazani_patra_se_schematem_projde()
    {
        await UploadAsync();

        using (var db = NewContext())
        {
            var floor = await db.Floors.FirstAsync(f => f.Id == _floorId);
            db.Rooms.RemoveRange(db.Rooms.Where(r => r.FloorId == _floorId));
            db.Readers.RemoveRange(db.Readers.Where(r => r.Room!.FloorId == _floorId));
            db.Floors.Remove(floor);
            await db.SaveChangesAsync();
        }

        using var check = NewContext();
        Assert.False(await check.Floors.AnyAsync(f => f.Id == _floorId));
        Assert.False(await check.Set<FloorSchema>().AnyAsync(s => s.FloorId == _floorId));
        Assert.True(await check.Buildings.AnyAsync(b => b.Id == _buildingId));
    }
}
