using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Parkovací povolení: žádost, vazba na SPZ/funkci, řetěz matic druh → areály, schválení.</summary>
public sealed class ParkingWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RequestWorkflowService _workflow;

    private readonly AppUser _requester;
    private readonly AppUser _admin;
    private readonly AppUser _typeApprover;   // matice druhu
    private readonly AppUser _homolkaApprover; // matice areálu Homolka
    private readonly Employee _employee;
    private readonly Site _motol;   // bez matice
    private readonly Site _homolka; // s maticí
    private readonly ParkingPermitType _employeeType; // na SPZ, max 2, matice druhu
    private readonly ParkingPermitType _managementType; // na funkci, bez matice, všechny areály

    public ParkingWorkflowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _workflow = new RequestWorkflowService(_db, new AuditService(_db));

        _requester = new AppUser { UserName = "zadatel" };
        _admin = new AppUser { UserName = "admin", Roles = AppRole.Admin };
        _typeApprover = new AppUser { UserName = "schvalovatel-druhu" };
        _homolkaApprover = new AppUser { UserName = "schvalovatel-homolka" };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák", Department = "Chirurgie" };
        _db.AddRange(_requester, _admin, _typeApprover, _homolkaApprover, _employee);

        var typeMatrix = new ApprovalMatrix { Name = "Parkování zaměstnanci", Levels = [new ApprovalLevel { Order = 1 }] };
        var homolkaMatrix = new ApprovalMatrix { Name = "Areál Homolka", Levels = [new ApprovalLevel { Order = 1 }] };
        _db.AddRange(typeMatrix, homolkaMatrix);
        _db.SaveChanges();
        _db.Approvers.Add(new Approver { LevelId = typeMatrix.Levels[0].Id, UserId = _typeApprover.Id });
        _db.Approvers.Add(new Approver { LevelId = homolkaMatrix.Levels[0].Id, UserId = _homolkaApprover.Id });

        _motol = new Site { Name = "Motol", Code = "MOT", SortOrder = 1 };
        _homolka = new Site { Name = "Homolka", Code = "HOM", SortOrder = 2, ApprovalMatrix = homolkaMatrix };
        _employeeType = new ParkingPermitType
        {
            Name = "Zaměstnanec", Binding = PermitBinding.LicensePlate, MaxPlates = 2,
            ApprovalMatrix = typeMatrix, DefaultValidityMonths = 12,
        };
        _managementType = new ParkingPermitType
        {
            Name = "Vedení nemocnice", Binding = PermitBinding.Function, AllSitesByDefault = true,
        };
        _db.AddRange(_motol, _homolka, _employeeType, _managementType);
        _db.SaveChanges();

        _requester.EmployeeId = _employee.Id;
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ParkingRequestInput Input(int typeId, bool allSites = false, int[]? siteIds = null,
        string[]? plates = null, string? function = null, DateTime? validTo = null)
        => new(typeId, allSites, siteIds, plates, function, null, validTo, "test");

    private AccessRequestItem Item(AccessRequest request)
        => _db.AccessRequestItems.Include(i => i.Stages).Include(i => i.ParkingPermit!).ThenInclude(p => p.Plates)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Sites)
            .First(i => i.RequestId == request.Id);

    [Fact]
    public async Task Request_ForSiteWithMatrix_BuildsChainTypeThenSite()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_motol.Id, _homolka.Id], plates: ["1ab 2345"]));

        var item = Item(request);
        Assert.True(item.IsParking);
        Assert.Equal(RequestStatus.Pending, item.Status);
        Assert.Equal(2, item.Stages.Count);
        Assert.Equal(_employeeType.ApprovalMatrixId, item.Stages[0].MatrixId);
        Assert.Equal(_homolka.ApprovalMatrixId, item.Stages[1].MatrixId);
        Assert.Equal(_employeeType.ApprovalMatrixId, item.MatrixId);
        Assert.Equal(["1AB2345"], item.ParkingPermit!.Plates.Select(p => p.Value).ToArray());
        Assert.Equal(2, item.ParkingPermit.Sites.Count);
        Assert.NotNull(item.ParkingPermit.ValidTo); // 12 měsíců z druhu
        Assert.Equal(item.ParkingPermit.ValidTo, request.ValidUntil);
    }

    [Fact]
    public async Task Request_ForSiteWithoutMatrix_HasOnlyTypeStage()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["2CD3456"]));

        var item = Item(request);
        Assert.Single(item.Stages);
        Assert.Equal(_employeeType.ApprovalMatrixId, item.MatrixId);
    }

    [Fact]
    public async Task AllSites_IncludesEveryActiveSiteMatrix_AndStoresNoExplicitSites()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, allSites: true, plates: ["3EF4567"]));

        var item = Item(request);
        Assert.True(item.ParkingPermit!.AllSites);
        Assert.Empty(item.ParkingPermit.Sites);
        Assert.Equal(2, item.Stages.Count); // druh + Homolka (Motol matici nemá)
    }

    [Fact]
    public async Task FunctionBinding_WithoutMatrix_IsPendingForAdminOnly()
    {
        // Motol nemá matici a druh „Vedení nemocnice“ také ne → řetěz je prázdný.
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_managementType.Id, siteIds: [_motol.Id], function: "Náměstek"));

        var item = Item(request);
        Assert.Null(item.MatrixId);
        Assert.Empty(item.Stages);
        Assert.Empty(item.ParkingPermit!.Plates);
        Assert.Equal("Náměstek", item.ParkingPermit.FunctionTitle);

        Assert.Empty(await _workflow.GetPendingForApproverAsync(_typeApprover.Id));
        Assert.Single(await _workflow.GetPendingForApproverAsync(_admin.Id, isAdmin: true));

        await _workflow.DecideAsync(item.Id, _admin.Id, true, null, isAdmin: true);
        Assert.Equal(RequestStatus.Approved, (await _db.AccessRequestItems.FindAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task FunctionBinding_AllSites_StillPassesSiteMatrix()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_managementType.Id, allSites: true, function: "Náměstek"));

        var item = Item(request);
        Assert.Single(item.Stages);
        Assert.Equal(_homolka.ApprovalMatrixId, item.MatrixId);
    }

    [Fact]
    public async Task FunctionBinding_RequiresFunctionTitle()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id, Input(_managementType.Id, allSites: true)));
        Assert.Contains("funkce", ex.Message);
    }

    [Fact]
    public async Task PlateBinding_RequiresPlate_AndRespectsMaxPlates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id, Input(_employeeType.Id, siteIds: [_motol.Id])));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
                Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1A1", "2B2", "3C3"])));
        Assert.Contains("nejvýše 2", ex.Message);
    }

    [Fact]
    public async Task Plates_AreNormalized_AndDeduplicated()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1ab-2345", "1AB 2345"]));

        Assert.Equal(["1AB2345"], Item(request).ParkingPermit!.Plates.Select(p => p.Value).ToArray());
    }

    [Fact]
    public async Task InvalidPlate_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
                Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1AB/2345"])));
    }

    [Fact]
    public async Task MustSelectSites_OrAllSites()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
                Input(_employeeType.Id, plates: ["1AB2345"])));
    }

    [Fact]
    public async Task DuplicateRequest_ForSameType_IsRefused()
    {
        await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1AB2345"]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
                Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["9ZZ9999"])));
        Assert.Contains("běží nebo platí", ex.Message);
    }

    [Fact]
    public async Task RegularUser_CannotRequestForOthers()
    {
        var other = new Employee { FirstName = "Eva", LastName = "Malá" };
        _db.Employees.Add(other);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, other.Id,
                Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1AB2345"])));
    }

    [Fact]
    public async Task Approval_WalksTypeMatrixThenSiteMatrix_ThenApproved()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_homolka.Id], plates: ["1AB2345"]));
        var item = Item(request);

        // Fáze 1: schvalovatel druhu; schvalovatel areálu ještě nic nevidí.
        Assert.Single(await _workflow.GetPendingForApproverAsync(_typeApprover.Id));
        Assert.Empty(await _workflow.GetPendingForApproverAsync(_homolkaApprover.Id));

        await _workflow.DecideAsync(item.Id, _typeApprover.Id, true, null);
        var afterFirst = await _db.AccessRequestItems.AsNoTracking().FirstAsync(i => i.Id == item.Id);
        Assert.Equal(RequestStatus.Pending, afterFirst.Status);
        Assert.Equal(2, afterFirst.CurrentStageOrder);
        Assert.Equal(_homolka.ApprovalMatrixId, afterFirst.MatrixId);

        // Fáze 2: areál.
        Assert.Single(await _workflow.GetPendingForApproverAsync(_homolkaApprover.Id));
        await _workflow.DecideAsync(item.Id, _homolkaApprover.Id, true, "ok");

        var final = await _db.AccessRequestItems.AsNoTracking().FirstAsync(i => i.Id == item.Id);
        Assert.Equal(RequestStatus.Approved, final.Status);
        Assert.NotNull(final.DecidedAt);
    }

    [Fact]
    public async Task Rejection_InSiteStage_StopsItem()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_homolka.Id], plates: ["1AB2345"]));
        var item = Item(request);

        await _workflow.DecideAsync(item.Id, _typeApprover.Id, true, null);
        await _workflow.DecideAsync(item.Id, _homolkaApprover.Id, false, "není místo");

        Assert.Equal(RequestStatus.Rejected, (await _db.AccessRequestItems.FindAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task InactiveType_IsRefused()
    {
        _employeeType.IsActive = false;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
                Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1AB2345"])));
    }

    [Fact]
    public async Task ValidTo_BeforeValidFrom_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
                Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1AB2345"], validTo: DateTime.UtcNow.AddDays(-1))));
    }

    [Fact]
    public async Task RevokeRequest_RequiresIssuedPermit()
    {
        var request = await _workflow.CreateParkingRequestAsync(_requester.Id, _employee.Id,
            Input(_employeeType.Id, siteIds: [_motol.Id], plates: ["1AB2345"]));
        var permitId = Item(request).ParkingPermitId!.Value;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.CreateParkingRevokeRequestAsync(_requester.Id, permitId, null));
        Assert.Contains("není vydané", ex.Message);
    }
}
