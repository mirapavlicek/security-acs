using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Schvalování „nadřízeným zaměstnance“ (z AD atributu manager): nadřízený se
/// vyhodnocuje u každé žádosti podle cílového zaměstnance, funguje i bez účtu v ACS
/// (přes EmployeeId / AD účet), respektuje zástupy, přeskakuje samoschválení a bez
/// nadřízeného padá na administrátora s upozorněním.
/// </summary>
public sealed class ManagerApprovalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RecordingNotifier _notifier = new();
    private readonly RequestWorkflowService _workflow;

    private readonly AppUser _admin;
    private readonly AppUser _requester;
    private readonly AppUser _managerUser;
    private readonly AppUser _directorUser;
    private readonly AppUser _security;
    private readonly Employee _employee;
    private readonly Employee _manager;
    private readonly Employee _director;
    private readonly ApprovalMatrix _matrix;
    private readonly Reader _door;

    public ManagerApprovalTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _workflow = new RequestWorkflowService(_db, new AuditService(_db), _notifier);

        // Hierarchie: zaměstnanec → vedoucí → ředitel (ředitel bez nadřízeného).
        _director = new Employee { FirstName = "Dana", LastName = "Ředitelová", AdAccount = "dreditelova", Email = "dana@fnmh.cz" };
        _manager = new Employee { FirstName = "Věra", LastName = "Vedoucí", AdAccount = "vvedouci", Email = "vera@fnmh.cz", Manager = _director };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák", AdAccount = "jnovak", Manager = _manager };
        _db.Employees.AddRange(_director, _manager, _employee);

        _admin = new AppUser { UserName = "admin", Roles = AppRole.Admin, Email = "admin@fnmh.cz" };
        _requester = new AppUser { UserName = "zadatel", Employee = _employee };
        _managerUser = new AppUser { UserName = "vvedouci", Employee = _manager, Email = "vera.user@fnmh.cz" };
        _directorUser = new AppUser { UserName = "dreditelova", Employee = _director };
        _security = new AppUser { UserName = "bezpecnost" };
        _db.Users.AddRange(_admin, _requester, _managerUser, _directorUser, _security);

        // Matice: 1) nadřízený zaměstnance (Any), 2) bezpečnost (All).
        _matrix = new ApprovalMatrix
        {
            Name = "Nadřízený → bezpečnost",
            Levels =
            [
                new ApprovalLevel { Order = 1, Mode = ApprovalMode.Any },
                new ApprovalLevel { Order = 2, Mode = ApprovalMode.All },
            ],
        };
        _db.ApprovalMatrices.Add(_matrix);
        _db.SaveChanges();
        _db.Approvers.Add(new Approver { LevelId = _matrix.Levels[0].Id, Kind = ApproverKind.LineManager, ManagerDepth = 1 });
        _db.Approvers.Add(new Approver { LevelId = _matrix.Levels[1].Id, Kind = ApproverKind.User, UserId = _security.Id });

        _door = new Reader { Name = "Sklad", IsActive = true, ApprovalMatrix = _matrix };
        _db.Readers.Add(_door);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<AccessRequestItem> CreateItemAsync(int? requesterId = null, int? employeeId = null)
    {
        var request = await _workflow.CreateRequestAsync(
            requesterId ?? _requester.Id, employeeId ?? _employee.Id, [_door.Id], "sklad",
            requesterCanActForOthers: true);
        return request.Items.Single();
    }

    [Fact]
    public async Task Nadrizeny_vidi_a_rozhoduje_polozku_na_urovni_nadrizeneho()
    {
        var item = await CreateItemAsync();

        var pendingForManager = await _workflow.GetPendingForApproverAsync(_managerUser.Id);
        Assert.Contains(pendingForManager, i => i.Id == item.Id);

        // Ředitel ani bezpečnost na 1. úrovni nerozhodují.
        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_directorUser.Id), i => i.Id == item.Id);
        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_security.Id), i => i.Id == item.Id);
        // A ani admin — úroveň má schvalovatele, není co suplovat.
        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_admin.Id, isAdmin: true), i => i.Id == item.Id);

        await _workflow.DecideAsync(item.Id, _managerUser.Id, approve: true, "ok");

        var reloaded = await _db.AccessRequestItems.Include(i => i.Decisions).SingleAsync(i => i.Id == item.Id);
        Assert.Equal(RequestStatus.Pending, reloaded.Status);
        Assert.Equal(2, reloaded.CurrentLevelOrder);
        Assert.Equal(_managerUser.Id, reloaded.Decisions.Single().ApproverUserId);
        // Postup na 2. úroveň upozorní bezpečnost.
        Assert.Contains(item.Id, _notifier.PendingNotified);
    }

    [Fact]
    public async Task Cizi_uzivatel_nesmi_rozhodovat_za_nadrizeneho()
    {
        var item = await CreateItemAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _workflow.DecideAsync(item.Id, _directorUser.Id, true, null));
    }

    [Fact]
    public async Task Nadrizeny_bez_uctu_v_ACS_rozhodne_po_prvnim_prihlaseni_pres_AD_ucet()
    {
        // Vedoucí ještě nemá AppUser: účet vznikne při prvním přihlášení bez spárovaného EmployeeId.
        _db.Users.Remove(_managerUser);
        await _db.SaveChangesAsync();

        var item = await CreateItemAsync();

        // Vyhodnocení: nadřízený je znám (přes zaměstnance), jen bez uživatele.
        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        Assert.NotNull(resolution);
        var approver = Assert.Single(resolution!.Approvers);
        Assert.Null(approver.UserId);
        Assert.Equal(_manager.Id, approver.EmployeeId);
        Assert.Equal("vera@fnmh.cz", approver.Email);
        Assert.False(resolution.RequiresAdminFallback);

        // Přihlásí se (SSO) — účet se stejným sAMAccountName, EmployeeId zatím nespárováno.
        var fresh = new AppUser { UserName = "vvedouci" };
        _db.Users.Add(fresh);
        await _db.SaveChangesAsync();

        Assert.Contains(await _workflow.GetPendingForApproverAsync(fresh.Id), i => i.Id == item.Id);
        await _workflow.DecideAsync(item.Id, fresh.Id, true, null);

        var reloaded = await _db.AccessRequestItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(2, reloaded.CurrentLevelOrder);
    }

    [Fact]
    public async Task Zastup_nadrizeneho_rozhoduje_v_zastupu()
    {
        var deputy = new AppUser { UserName = "zastupce" };
        _db.Users.Add(deputy);
        await _db.SaveChangesAsync();
        _db.Deputies.Add(new Deputy
        {
            PrincipalUserId = _managerUser.Id, DeputyUserId = deputy.Id,
            ValidFrom = DateTime.UtcNow.AddDays(-1), ValidTo = DateTime.UtcNow.AddDays(1),
        });
        await _db.SaveChangesAsync();

        var item = await CreateItemAsync();
        Assert.Contains(await _workflow.GetPendingForApproverAsync(deputy.Id), i => i.Id == item.Id);

        await _workflow.DecideAsync(item.Id, deputy.Id, true, null);

        var decision = await _db.ApprovalDecisions.SingleAsync(d => d.ItemId == item.Id);
        Assert.Equal(deputy.Id, decision.ApproverUserId);
        Assert.Equal(_managerUser.Id, decision.OnBehalfOfUserId);
    }

    [Fact]
    public async Task Samoschvaleni_zadatel_je_nadrizeny_rozhoduje_nadrizeny_nadrizeneho()
    {
        // Vedoucí podává žádost za svého podřízeného → nemůže si ji sama schválit; rozhoduje ředitelka.
        var item = await CreateItemAsync(requesterId: _managerUser.Id);

        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        var approver = Assert.Single(resolution!.Approvers);
        Assert.Equal(_director.Id, approver.EmployeeId);

        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_managerUser.Id), i => i.Id == item.Id);
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_directorUser.Id), i => i.Id == item.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _workflow.DecideAsync(item.Id, _managerUser.Id, true, null));
    }

    [Fact]
    public async Task Bez_nadrizeneho_rozhodne_admin_a_admini_dostanou_upozorneni()
    {
        // Žádost pro ředitelku — nemá nadřízeného.
        var item = await CreateItemAsync(requesterId: _admin.Id, employeeId: _director.Id);

        Assert.Contains(item.Id, _notifier.NoApprover.Keys);
        Assert.Contains("nemá v ACS nadřízeného", _notifier.NoApprover[item.Id]);
        Assert.DoesNotContain(item.Id, _notifier.PendingNotified);

        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        Assert.True(resolution!.RequiresAdminFallback);

        // Běžný uživatel ne, admin ano.
        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_managerUser.Id), i => i.Id == item.Id);
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_admin.Id, isAdmin: true), i => i.Id == item.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _workflow.DecideAsync(item.Id, _managerUser.Id, true, null));

        await _workflow.DecideAsync(item.Id, _admin.Id, true, "schvaluji místo nadřízeného", isAdmin: true);

        var reloaded = await _db.AccessRequestItems.Include(i => i.Decisions).SingleAsync(i => i.Id == item.Id);
        // Rozhodnutí admina nahradí chybějícího nadřízeného; bezpečnost (2. úroveň) rozhoduje dál.
        Assert.Equal(RequestStatus.Pending, reloaded.Status);
        Assert.Equal(2, reloaded.CurrentLevelOrder);
        var decision = reloaded.Decisions.Single();
        Assert.Contains("rozhodl administrátor", decision.Comment);
        Assert.Contains("schvaluji místo nadřízeného", decision.Comment);
    }

    [Fact]
    public async Task Nahradni_konkretni_uzivatel_na_urovni_odstrani_fallback()
    {
        // Správce přidá na 1. úroveň vedle nadřízeného i konkrétního uživatele (režim Any).
        _db.Approvers.Add(new Approver { LevelId = _matrix.Levels[0].Id, Kind = ApproverKind.User, UserId = _security.Id });
        await _db.SaveChangesAsync();

        var item = await CreateItemAsync(requesterId: _admin.Id, employeeId: _director.Id);

        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        Assert.False(resolution!.RequiresAdminFallback);
        Assert.Single(resolution.Approvers);
        Assert.NotEmpty(resolution.Warnings);
        Assert.DoesNotContain(item.Id, _notifier.NoApprover.Keys);
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_security.Id), i => i.Id == item.Id);
    }

    [Fact]
    public async Task Rezim_vsichni_pocita_nadrizeneho_i_konkretniho_uzivatele()
    {
        var level = _matrix.Levels[0];
        level.Mode = ApprovalMode.All;
        _db.Approvers.Add(new Approver { LevelId = level.Id, Kind = ApproverKind.User, UserId = _security.Id });
        await _db.SaveChangesAsync();

        var item = await CreateItemAsync();

        await _workflow.DecideAsync(item.Id, _security.Id, true, null);
        var afterFirst = await _db.AccessRequestItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(1, afterFirst.CurrentLevelOrder); // nadřízený ještě nerozhodl

        await _workflow.DecideAsync(item.Id, _managerUser.Id, true, null);
        var afterSecond = await _db.AccessRequestItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(2, afterSecond.CurrentLevelOrder);
    }

    [Fact]
    public async Task Hloubka_2_je_nadrizeny_nadrizeneho()
    {
        var approver = await _db.Approvers.SingleAsync(a => a.Kind == ApproverKind.LineManager);
        approver.ManagerDepth = 2;
        await _db.SaveChangesAsync();

        var item = await CreateItemAsync();

        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        var resolved = Assert.Single(resolution!.Approvers);
        Assert.Equal(_director.Id, resolved.EmployeeId);
        Assert.Equal("nadřízený nadřízeného", resolved.OriginLabel);

        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_managerUser.Id), i => i.Id == item.Id);
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_directorUser.Id), i => i.Id == item.Id);
    }

    [Fact]
    public async Task Neaktivni_nadrizeny_znamena_bez_nadrizeneho()
    {
        _manager.IsActive = false;
        await _db.SaveChangesAsync();

        var item = await CreateItemAsync(requesterId: _admin.Id);
        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        Assert.True(resolution!.RequiresAdminFallback);
        Assert.Contains(resolution.Warnings, w => w.Contains("není aktivní"));
    }

    [Fact]
    public async Task Notifikace_nadrizenemu_jde_na_email_uzivatele_nebo_zamestnance()
    {
        var item = await CreateItemAsync();
        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        var approver = Assert.Single(resolution!.Approvers);
        // Přednost má e-mail účtu v ACS.
        Assert.Equal("vera.user@fnmh.cz", approver.Email);
        Assert.Equal(_managerUser.Id, approver.UserId);
    }

    private sealed class RecordingNotifier : INotificationService
    {
        public List<int> PendingNotified { get; } = [];
        public Dictionary<int, string> NoApprover { get; } = [];

        public Task NotifyPendingAsync(int itemId, CancellationToken ct = default)
        {
            PendingNotified.Add(itemId);
            return Task.CompletedTask;
        }

        public Task NotifyDecidedAsync(int itemId, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyEscalationAsync(int itemId, int waitingDays, CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyNoApproverAsync(int itemId, string reason, CancellationToken ct = default)
        {
            NoApprover[itemId] = reason;
            return Task.CompletedTask;
        }
    }
}
