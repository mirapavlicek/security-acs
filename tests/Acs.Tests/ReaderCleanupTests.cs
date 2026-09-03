using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Acs.Tests;

/// <summary>
/// Odstranění čteček z číselníku. Správce chce čtečku pryč a nemá řešit vazby:
/// bez vazeb se smaže, s vazbami (žádost, skupina, závislost) se deaktivuje
/// a skryje — záznam zůstává kvůli dohledatelnosti přístupů.
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

    private Reader AddReader(string name, bool active = true)
    {
        var reader = new Reader { Name = name, IsActive = active, Source = RecordSource.Manual };
        _db.Readers.Add(reader);
        _db.SaveChanges();
        return reader;
    }

    private void AddRequestItemFor(Reader reader)
    {
        var employee = new Employee { FirstName = "Miroslav", LastName = "Pavlíček" };
        var user = new AppUser { UserName = "zadatel" + reader.Id };
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
        _db.AccessRequestItems.Add(new AccessRequestItem
        {
            RequestId = request.Id, ReaderId = reader.Id, Status = RequestStatus.PushedToWinPak,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Ctecku_bez_vazeb_smaze_i_kdyz_je_aktivni()
    {
        var stale = AddReader("ACS.24 — 23-02307 — odhad", active: true);

        var result = await _cleanup.RemoveAsync([stale.Id], "spravce");

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Deactivated);
        Assert.False(await _db.Readers.AnyAsync(r => r.Id == stale.Id));
        var log = await _db.AuditLogs.SingleAsync(a => a.Action == "readers-removed");
        Assert.Equal("spravce", log.UserName);
    }

    [Fact]
    public async Task Ctecku_se_zadosti_nesmaze_ale_deaktivuje_a_ohlasi()
    {
        var referenced = AddReader("362001 — 23-02301", active: true);
        AddRequestItemFor(referenced);

        var result = await _cleanup.RemoveAsync([referenced.Id], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Deactivated);
        Assert.Contains(referenced.Name, result.DeactivatedNames);
        await _db.Entry(referenced).ReloadAsync();
        Assert.False(referenced.IsActive);
        // Žádost na ni dál ukazuje — historie přístupu zůstává dohledatelná.
        Assert.Equal(1, await _db.AccessRequestItems.CountAsync(i => i.ReaderId == referenced.Id));
        Assert.Contains("Deaktivováno a skryto: 1", result.ToString());
    }

    [Fact]
    public async Task Ctecku_ve_skupine_deaktivuje()
    {
        var member = AddReader("ACS.24 — 23-02307 — odhad");
        var group = new ReaderGroup { Name = "Sklady" };
        _db.ReaderGroups.Add(group);
        _db.SaveChanges();
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = group.Id, ReaderId = member.Id });
        _db.SaveChanges();

        var result = await _cleanup.RemoveAsync([member.Id], "test");

        Assert.Equal(1, result.Deactivated);
        await _db.Entry(member).ReloadAsync();
        Assert.False(member.IsActive);
    }

    [Fact]
    public async Task Ctecku_v_zavislosti_deaktivuje_na_obou_stranach()
    {
        var requiring = AddReader("A");
        var required = AddReader("B");
        _db.ReaderDependencies.Add(new ReaderDependency { ReaderId = requiring.Id, RequiresReaderId = required.Id });
        _db.SaveChanges();

        var result = await _cleanup.RemoveAsync([requiring.Id, required.Id], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, result.Deactivated);
        Assert.Equal(0, await _db.Readers.CountAsync(r => r.IsActive));
    }

    [Fact]
    public async Task Smichany_vyber_smaze_co_smi_a_zbytek_skryje()
    {
        var stale = AddReader("odhad 1");
        var withGroup = AddReader("odhad 2");
        var withRequest = AddReader("362001");
        var group = new ReaderGroup { Name = "Sklady" };
        _db.ReaderGroups.Add(group);
        _db.SaveChanges();
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = group.Id, ReaderId = withGroup.Id });
        _db.SaveChanges();
        AddRequestItemFor(withRequest);

        var result = await _cleanup.RemoveAsync([stale.Id, withGroup.Id, withRequest.Id], "test");

        Assert.Equal(1, result.Deleted);
        Assert.Equal(2, result.Deactivated);
        Assert.Equal(2, await _db.Readers.CountAsync());
        Assert.Equal(0, await _db.Readers.CountAsync(r => r.IsActive));
    }

    [Fact]
    public async Task Uz_neaktivni_ctecka_s_vazbou_se_znovu_neaudituje_jako_zmena()
    {
        var hidden = AddReader("skrytá", active: false);
        AddRequestItemFor(hidden);

        var result = await _cleanup.RemoveAsync([hidden.Id], "test");

        Assert.Equal(1, result.Deactivated);
        // Nic se nezměnilo (byla už neaktivní), takže žádný záznam v auditu.
        Assert.Equal(0, await _db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Prazdny_vyber_nic_nedela()
    {
        var result = await _cleanup.RemoveAsync([], "test");

        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, await _db.AuditLogs.CountAsync());
    }
}
