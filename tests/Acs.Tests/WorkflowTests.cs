using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RequestWorkflowService _workflow;

    private readonly AppUser _requester;
    private readonly AppUser _boss;
    private readonly AppUser _security;
    private readonly AppUser _deputy;
    private readonly Employee _employee;
    private readonly Reader _room;
    private readonly Reader _corridor;
    private readonly Reader _floor;

    public WorkflowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _workflow = new RequestWorkflowService(_db, new AuditService(_db));

        _requester = new AppUser { UserName = "zadatel" };
        _boss = new AppUser { UserName = "vedouci" };
        _security = new AppUser { UserName = "bezpecnost" };
        _deputy = new AppUser { UserName = "zastupce" };
        _db.Users.AddRange(_requester, _boss, _security, _deputy);

        _employee = new Employee { FirstName = "Jan", LastName = "Novák" };
        _db.Employees.Add(_employee);

        // Dvouúrovňová matice: 1) vedoucí (Any), 2) bezpečnost (All).
        var matrix = new ApprovalMatrix
        {
            Name = "Serverovna",
            Levels =
            [
                new ApprovalLevel { Order = 1, Mode = ApprovalMode.Any },
                new ApprovalLevel { Order = 2, Mode = ApprovalMode.All },
            ],
        };
        _db.ApprovalMatrices.Add(matrix);
        _db.SaveChanges();
        _db.Approvers.Add(new Approver { LevelId = matrix.Levels[0].Id, UserId = _boss.Id });
        _db.Approvers.Add(new Approver { LevelId = matrix.Levels[1].Id, UserId = _security.Id });

        // Řetězec: místnost vyžaduje chodbu, chodba vyžaduje patro. Patro bez matice.
        _floor = new Reader { Name = "Patro", IsActive = true };
        _corridor = new Reader { Name = "Chodba", IsActive = true, ApprovalMatrix = matrix };
        _room = new Reader { Name = "Serverovna", IsActive = true, ApprovalMatrix = matrix };
        _db.Readers.AddRange(_floor, _corridor, _room);
        _db.SaveChanges();
        _db.ReaderDependencies.Add(new ReaderDependency { ReaderId = _room.Id, RequiresReaderId = _corridor.Id });
        _db.ReaderDependencies.Add(new ReaderDependency { ReaderId = _corridor.Id, RequiresReaderId = _floor.Id });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateRequest_ExpandsDependencyChain_Transitively()
    {
        var request = await _workflow.CreateRequestAsync(
            _requester.Id, _employee.Id, [_room.Id], "potřebuji do serverovny",
            requesterCanActForOthers: true);

        Assert.Equal(3, request.Items.Count);

        var roomItem = request.Items.Single(i => i.ReaderId == _room.Id);
        Assert.False(roomItem.AutoAdded);
        Assert.Equal(RequestStatus.Pending, roomItem.Status);

        var corridorItem = request.Items.Single(i => i.ReaderId == _corridor.Id);
        Assert.True(corridorItem.AutoAdded);

        // Patro nemá matici → NESCHVALUJE se automaticky, čeká na rozhodnutí admina.
        var floorItem = request.Items.Single(i => i.ReaderId == _floor.Id);
        Assert.True(floorItem.AutoAdded);
        Assert.Equal(RequestStatus.Pending, floorItem.Status);
        Assert.Null(floorItem.MatrixId);
    }

    [Fact]
    public async Task NonAdmin_CannotRequestForOtherEmployee()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_corridor.Id], null));
    }

    [Fact]
    public async Task ReaderWithoutMatrix_RequiresAdminApproval()
    {
        var request = await _workflow.CreateRequestAsync(
            _requester.Id, _employee.Id, [_floor.Id], null, requesterCanActForOthers: true);
        var item = request.Items.Single(i => i.ReaderId == _floor.Id);

        // Běžný schvalovatel položku bez matice nevidí.
        Assert.Empty(await _workflow.GetPendingForApproverAsync(_boss.Id, isAdmin: false));
        // Admin ano a smí ji schválit.
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_boss.Id, isAdmin: true),
            i => i.Id == item.Id);

        await _workflow.DecideAsync(item.Id, _boss.Id, approve: true, null, isAdmin: true);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Approved, item.Status);
    }

    [Fact]
    public async Task ReaderWithoutMatrix_NonAdminCannotDecide()
    {
        var request = await _workflow.CreateRequestAsync(
            _requester.Id, _employee.Id, [_floor.Id], null, requesterCanActForOthers: true);
        var item = request.Items.Single(i => i.ReaderId == _floor.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _workflow.DecideAsync(item.Id, _boss.Id, approve: true, null, isAdmin: false));
    }

    [Fact]
    public async Task Approval_WalksThroughLevels_ThenApproves()
    {
        var request = await _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_room.Id], null,
            requesterCanActForOthers: true);
        var item = request.Items.Single(i => i.ReaderId == _room.Id);

        // Úroveň 1: vedoucí je schvalovatel, žadatel ne.
        var pendingForBoss = await _workflow.GetPendingForApproverAsync(_boss.Id);
        Assert.Contains(pendingForBoss, i => i.Id == item.Id);
        Assert.Empty(await _workflow.GetPendingForApproverAsync(_requester.Id));

        await _workflow.DecideAsync(item.Id, _boss.Id, approve: true, "ok");
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Pending, item.Status);
        Assert.Equal(2, item.CurrentLevelOrder);

        // Úroveň 2: bezpečnost.
        await _workflow.DecideAsync(item.Id, _security.Id, approve: true, null);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Approved, item.Status);
    }

    [Fact]
    public async Task Rejection_StopsItem()
    {
        var request = await _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_corridor.Id], null,
            requesterCanActForOthers: true);
        var item = request.Items.Single(i => i.ReaderId == _corridor.Id);

        await _workflow.DecideAsync(item.Id, _boss.Id, approve: false, "neopodstatněné");
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Rejected, item.Status);
    }

    [Fact]
    public async Task Deputy_CanDecide_OnBehalfOfPrincipal()
    {
        _db.Deputies.Add(new Deputy
        {
            PrincipalUserId = _boss.Id,
            DeputyUserId = _deputy.Id,
            ValidFrom = DateTime.UtcNow.AddDays(-1),
            ValidTo = DateTime.UtcNow.AddDays(1),
        });
        await _db.SaveChangesAsync();

        var request = await _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_corridor.Id], null,
            requesterCanActForOthers: true);
        var item = request.Items.Single(i => i.ReaderId == _corridor.Id);

        var pendingForDeputy = await _workflow.GetPendingForApproverAsync(_deputy.Id);
        Assert.Contains(pendingForDeputy, i => i.Id == item.Id);

        await _workflow.DecideAsync(item.Id, _deputy.Id, approve: true, "za vedoucího");
        var decision = await _db.ApprovalDecisions.SingleAsync(d => d.ItemId == item.Id);
        Assert.Equal(_deputy.Id, decision.ApproverUserId);
        Assert.Equal(_boss.Id, decision.OnBehalfOfUserId);
    }

    [Fact]
    public async Task ExpiredDeputy_CannotDecide()
    {
        _db.Deputies.Add(new Deputy
        {
            PrincipalUserId = _boss.Id,
            DeputyUserId = _deputy.Id,
            ValidFrom = DateTime.UtcNow.AddDays(-10),
            ValidTo = DateTime.UtcNow.AddDays(-5),
        });
        await _db.SaveChangesAsync();

        var request = await _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_corridor.Id], null,
            requesterCanActForOthers: true);
        var item = request.Items.Single(i => i.ReaderId == _corridor.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _workflow.DecideAsync(item.Id, _deputy.Id, approve: true, null));
    }

    [Fact]
    public async Task QuorumMode_RequiresNApprovals()
    {
        var approver3 = new AppUser { UserName = "treti" };
        _db.Users.Add(approver3);
        var matrix = new ApprovalMatrix
        {
            Name = "Kvorum 2 ze 3",
            Levels = [new ApprovalLevel { Order = 1, Mode = ApprovalMode.Quorum, RequiredCount = 2 }],
        };
        _db.ApprovalMatrices.Add(matrix);
        await _db.SaveChangesAsync();
        _db.Approvers.AddRange(
            new Approver { LevelId = matrix.Levels[0].Id, UserId = _boss.Id },
            new Approver { LevelId = matrix.Levels[0].Id, UserId = _security.Id },
            new Approver { LevelId = matrix.Levels[0].Id, UserId = approver3.Id });
        var reader = new Reader { Name = "Lékárna", IsActive = true, ApprovalMatrix = matrix };
        _db.Readers.Add(reader);
        await _db.SaveChangesAsync();

        var request = await _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [reader.Id], null,
            requesterCanActForOthers: true);
        var item = request.Items.Single();

        await _workflow.DecideAsync(item.Id, _boss.Id, approve: true, null);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Pending, item.Status); // 1 ze 2 nutných

        await _workflow.DecideAsync(item.Id, _security.Id, approve: true, null);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Approved, item.Status); // kvorum splněno
    }

    [Fact]
    public async Task DuplicatePendingRequest_IsSkipped()
    {
        await _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_floor.Id], null,
            requesterCanActForOthers: true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _workflow.CreateRequestAsync(_requester.Id, _employee.Id, [_floor.Id], null,
                requesterCanActForOthers: true));
    }
}
