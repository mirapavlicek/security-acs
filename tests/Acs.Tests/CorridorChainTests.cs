using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Systém chodeb: budova MOC → část G → patro P → chodby A100 → A200 → místnost M1000.
/// Žádost o čtečku místnosti automaticky přidá čtečky celého řetězu chodeb.
/// </summary>
public sealed class CorridorChainTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RequestWorkflowService _workflow;

    private readonly AppUser _requester;
    private readonly Employee _employee;
    private readonly Reader _roomReader;      // M1000
    private readonly Reader _corridorA200;    // chodba A200 (vede k M1000)
    private readonly Reader _corridorA100;    // chodba A100 (nadřazená A200)
    private readonly Reader _noCorridorReader;

    public CorridorChainTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _workflow = new RequestWorkflowService(_db, new AuditService(_db));

        _requester = new AppUser { UserName = "zadatel" };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák" };
        _db.AddRange(_requester, _employee);

        var building = new Building { Name = "MOC" };
        var sectionG = new BuildingSection { Building = building, Name = "G" };
        var floorP = new Floor { Building = building, Section = sectionG, Name = "P" };
        var a100 = new Corridor { Floor = floorP, Name = "A100" };
        var a200 = new Corridor { Floor = floorP, Name = "A200", ParentCorridor = a100 };
        var m1000 = new Room { Floor = floorP, Corridor = a200, Name = "M1000" };
        _db.AddRange(building, sectionG, floorP, a100, a200, m1000);

        _roomReader = new Reader { Name = "M1000 dveře", IsActive = true, Room = m1000 };
        _corridorA200 = new Reader { Name = "A200 vstup", IsActive = true, Corridor = a200 };
        _corridorA100 = new Reader { Name = "A100 vstup", IsActive = true, Corridor = a100 };
        _noCorridorReader = new Reader { Name = "Samostatná", IsActive = true };
        _db.AddRange(_roomReader, _corridorA200, _corridorA100, _noCorridorReader);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RoomReader_AddsWholeCorridorChain()
    {
        var expanded = await _workflow.ExpandWithDependenciesAsync([_roomReader.Id]);
        Assert.Equal(new[] { _roomReader.Id, _corridorA200.Id, _corridorA100.Id }.ToHashSet(), expanded);
    }

    [Fact]
    public async Task CorridorReader_AddsParentCorridorsOnly()
    {
        // Čtečka na A200 → přidá se A100 (rodič), ne čtečka místnosti.
        var expanded = await _workflow.ExpandWithDependenciesAsync([_corridorA200.Id]);
        Assert.Equal(new[] { _corridorA200.Id, _corridorA100.Id }.ToHashSet(), expanded);
    }

    [Fact]
    public async Task ReaderWithoutCorridor_ExpandsToItself()
    {
        var expanded = await _workflow.ExpandWithDependenciesAsync([_noCorridorReader.Id]);
        Assert.Equal(new[] { _noCorridorReader.Id }.ToHashSet(), expanded);
    }

    [Fact]
    public async Task Request_ForRoomReader_ContainsCorridorItemsAsAutoAdded()
    {
        var request = await _workflow.CreateRequestAsync(
            _requester.Id, _employee.Id, [_roomReader.Id], null, requesterCanActForOthers: true);

        Assert.Equal(3, request.Items.Count);
        Assert.False(request.Items.Single(i => i.ReaderId == _roomReader.Id).AutoAdded);
        Assert.True(request.Items.Single(i => i.ReaderId == _corridorA200.Id).AutoAdded);
        Assert.True(request.Items.Single(i => i.ReaderId == _corridorA100.Id).AutoAdded);
    }

    [Fact]
    public async Task CorridorChain_CombinesWithManualDependencies()
    {
        // Ruční závislost: čtečka místnosti vyžaduje i samostatnou čtečku (např. vjezd).
        _db.ReaderDependencies.Add(new ReaderDependency
        {
            ReaderId = _roomReader.Id, RequiresReaderId = _noCorridorReader.Id,
        });
        await _db.SaveChangesAsync();

        var expanded = await _workflow.ExpandWithDependenciesAsync([_roomReader.Id]);
        Assert.Equal(4, expanded.Count); // místnost + A200 + A100 + ruční závislost
    }

    [Fact]
    public async Task CorridorCycle_DoesNotLoopForever()
    {
        // Uměle vytvořený cyklus v datech nesmí expanzi zacyklit.
        var a100 = await _db.Corridors.SingleAsync(c => c.Name == "A100");
        var a200 = await _db.Corridors.SingleAsync(c => c.Name == "A200");
        a100.ParentCorridorId = a200.Id;
        await _db.SaveChangesAsync();

        var expanded = await _workflow.ExpandWithDependenciesAsync([_roomReader.Id]);
        Assert.Equal(3, expanded.Count);
    }
}
