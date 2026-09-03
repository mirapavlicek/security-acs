using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Acs.Tests;

/// <summary>
/// Import čteček z tabulek „čtečky EKV“. Ukázkové tabulky ve Fixtures mají
/// stejné rozvržení jako soubory od projektanta: řádek s názvem, hlavička,
/// mezisoučty za patra a čísla čteček uložená jako čísla, ne text.
/// </summary>
public sealed class EkvReaderImportTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly EkvReaderImportService _importer;

    private readonly Floor _floor2Pp;
    private readonly Floor _floor1NpA;
    private readonly Floor _floor1NpB;

    public EkvReaderImportTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _importer = new EkvReaderImportService(_db, new AuditService(_db));

        var building = new Building { Name = "MOC" };
        _db.Buildings.Add(building);
        _db.SaveChanges();

        _floor2Pp = new Floor { BuildingId = building.Id, Name = "2PP", SortOrder = 1 };
        _floor1NpA = new Floor { BuildingId = building.Id, Name = "1NP A", SortOrder = 3 };
        _floor1NpB = new Floor { BuildingId = building.Id, Name = "1NP B", SortOrder = 4 };
        _db.Floors.AddRange(_floor2Pp, _floor1NpA, _floor1NpB);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static Stream Fixture(string name) => File.OpenRead(Path.Combine(Fixtures, name));

    private Room AddRoom(Floor floor, string name)
    {
        var room = new Room { FloorId = floor.Id, Name = name };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    private Corridor AddCorridor(Floor floor, string name)
    {
        var corridor = new Corridor { FloorId = floor.Id, Name = name };
        _db.Corridors.Add(corridor);
        _db.SaveChanges();
        return corridor;
    }

    /// <summary>Čtečka tak, jak ji zakládal import z výkresů.</summary>
    private Reader AddDrawingReader(string name, Room? room = null, Corridor? corridor = null)
    {
        var reader = new Reader
        {
            Name = name, Description = "Import DPS MOC_2PP.pdf", Source = RecordSource.Manual,
            IsActive = true, RoomId = room?.Id, CorridorId = corridor?.Id,
        };
        _db.Readers.Add(reader);
        _db.SaveChanges();
        return reader;
    }

    // ---------- Čtení tabulky ----------

    [Fact]
    public void Parse_PreskociNazevHlavickuAMezisoucty()
    {
        using var stream = Fixture("ekv-dvere.xlsx");

        var rows = EkvReaderImportService.Parse(stream);

        // 6 čteček; řádky s názvem, hlavičkou, „-02PP“ a „-02PP: 2“ nemají číslo čtečky.
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsLift));
    }

    [Fact]
    public void Parse_CislaJsouTextBezDesetinnehoMista()
    {
        using var stream = Fixture("ekv-dvere.xlsx");

        var first = EkvReaderImportService.Parse(stream)[0];

        Assert.Equal("362001", first.DeviceNumber);
        Assert.Equal("23-02301", first.RoomNumber);
        Assert.Equal("ACS.03", first.Cabinet);
        Assert.Equal("-02PP", first.Floor);
        Assert.Equal("101", first.BuildingObject);
    }

    [Fact]
    public void Parse_StavebniObjektSDesetinnouCastiZustane()
    {
        using var stream = Fixture("ekv-dvere.xlsx");

        var subRoom = EkvReaderImportService.Parse(stream).Single(r => r.RoomNumber == "23-00502/01");

        Assert.Equal("101.02", subRoom.BuildingObject);
        Assert.Equal("servisní vstup", subRoom.Note);
    }

    [Fact]
    public void Parse_DvereAStranaSeOdvodiZCisla()
    {
        var row = new EkvReaderRow("362002", "23-023S2", "-02PP", "ACS.03", null, "101", null, null);

        Assert.Equal("36200", row.DoorNumber);
        Assert.Equal("2", row.Side);
    }

    [Fact]
    public void Parse_VytahovaTabulkaMaJinePoradiSloupcuAFunkciMistoMistnosti()
    {
        using var stream = Fixture("ekv-vytahy.xlsx");

        var rows = EkvReaderImportService.Parse(stream);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsLift));
        var first = rows[0];
        Assert.Equal("341052", first.DeviceNumber);
        Assert.Equal("výtah V01", first.Lift);
        Assert.Equal("blokování volby vybr. stanic", first.Function);
        Assert.Null(first.RoomNumber);
        Assert.Equal("ACS.65.V01", first.Cabinet);
    }

    [Fact]
    public void Parse_TabulkaBezCislaCteckySrozumitelneSelze()
    {
        using var stream = Fixture("ekv-bez-sloupce.xlsx");

        var error = Assert.Throws<InvalidDataException>(() => EkvReaderImportService.Parse(stream));

        Assert.Contains("číslo čtečky EKV", error.Message);
    }

    // ---------- Pomocné funkce ----------

    [Theory]
    [InlineData("23-02306 — TECHNICKÁ MÍSTNOST", "23-02306")]
    [InlineData("23-023S2", "23-023S2")]
    [InlineData("  23-00502/01  ", "23-00502/01")]
    public void Number_VezmeCisloZNazvuMistnosti(string name, string expected)
        => Assert.Equal(expected, EkvReaderImportService.Number(name));

    [Theory]
    [InlineData("23-02306", "2PP")]
    [InlineData("23-01301", "1PP")]
    [InlineData("23-00516", "1NP")]
    [InlineData("23-10125/01", "2NP")]
    [InlineData("23-60002", "TP")]
    [InlineData("střecha SO101", null)]
    public void FloorLabel_DekodujePatroZCislaMistnosti(string number, string? expected)
        => Assert.Equal(expected, EkvReaderImportService.FloorLabel(number));

    // ---------- Import ----------

    [Fact]
    public async Task Import_PrevezmeCteckuZVykresuKdyzSediRozvadecIMistnost()
    {
        var room = AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        var drawing = AddDrawingReader("ACS.03 — 23-02301 — ROZVODNA", room);
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(1, result.ClaimedFromDrawing);
        Assert.Equal(0, result.Created);
        await _db.Entry(drawing).ReloadAsync();
        // Stejný záznam (stejné Id), takže žádosti na něj navázané zůstávají.
        Assert.Equal("362001", drawing.DeviceNumber);
        Assert.Equal("362001 — 23-02301 — ROZVODNA", drawing.Name);
        Assert.Equal("ACS.03", drawing.PanelName);
        Assert.StartsWith("Výkres: ACS.03 — 23-02301 — ROZVODNA.", drawing.Description);
        Assert.True(drawing.IsActive);
    }

    [Fact]
    public async Task Import_CteckuZVykresuSJinymRozvademNeprevezmeADeaktivuje()
    {
        var room = AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        var wrongCabinet = AddDrawingReader("ACS.24 — 23-02301 — ROZVODNA", room);
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Deactivated);
        await _db.Entry(wrongCabinet).ReloadAsync();
        Assert.False(wrongCabinet.IsActive);
        Assert.Contains("deaktivováno importem", wrongCabinet.Description);
        Assert.Equal(1, await _db.Readers.CountAsync(r => r.DeviceNumber == "362001" && r.IsActive));
    }

    [Fact]
    public async Task Import_DveCteckyVJedneMistnostiPrevezmeObeZVykresu()
    {
        var room = AddRoom(_floor1NpA, "23-00516 — STROJOVNA");
        AddDrawingReader("ACS.11 — 23-00516 — STROJOVNA", room);
        AddDrawingReader("ACS.11 — 23-00516 — STROJOVNA (2)", room);
        var rows = new[]
        {
            new EkvReaderRow("363111", "23-00516", "1NP", "ACS.11", null, "101", null, null),
            new EkvReaderRow("363112", "23-00516", "1NP", "ACS.11", null, "101", null, null),
        };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(2, result.ClaimedFromDrawing);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Deactivated);
    }

    [Fact]
    public async Task Import_CteckuNaChodbePrevezmeStejne()
    {
        var corridor = AddCorridor(_floor2Pp, "23-023S2 — SCHODIŠTĚ");
        var drawing = AddDrawingReader("ACS.03 — 23-023S2 — SCHODIŠTĚ", corridor: corridor);
        var rows = new[] { new EkvReaderRow("362002", "23-023S2", "-02PP", "ACS.03", null, "101", null, null) };

        await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        await _db.Entry(drawing).ReloadAsync();
        Assert.Equal("362002", drawing.DeviceNumber);
        Assert.Equal(corridor.Id, drawing.CorridorId);
        Assert.Null(drawing.RoomId);
    }

    [Fact]
    public async Task Import_CastMistnostiSeZalozi_NaPatreZakladniMistnosti()
    {
        var parent = AddRoom(_floor1NpA, "23-00502 — SKLAD");
        var rows = new[] { new EkvReaderRow("363141", "23-00502/01", "1NP", "ACS.11", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(1, result.RoomsCreated);
        var subRoom = await _db.Rooms.SingleAsync(r => r.Name == "23-00502/01");
        Assert.Equal(parent.FloorId, subRoom.FloorId);
        var reader = await _db.Readers.SingleAsync(r => r.DeviceNumber == "363141");
        Assert.Equal(subRoom.Id, reader.RoomId);
    }

    [Fact]
    public async Task Import_NeznamaMistnostSeVypiseACteckaZustaneBezMistnosti()
    {
        var rows = new[] { new EkvReaderRow("363151", "23-99999", "1NP", "ACS.11", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(["23-99999"], result.Unresolved);
        var reader = await _db.Readers.SingleAsync(r => r.DeviceNumber == "363151");
        Assert.Null(reader.RoomId);
        Assert.Null(reader.CorridorId);
        Assert.Contains("nenalezena", reader.Description);
        Assert.Contains("23-99999", result.ToString());
    }

    [Fact]
    public async Task Import_DuplicitniCisloMistnostiVybereZaznamNaPatrePodleCisla()
    {
        // Výkres 1NP ukazuje i místnost z 2PP — v číselníku je dvakrát.
        var real = AddRoom(_floor2Pp, "23-02306 — TECHNICKÁ MÍSTNOST");
        AddRoom(_floor1NpB, "23-02306 — TECHNICKÁ MÍSTNOST");
        var rows = new[] { new EkvReaderRow("362021", "23-02306", "-02PP", "ACS.03", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(1, result.Ambiguous);
        var reader = await _db.Readers.SingleAsync(r => r.DeviceNumber == "362021");
        Assert.Equal(real.Id, reader.RoomId);
    }

    [Fact]
    public async Task Import_DuplicitaNaStejnemPatreDaPrednostCastiSCteckouStejnehoRozvadece()
    {
        // Části A i B téhož patra ukazují tutéž místnost; čtečka ACS.11 byla na listu B.
        AddRoom(_floor1NpA, "23-00516 — STROJOVNA");
        var withEvidence = AddRoom(_floor1NpB, "23-00516 — STROJOVNA");
        AddDrawingReader("ACS.11 — 23-00516 — STROJOVNA", withEvidence);
        var rows = new[] { new EkvReaderRow("363111", "23-00516", "1NP", "ACS.11", null, "101", null, null) };

        await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        var reader = await _db.Readers.SingleAsync(r => r.DeviceNumber == "363111");
        Assert.Equal(withEvidence.Id, reader.RoomId);
    }

    [Fact]
    public async Task Import_VytahovaCteckaJeBezMistnostiANeDeaktivujeDverni()
    {
        var room = AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        var door = AddDrawingReader("ACS.03 — 23-02301 — ROZVODNA", room);
        using var stream = Fixture("ekv-vytahy.xlsx");
        var rows = EkvReaderImportService.Parse(stream);

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(2, result.Created);
        // Výtahová tabulka dveřní čtečky neobsahuje, takže podle ní nesmí nic deaktivovat.
        Assert.Equal(0, result.Deactivated);
        await _db.Entry(door).ReloadAsync();
        Assert.True(door.IsActive);
        var lift = await _db.Readers.SingleAsync(r => r.DeviceNumber == "341052");
        Assert.Equal("341052 — výtah V01 (kabina)", lift.Name);
        Assert.Equal("ACS.65.V01", lift.PanelName);
        Assert.Null(lift.RoomId);
        Assert.Contains("blokování volby", lift.Description);
    }

    [Fact]
    public async Task Import_OpakovanyBehNicNezaklada_AZachovaPuvodZVykresu()
    {
        var room = AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        AddDrawingReader("ACS.03 — 23-02301 — ROZVODNA", room);
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");
        var second = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);
        Assert.Equal(0, second.ClaimedFromDrawing);
        Assert.Equal(1, await _db.Readers.CountAsync());
        var reader = await _db.Readers.SingleAsync();
        Assert.StartsWith("Výkres: ACS.03 — 23-02301 — ROZVODNA.", reader.Description);
    }

    [Fact]
    public async Task Import_NahledNicNeulozi()
    {
        var room = AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        var drawing = AddDrawingReader("ACS.24 — 23-02301 — ROZVODNA", room);
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: true, deactivateUnmatched: true, "test");

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Deactivated);
        // Počty odpovídají ostrému běhu, ale v databázi se nic nezměnilo.
        Assert.Equal(1, await _db.Readers.CountAsync());
        await _db.Entry(drawing).ReloadAsync();
        Assert.True(drawing.IsActive);
        Assert.Null(drawing.DeviceNumber);
        Assert.Equal(0, await _db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Import_VypnutaDeaktivaceNechaCteckyZVykresuBezZmeny()
    {
        var room = AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        var drawing = AddDrawingReader("ACS.24 — 23-02301 — ROZVODNA", room);
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: false, "test");

        Assert.Equal(0, result.Deactivated);
        await _db.Entry(drawing).ReloadAsync();
        Assert.True(drawing.IsActive);
    }

    [Fact]
    public async Task Import_ChybejiciBudovaSrozumitelneSelze()
    {
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _importer.ImportAsync(rows, "NEEXISTUJE", dryRun: false, deactivateUnmatched: true, "test"));

        Assert.Contains("výkresů", error.Message);
    }

    [Fact]
    public async Task Import_ZapiseAudit()
    {
        AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        var rows = new[] { new EkvReaderRow("362001", "23-02301", "-02PP", "ACS.03", null, "101", null, null) };

        await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "spravce");

        var log = await _db.AuditLogs.SingleAsync(a => a.Action == "ekv-readers-imported");
        Assert.Equal("spravce", log.UserName);
        Assert.Contains("nových čteček 1", log.Details);
    }

    [Fact]
    public async Task Import_CelaFixtureProjdeKoncemKeKonci()
    {
        AddRoom(_floor2Pp, "23-02301 — ROZVODNA");
        AddCorridor(_floor2Pp, "23-023S2 — SCHODIŠTĚ");
        AddRoom(_floor1NpA, "23-00502 — SKLAD");
        AddRoom(_floor1NpA, "23-00516 — STROJOVNA");
        using var stream = Fixture("ekv-dvere.xlsx");
        var rows = EkvReaderImportService.Parse(stream);

        var result = await _importer.ImportAsync(rows, "MOC", dryRun: false, deactivateUnmatched: true, "test");

        Assert.Equal(6, result.Rows);
        Assert.Equal(6, result.Created);
        Assert.Equal(1, result.RoomsCreated);
        Assert.Equal(["23-99999"], result.Unresolved);
        Assert.Equal(6, await _db.Readers.CountAsync(r => r.DeviceNumber != null && r.IsActive));
    }
}
