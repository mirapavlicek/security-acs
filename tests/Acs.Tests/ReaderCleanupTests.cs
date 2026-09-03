using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Acs.Tests;

/// <summary>
/// Mazání čteček z číselníku. Podstatné je, co se NEsmaže: aktivní čtečky
/// a cokoli, na čem visí žádost, skupina nebo závislost — jinak by se ztratila
/// dohledatelnost přístupů.
/// </summary>
public sealed class ReaderCleanupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly ReaderCleanupService _cleanup;

    public ReaderCleanupTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _cleanup = new ReaderCleanupService(_db, new AuditService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Reader AddReader(string name, bool active)
    {
        var reader = new Reader { Name = name, IsActive = active, Source = RecordSource.Manual };
        _db.Readers.Add(reader);
        _db.SaveChanges();
        return reader;
    }

    [Fact]
    public async Task Smaze_neaktivni_ctecku_bez_vazeb()
    {
        var stale = AddReader("ACS.24 — 23-02307 — odhad", active: false);

        var result = await _cleanup.DeleteAsync([stale.Id], "spravce");

        Assert.Equal(1, result.Deleted);
        Assert.False(await _db.Readers.AnyAsync(r => r.Id == stale.Id));
        var log = await _db.AuditLogs.SingleAsync(a => a.Action == "readers-deleted");
        Assert.Equal("spravce", log.UserName);
    }

    [Fact]
    public async Task Aktivni_ctecku_nesmaze_a_ohlasi_to()
    {
        var active = AddReader("362001 — 23-02301", active: true);

        var result = await _cleanup.DeleteAsync([active.Id], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.SkippedActive);
        Assert.True(await _db.Readers.AnyAsync(r => r.Id == active.Id));
        Assert.Contains("mažou se jen neaktivní", result.ToString());
    }

    [Fact]
    public async Task Ctecku_se_zadosti_nesmaze()
    {
        var referenced = AddReader("ACS.24 — 23-02307 — odhad", active: false);
        var employee = new Employee { FirstName = "Miroslav", LastName = "Pavlíček" };
        var user = new AppUser { UserName = "zadatel" };
        _db.Employees.Add(employee);
        _db.Users.Add(user);
        _db.SaveChanges();
        var request = new AccessRequest
        {
            RequesterUserId = user.Id, TargetEmployeeId = employee.Id, Kind = RequestKind.Grant,
            Justification = "test", CreatedAt = DateTime.UtcNow,
        };
        _db.AccessRequests.Add(request);
        _db.SaveChanges();
        _db.AccessRequestItems.Add(new AccessRequestItem { RequestId = request.Id, ReaderId = referenced.Id, Status = RequestStatus.Revoked });
        _db.SaveChanges();

        var result = await _cleanup.DeleteAsync([referenced.Id], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.SkippedReferenced);
        Assert.Contains(referenced.Name, result.Referenced);
        Assert.True(await _db.Readers.AnyAsync(r => r.Id == referenced.Id));
    }

    [Fact]
    public async Task Ctecku_ve_skupine_nesmaze()
    {
        var member = AddReader("ACS.24 — 23-02307 — odhad", active: false);
        var group = new ReaderGroup { Name = "Sklady" };
        _db.ReaderGroups.Add(group);
        _db.SaveChanges();
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = group.Id, ReaderId = member.Id });
        _db.SaveChanges();

        var result = await _cleanup.DeleteAsync([member.Id], "test");

        Assert.Equal(1, result.SkippedReferenced);
        Assert.True(await _db.Readers.AnyAsync(r => r.Id == member.Id));
    }

    [Fact]
    public async Task Ctecku_v_zavislosti_nesmaze_na_zadne_strane()
    {
        var requiring = AddReader("A", active: false);
        var required = AddReader("B", active: false);
        _db.ReaderDependencies.Add(new ReaderDependency { ReaderId = requiring.Id, RequiresReaderId = required.Id });
        _db.SaveChanges();

        var result = await _cleanup.DeleteAsync([requiring.Id, required.Id], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, result.SkippedReferenced);
    }

    [Fact]
    public async Task Smichany_vyber_smaze_jen_to_co_smi()
    {
        var stale = AddReader("odhad 1", active: false);
        var stale2 = AddReader("odhad 2", active: false);
        var active = AddReader("362001", active: true);
        var group = new ReaderGroup { Name = "Sklady" };
        _db.ReaderGroups.Add(group);
        _db.SaveChanges();
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = group.Id, ReaderId = stale2.Id });
        _db.SaveChanges();

        var result = await _cleanup.DeleteAsync([stale.Id, stale2.Id, active.Id], "test");

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.SkippedActive);
        Assert.Equal(1, result.SkippedReferenced);
        Assert.Equal(2, await _db.Readers.CountAsync());
    }

    [Fact]
    public async Task Prazdny_vyber_nic_nedela_a_nezapisuje_audit()
    {
        var result = await _cleanup.DeleteAsync([], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, await _db.AuditLogs.CountAsync());
    }
}
