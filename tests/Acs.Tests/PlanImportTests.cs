using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Import struktury budovy z DPS výkresů (JSON z extract.py).</summary>
public sealed class PlanImportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly PlanImportService _importer;

    private const string SampleJson = """
    [
      {
        "file": "MOC_4NP_A.pdf", "floor": "4NP", "section": "A",
        "rooms": [
          {"number":"23-30501","numberDashed":"23-30501","numberDotted":"04.5.01",
           "name":"CHODBA","isCorridor":true,"x":100,"y":100},
          {"number":"23-30504","numberDashed":"23-30504","numberDotted":"04.5.04",
           "name":"SKLAD, ČISTÉ PRÁDLO","isCorridor":false,"x":200,"y":100},
          {"number":"04.5.92","numberDashed":null,"numberDotted":"04.5.92",
           "name":"INSTALAČNÍ JÁDRO","isCorridor":false,"x":300,"y":100}
        ],
        "readers": [
          {"code":"ACS.41","x":205,"y":105,"room":"23-30501","roomDistance":12.0,
           "roomNonCorridor":"23-30504"},
          {"code":"ACS.41","x":210,"y":110,"room":"23-30504","roomDistance":8.0,
           "roomNonCorridor":"23-30504"},
          {"code":"ACS.42","x":102,"y":102,"room":"23-30501","roomDistance":3.0,
           "roomNonCorridor":null}
        ]
      },
      {
        "file": "MOC_1PP.pdf", "floor": "1PP", "section": null,
        "rooms": [
          {"number":"23-01308","numberDashed":"23-01308","numberDotted":"1S.3.10",
           "name":"KORIDOR","isCorridor":true,"x":50,"y":50}
        ],
        "readers": []
      }
    ]
    """;

    public PlanImportTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _importer = new PlanImportService(_db, new AuditService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static List<PlanFloor> Sample()
        => PlanImportService.Parse(new MemoryStream(Encoding.UTF8.GetBytes(SampleJson)));

    [Fact]
    public void Parse_ReadsFloorsRoomsAndReaders()
    {
        var plan = Sample();
        Assert.Equal(2, plan.Count);
        Assert.Equal("4NP", plan[0].Floor);
        Assert.Equal("A", plan[0].Section);
        Assert.Equal(3, plan[0].Rooms.Count);
        Assert.Equal(3, plan[0].Readers.Count);
    }

    [Fact]
    public async Task Import_CreatesBuildingSectionsFloorsCorridorsRoomsReaders()
    {
        var result = await _importer.ImportAsync(Sample(), "MOC", dryRun: false,
            preferNonCorridor: true, userName: "test");

        Assert.Equal(1, result.Sections);          // část A (1PP je bez části)
        Assert.Equal(2, result.Floors);
        Assert.Equal(2, result.Corridors);         // CHODBA + KORIDOR
        Assert.Equal(2, result.Rooms);             // 23-30504 + 04.5.92
        Assert.Equal(3, result.Readers);

        var building = await _db.Buildings.SingleAsync();
        Assert.Equal("MOC", building.Name);
        Assert.Equal("4NP A", (await _db.Floors.OrderBy(f => f.SortOrder).LastAsync()).Name);
        // Suterén se řadí před nadzemní podlaží.
        Assert.Equal("1PP", (await _db.Floors.OrderBy(f => f.SortOrder).FirstAsync()).Name);
    }

    [Fact]
    public async Task Import_UsesDashedNumberAndKeepsRoomName()
    {
        await _importer.ImportAsync(Sample(), "MOC", false, true, "test");

        var room = await _db.Rooms.SingleAsync(r => r.Name.StartsWith("23-30504"));
        Assert.Equal("23-30504 — SKLAD, ČISTÉ PRÁDLO", room.Name);
        Assert.Equal("SKLAD, ČISTÉ PRÁDLO", room.Description);
    }

    [Fact]
    public async Task Import_FallsBackToDottedNumber_WhenDashedMissing()
    {
        await _importer.ImportAsync(Sample(), "MOC", false, true, "test");
        Assert.True(await _db.Rooms.AnyAsync(r => r.Name == "04.5.92 — INSTALAČNÍ JÁDRO"));
    }

    [Fact]
    public async Task Import_PrefersNonCorridorRoomForReaders()
    {
        await _importer.ImportAsync(Sample(), "MOC", false, preferNonCorridor: true, userName: "test");

        var room = await _db.Rooms.SingleAsync(r => r.Name.StartsWith("23-30504"));
        var atRoom = await _db.Readers.CountAsync(r => r.RoomId == room.Id);
        Assert.Equal(2, atRoom); // obě ACS.41 patří ke dveřím místnosti, ne k chodbě

        // Čtečka bez nechodbové alternativy zůstane na chodbě.
        var corridor = await _db.Corridors.SingleAsync(c => c.Name.StartsWith("23-30501"));
        Assert.Equal(1, await _db.Readers.CountAsync(r => r.CorridorId == corridor.Id));
    }

    [Fact]
    public async Task Import_DuplicateReaderCodes_GetUniqueNames()
    {
        await _importer.ImportAsync(Sample(), "MOC", false, true, "test");
        var names = await _db.Readers.Select(r => r.Name).ToListAsync();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains(names, n => n.StartsWith("ACS.41 — 23-30504") && n.EndsWith("(2)"));
    }

    /// <summary>
    /// Souřadnice z výkresu jsou jediný podklad, ze kterého se dá vygenerovat plán
    /// odpovídající skutečné budově — import je proto musí uložit.
    /// </summary>
    [Fact]
    public async Task Import_StoresDrawingCoordinates()
    {
        await _importer.ImportAsync(Sample(), "MOC", false, true, "test");

        var room = await _db.Rooms.SingleAsync(r => r.Name.StartsWith("23-30504"));
        Assert.Equal(200, room.SourceX);
        Assert.Equal(100, room.SourceY);

        var reader = await _db.Readers.FirstAsync(r => r.Name.StartsWith("ACS.42"));
        Assert.Equal(102, reader.SourceX);
        Assert.Equal(102, reader.SourceY);

        // Interaktivní plán zůstává prázdný, dokud ho někdo nevygeneruje nebo neupraví.
        Assert.Null(room.PlanX);
        Assert.Null(reader.SchemaX);
    }

    [Fact]
    public async Task DryRun_ChangesNothing()
    {
        var result = await _importer.ImportAsync(Sample(), "MOC", dryRun: true,
            preferNonCorridor: true, userName: "test");

        Assert.True(result.DryRun);
        Assert.Equal(2, result.Rooms);            // počty odpovídají skutečnému importu
        Assert.False(await _db.Buildings.AnyAsync());
        Assert.False(await _db.Rooms.AnyAsync());
        Assert.False(await _db.Readers.AnyAsync());
    }

    [Fact]
    public async Task Import_IsIdempotent()
    {
        await _importer.ImportAsync(Sample(), "MOC", false, true, "test");
        var afterFirst = (await _db.Rooms.CountAsync(), await _db.Readers.CountAsync(),
                          await _db.Corridors.CountAsync(), await _db.Buildings.CountAsync());

        var second = await _importer.ImportAsync(Sample(), "MOC", false, true, "test");

        Assert.Equal(0, second.Rooms);            // nic nového nevzniklo
        Assert.Equal(0, second.Readers);
        Assert.Equal(afterFirst, (await _db.Rooms.CountAsync(), await _db.Readers.CountAsync(),
                                  await _db.Corridors.CountAsync(), await _db.Buildings.CountAsync()));
    }
}
