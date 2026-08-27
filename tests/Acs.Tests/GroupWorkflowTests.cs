using System.Net;
using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Skupiny čteček: vnořené schvalování, auto-zařazení, předání skupiny do WIN-PAK.</summary>
public sealed class GroupWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RequestWorkflowService _workflow;
    private readonly ReaderGroupService _groups;

    private readonly AppUser _requester;
    private readonly AppUser _headOfSurgery;
    private readonly AppUser _security;
    private readonly Employee _employee;
    private readonly Reader _or1;      // operační sál, AL-OR1
    private readonly Reader _corridor; // chodba chirurgie, AL-CHIR
    private readonly ReaderGroup _surgery;      // skupina Chirurgie (matice: vedoucí chirurgie)
    private readonly ReaderGroup _securityZone; // nadřazená skupina (matice: bezpečnost)

    public GroupWorkflowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _groups = new ReaderGroupService(_db);
        _workflow = new RequestWorkflowService(_db, new AuditService(_db), groups: _groups);

        _requester = new AppUser { UserName = "zadatel" };
        _headOfSurgery = new AppUser { UserName = "vedouci-chirurgie" };
        _security = new AppUser { UserName = "bezpecnost" };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák", Department = "Chirurgie", WinPakCardHolderId = "CH-1" };
        _db.AddRange(_requester, _headOfSurgery, _security, _employee);

        var surgeryMatrix = new ApprovalMatrix
        {
            Name = "Chirurgie",
            Levels = [new ApprovalLevel { Order = 1, Mode = ApprovalMode.Any }],
        };
        var securityMatrix = new ApprovalMatrix
        {
            Name = "Bezpečnost",
            Levels = [new ApprovalLevel { Order = 1, Mode = ApprovalMode.Any }],
        };
        _db.AddRange(surgeryMatrix, securityMatrix);
        _db.SaveChanges();
        _db.Approvers.Add(new Approver { LevelId = surgeryMatrix.Levels[0].Id, UserId = _headOfSurgery.Id });
        _db.Approvers.Add(new Approver { LevelId = securityMatrix.Levels[0].Id, UserId = _security.Id });

        _or1 = new Reader { Name = "Operační sál 1", IsActive = true, AccessLevelExternalId = "AL-OR1" };
        _corridor = new Reader { Name = "Chodba chirurgie", IsActive = true, AccessLevelExternalId = "AL-CHIR" };
        _db.AddRange(_or1, _corridor);

        _surgery = new ReaderGroup { Name = "Chirurgie", ApprovalMatrix = surgeryMatrix };
        _securityZone = new ReaderGroup { Name = "Bezpečnostní zóna", ApprovalMatrix = securityMatrix };
        _db.AddRange(_surgery, _securityZone);
        _db.SaveChanges();

        // Chirurgie obsahuje chodbu a vnořenou skupinu? Ne — Chirurgie obsahuje čtečky,
        // a je vnořená do Bezpečnostní zóny (rodič) → řetěz matic Chirurgie → Bezpečnost.
        _db.ReaderGroupMembers.AddRange(
            new ReaderGroupMember { GroupId = _surgery.Id, ReaderId = _or1.Id },
            new ReaderGroupMember { GroupId = _surgery.Id, ReaderId = _corridor.Id },
            new ReaderGroupMember { GroupId = _securityZone.Id, ChildGroupId = _surgery.Id });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GroupExpansion_ReturnsAllReaders_Recursively()
    {
        var fromParent = await _groups.ExpandReaderIdsAsync(_securityZone.Id);
        Assert.Equal(new[] { _corridor.Id, _or1.Id }.ToHashSet(), fromParent);
    }

    [Fact]
    public async Task MatrixChain_IsGroupThenAncestors()
    {
        var chain = await _groups.GetMatrixChainAsync(_surgery.Id);
        Assert.Equal(2, chain.Count);
        Assert.Equal(_surgery.ApprovalMatrixId, chain[0]);
        Assert.Equal(_securityZone.ApprovalMatrixId, chain[1]);
    }

    [Fact]
    public async Task NestedApproval_GroupRequest_PassesBothMatrices()
    {
        var request = await _workflow.CreateRequestAsync(
            _requester.Id, _employee.Id, [], null,
            requesterCanActForOthers: true, groupIds: [_surgery.Id]);
        var item = request.Items.Single();
        Assert.Equal(_surgery.Id, item.ReaderGroupId);
        Assert.Equal(2, item.Stages.Count);

        // Fáze 1: vedoucí chirurgie (bezpečnost ještě rozhodovat nesmí).
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_headOfSurgery.Id), i => i.Id == item.Id);
        Assert.Empty(await _workflow.GetPendingForApproverAsync(_security.Id));

        await _workflow.DecideAsync(item.Id, _headOfSurgery.Id, approve: true, "ok");
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Pending, item.Status);
        Assert.Equal(2, item.CurrentStageOrder); // spadlo na bezpečnost

        // Fáze 2: bezpečnost.
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_security.Id), i => i.Id == item.Id);
        await _workflow.DecideAsync(item.Id, _security.Id, approve: true, null);
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Approved, item.Status);
    }

    [Fact]
    public async Task NestedApproval_RejectionInSecondStage_Rejects()
    {
        var request = await _workflow.CreateRequestAsync(
            _requester.Id, _employee.Id, [], null,
            requesterCanActForOthers: true, groupIds: [_surgery.Id]);
        var item = request.Items.Single();

        await _workflow.DecideAsync(item.Id, _headOfSurgery.Id, approve: true, null);
        await _workflow.DecideAsync(item.Id, _security.Id, approve: false, "bezpečnostní riziko");
        await _db.Entry(item).ReloadAsync();
        Assert.Equal(RequestStatus.Rejected, item.Status);
    }

    [Fact]
    public async Task CycleDetection_PreventsNestingLoop()
    {
        Assert.True(await _groups.WouldCreateCycleAsync(_surgery.Id, _securityZone.Id)); // zóna už obsahuje chirurgii
        Assert.True(await _groups.WouldCreateCycleAsync(_surgery.Id, _surgery.Id));
        Assert.False(await _groups.WouldCreateCycleAsync(_securityZone.Id, _surgery.Id)); // už existuje, ale cyklus není
    }

    [Fact]
    public async Task AutoAssignment_CreatesPreApprovedGroupRequest_Idempotently()
    {
        _db.AutoAssignmentRules.Add(new AutoAssignmentRule
        {
            Department = "chirurgie", // jiná velikost písmen — musí se shodovat case-insensitive
            ReaderGroupId = _surgery.Id,
        });
        await _db.SaveChangesAsync();

        var service = new AutoAssignmentService(_db, new AuditService(_db));
        var first = await service.RunAsync("test");
        Assert.Equal(1, first.Created);

        var item = await _db.AccessRequestItems.Include(i => i.Request).SingleAsync();
        Assert.Equal(_surgery.Id, item.ReaderGroupId);
        Assert.Equal(RequestStatus.Approved, item.Status); // předschváleno → fronta správce karet
        Assert.Equal(_employee.Id, item.Request!.TargetEmployeeId);

        var second = await service.RunAsync("test");
        Assert.Equal(0, second.Created); // idempotentní
    }

    [Fact]
    public async Task CardAdmin_PushGroup_AssignsAllAccessLevels()
    {
        var calls = new List<string>();
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        await settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://c");
        var http = new HttpClient(new StubHandler(request =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return (HttpStatusCode.NoContent, "");
        }));
        var cardAdmin = new CardAdminService(_db, new WinPakClient(http, settings), new AuditService(_db), groups: _groups);

        var request = new AccessRequest
        {
            RequesterUserId = _requester.Id,
            TargetEmployeeId = _employee.Id,
            Items = [new AccessRequestItem { ReaderGroupId = _surgery.Id, Status = RequestStatus.Approved }],
        };
        _db.AccessRequests.Add(request);
        await _db.SaveChangesAsync();

        await cardAdmin.PushAsync(request.Items[0].Id, "spravce");

        Assert.Equal(2, calls.Count); // AL-OR1 + AL-CHIR
        Assert.All(calls, c => Assert.StartsWith("POST /api/v1/cardholders/CH-1/access-levels", c));
        await _db.Entry(request.Items[0]).ReloadAsync();
        Assert.Equal(RequestStatus.PushedToWinPak, request.Items[0].Status);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
