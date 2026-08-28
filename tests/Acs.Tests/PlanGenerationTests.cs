using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Plans;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Generování plánu patra z dat, která už v systému jsou. Klíčové je, že se
/// prvky vejdou do plochy 0–100 %, nepřekrývají se a že „generuj chybějící“
/// nepřepíše ruční práci.
/// </summary>
public sealed class PlanGenerationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly PlanGenerationService _generator;

    private Building _building = null!;
    private Floor _floor = null!;

    public PlanGenerationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _generator = new PlanGenerationService(_db, new AuditService(_db));

        _building = new Building { Name = "MOC" };
        _db.Buildings.Add(_building);
        _db.SaveChanges();

        _floor = new Floor { BuildingId = _building.Id, Name = "2NP", SortOrder = 2 };
        _db.Floors.Add(_floor);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Corridor AddCorridor(string name)
    {
        var corridor = new Corridor { FloorId = _floor.Id, Name = name };
        _db.Corridors.Add(corridor);
        _db.SaveChanges();
        return corridor;
    }

    private Room AddRoom(string name, int? corridorId = null, double? sourceX = null, double? sourceY = null)
    {
        var room = new Room
        {
            FloorId = _floor.Id, Name = name, CorridorId = corridorId,
            SourceX = sourceX, SourceY = sourceY,
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    private Reader AddReader(string name, int? roomId = null, int? corridorId = null,
        double? sourceX = null, double? sourceY = null)
    {
        var reader = new Reader
        {
            Name = name, RoomId = roomId, CorridorId = corridorId,
            SourceX = sourceX, SourceY = sourceY,
        };
        _db.Readers.Add(reader);
        _db.SaveChanges();
        return reader;
    }

    private static void AssertInsidePlan(Room room)
    {
        Assert.NotNull(room.PlanX);
        Assert.NotNull(room.PlanY);
        Assert.InRange(room.PlanX!.Value, 0, 100);
        Assert.InRange(room.PlanY!.Value, 0, 100);
        Assert.InRange(room.PlanX.Value + room.PlanW!.Value, 0, 100.001);
        Assert.InRange(room.PlanY.Value + room.PlanH!.Value, 0, 100.001);
    }

    // ---------- Podle výkresů ----------

    [Fact]
    public async Task Z_vykresu_se_zachova_vzajemna_poloha_mistnosti()
    {
        // Souřadnice výkresu jsou v bodech PDF a mezi patry nesrovnatelné.
        var left = AddRoom("M2001", sourceX: 200, sourceY: 1000);
        var right = AddRoom("M2002", sourceX: 4000, sourceY: 1000);
        var below = AddRoom("M2003", sourceX: 200, sourceY: 3000);

        var result = await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");

        Assert.Equal(PlanGenerationMode.FromDrawing, result.Mode);
        Assert.Equal(3, result.RoomsPlaced);

        await _db.Entry(left).ReloadAsync();
        await _db.Entry(right).ReloadAsync();
        await _db.Entry(below).ReloadAsync();

        // Vlevo zůstane vlevo, výš zůstane výš.
        Assert.True(left.PlanX < right.PlanX);
        Assert.True(left.PlanY < below.PlanY);
        foreach (var room in new[] { left, right, below })
            AssertInsidePlan(room);
    }

    [Fact]
    public async Task Krajni_mistnosti_zustanou_uvnitr_plochy_i_s_okrajem()
    {
        AddRoom("M1", sourceX: 0, sourceY: 0);
        AddRoom("M2", sourceX: 5000, sourceY: 5000);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");

        foreach (var room in await _db.Rooms.ToListAsync())
            AssertInsidePlan(room);
    }

    [Fact]
    public async Task Vlastni_poloha_ctecky_ma_prednost_pred_stredem_mistnosti()
    {
        AddRoom("M2001", sourceX: 200, sourceY: 200);
        // Čtečka patří do místnosti v pravém dolním rohu, ale ve výkresu je vlevo nahoře
        // (typicky dveře na druhém konci chodby) — plán musí respektovat výkres.
        var farRoom = AddRoom("M2002", sourceX: 4000, sourceY: 4000);
        var reader = AddReader("ACS.01 — M2002", roomId: farRoom.Id, sourceX: 200, sourceY: 200);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");
        await _db.Entry(farRoom).ReloadAsync();
        await _db.Entry(reader).ReloadAsync();

        Assert.NotNull(reader.SchemaX);
        Assert.InRange(reader.SchemaX!.Value, 0, 20);
        Assert.InRange(reader.SchemaY!.Value, 0, 20);
        // Kdyby se použil střed místnosti, čtečka by skončila u pravého dolního okraje.
        Assert.True(reader.SchemaX < farRoom.PlanX);
    }

    [Fact]
    public async Task Ctecka_bez_vazby_na_patro_se_negeneruje()
    {
        AddRoom("M2001", sourceX: 200, sourceY: 200);
        // Čtečka bez místnosti i chodby nepatří na žádné patro, plán ji nemá kam dát.
        var orphan = AddReader("ACS.99 — neznámé", sourceX: 200, sourceY: 200);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");
        await _db.Entry(orphan).ReloadAsync();

        Assert.Null(orphan.SchemaX);
    }

    [Fact]
    public async Task Ctecka_bez_polohy_se_polozi_do_sve_mistnosti()
    {
        var room = AddRoom("M2001", sourceX: 1000, sourceY: 1000);
        AddRoom("M2002", sourceX: 4000, sourceY: 4000);
        var reader = AddReader("ACS.02 — M2001", roomId: room.Id);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");
        await _db.Entry(room).ReloadAsync();
        await _db.Entry(reader).ReloadAsync();

        Assert.Equal(room.PlanX! + room.PlanW! / 2, reader.SchemaX);
        Assert.Equal(room.PlanY! + room.PlanH! / 2, reader.SchemaY);
    }

    [Fact]
    public async Task Mistnosti_na_stejnem_miste_vykresu_neskonci_mimo_plan()
    {
        // Degenerovaný případ: všechny popisky ve stejném bodě.
        AddRoom("M1", sourceX: 500, sourceY: 500);
        AddRoom("M2", sourceX: 500, sourceY: 500);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");

        foreach (var room in await _db.Rooms.ToListAsync())
            AssertInsidePlan(room);
    }

    // ---------- Schéma podle chodeb ----------

    [Fact]
    public async Task Bez_vykresu_se_mistnosti_serazuji_po_chodbach()
    {
        var first = AddCorridor("A200");
        var second = AddCorridor("A300");
        var a1 = AddRoom("M2001", first.Id);
        var a2 = AddRoom("M2002", first.Id);
        var b1 = AddRoom("M3001", second.Id);

        var result = await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");

        Assert.Equal(PlanGenerationMode.Schematic, result.Mode);
        await _db.Entry(a1).ReloadAsync();
        await _db.Entry(a2).ReloadAsync();
        await _db.Entry(b1).ReloadAsync();

        // Místnosti jedné chodby jsou v jednom pásu, druhá chodba je níž.
        Assert.Equal(a1.PlanY, a2.PlanY);
        Assert.True(a1.PlanY < b1.PlanY);
        Assert.True(a1.PlanX < a2.PlanX);
    }

    [Fact]
    public async Task Schematicke_mistnosti_se_neprekryvaji()
    {
        var corridor = AddCorridor("A200");
        for (var i = 1; i <= 12; i++)
            AddRoom($"M2{i:000}", corridor.Id);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");

        var rooms = await _db.Rooms.OrderBy(r => r.Name).ToListAsync();
        foreach (var room in rooms)
            AssertInsidePlan(room);

        foreach (var (a, b) in rooms.SelectMany(a => rooms.Select(b => (a, b))).Where(p => p.a.Id < p.b.Id))
            Assert.False(Overlaps(a, b), $"{a.Name} a {b.Name} se překrývají");
    }

    private static bool Overlaps(Room a, Room b)
    {
        const double tolerance = 0.01;
        return a.PlanX! + a.PlanW! > b.PlanX! + tolerance
               && b.PlanX! + b.PlanW! > a.PlanX! + tolerance
               && a.PlanY! + a.PlanH! > b.PlanY! + tolerance
               && b.PlanY! + b.PlanH! > a.PlanY! + tolerance;
    }

    [Fact]
    public async Task Ctecka_chodby_se_umisti_do_pasu_sve_chodby()
    {
        var corridor = AddCorridor("A200");
        AddRoom("M2001", corridor.Id);
        var reader = AddReader("ACS.10 — A200", corridorId: corridor.Id);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");
        await _db.Entry(reader).ReloadAsync();

        Assert.NotNull(reader.SchemaX);
        Assert.InRange(reader.SchemaX!.Value, 0, 100);
        Assert.InRange(reader.SchemaY!.Value, 0, 100);
    }

    // ---------- Chování tlačítek ----------

    [Fact]
    public async Task Generuj_chybejici_nechá_rucni_pozice_beze_zmeny()
    {
        var manual = AddRoom("M2001", sourceX: 200, sourceY: 200);
        manual.PlanX = 70;
        manual.PlanY = 80;
        manual.PlanW = 5;
        manual.PlanH = 5;
        var empty = AddRoom("M2002", sourceX: 4000, sourceY: 4000);
        _db.SaveChanges();

        var result = await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: true, "test");

        await _db.Entry(manual).ReloadAsync();
        await _db.Entry(empty).ReloadAsync();

        Assert.Equal(70, manual.PlanX);
        Assert.Equal(80, manual.PlanY);
        Assert.NotNull(empty.PlanX);
        Assert.Equal(1, result.RoomsPlaced);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Generuj_cele_patro_prerovna_i_rucne_umistene()
    {
        var manual = AddRoom("M2001", sourceX: 200, sourceY: 200);
        manual.PlanX = 70;
        manual.PlanY = 80;
        _db.SaveChanges();
        AddRoom("M2002", sourceX: 4000, sourceY: 4000);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");
        await _db.Entry(manual).ReloadAsync();

        Assert.NotEqual(70, manual.PlanX);
    }

    [Fact]
    public async Task Doplneni_pouziva_stejne_meritko_jako_uz_umistene_prvky()
    {
        // Ohraničující obdélník se počítá ze všech prvků patra, ne jen z doplňovaných.
        AddRoom("M2001", sourceX: 200, sourceY: 200);
        var far = AddRoom("M2002", sourceX: 4000, sourceY: 200);
        far.PlanX = 90;
        far.PlanY = 5;
        _db.SaveChanges();
        var middle = AddRoom("M2003", sourceX: 2100, sourceY: 200);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: true, "test");
        await _db.Entry(middle).ReloadAsync();

        // Prostřední místnost musí skončit uprostřed, ne na kraji.
        Assert.InRange(middle.PlanX!.Value, 30, 70);
    }

    [Fact]
    public async Task Prazdne_patro_nic_negeneruje()
    {
        var result = await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "test");

        Assert.Equal(PlanGenerationMode.Empty, result.Mode);
        Assert.Equal(0, result.RoomsPlaced);
    }

    [Fact]
    public async Task Neznamé_patro_vyhodi_srozumitelnou_chybu()
        => await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _generator.GenerateFloorAsync(9999, onlyEmpty: false, "test"));

    // ---------- Celá budova ----------

    [Fact]
    public async Task Generovani_za_budovu_projde_vsechna_patra()
    {
        AddRoom("M2001", sourceX: 200, sourceY: 200);
        AddRoom("M2002", sourceX: 4000, sourceY: 4000);

        var otherFloor = new Floor { BuildingId = _building.Id, Name = "3NP", SortOrder = 3 };
        _db.Floors.Add(otherFloor);
        _db.SaveChanges();
        var corridor = new Corridor { FloorId = otherFloor.Id, Name = "A300" };
        _db.Corridors.Add(corridor);
        _db.SaveChanges();
        _db.Rooms.Add(new Room { FloorId = otherFloor.Id, Name = "M3001", CorridorId = corridor.Id });
        _db.SaveChanges();

        var result = await _generator.GenerateBuildingAsync(_building.Id, onlyEmpty: false, "test");

        Assert.Equal(2, result.Floors.Count);
        Assert.Equal(3, result.RoomsPlaced);
        // Jedno patro má výkresy, druhé jen strukturu.
        Assert.Equal(1, result.FromDrawing);
        Assert.Contains(result.Floors, f => f.Mode == PlanGenerationMode.Schematic);
    }

    [Fact]
    public async Task Generovani_se_zapise_do_auditu()
    {
        AddRoom("M2001", sourceX: 200, sourceY: 200);

        await _generator.GenerateFloorAsync(_floor.Id, onlyEmpty: false, "spravce");

        var log = await _db.AuditLogs.SingleAsync(a => a.Action == "floor-plan-generated");
        Assert.Equal("spravce", log.UserName);
        Assert.Contains("2NP", log.Details);
        Assert.Contains("z výkresů", log.Details);
    }
}
