using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Notifications;
using Acs.Infrastructure.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Počty pro červené indikátory (navigace + dlaždice).</summary>
public sealed class AttentionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly AppUser _admin;
    private readonly Employee _employee;
    private readonly Reader _reader;

    public AttentionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _admin = new AppUser { UserName = "admin", Roles = AppRole.Admin };
        _employee = new Employee { FirstName = "Jan", LastName = "Novák" };
        _reader = new Reader { Name = "Serverovna", IsActive = true };
        _db.AddRange(_admin, _employee, _reader);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ClaimsPrincipal AdminPrincipal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, _admin.UserName),
        new Claim(ClaimTypes.NameIdentifier, _admin.Id.ToString()),
        new Claim(ClaimTypes.Role, nameof(AppRole.Admin)),
    ], "TestAuth"));

    private AttentionService CreateService()
        => new(_db, new RequestWorkflowService(_db, new AuditService(_db)));

    private void AddItem(RequestStatus status)
    {
        _db.AccessRequests.Add(new AccessRequest
        {
            RequesterUserId = _admin.Id,
            TargetEmployeeId = _employee.Id,
            Items = [new AccessRequestItem { ReaderId = _reader.Id, Status = status }],
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task PendingItemWithoutMatrix_CountsForAdmin()
    {
        AddItem(RequestStatus.Pending);
        var counts = await CreateService().GetAsync(AdminPrincipal());
        Assert.Equal(1, counts.PendingApprovals);
    }

    [Fact]
    public async Task ApprovedItem_CountsInCardQueue()
    {
        AddItem(RequestStatus.Approved);
        var counts = await CreateService().GetAsync(AdminPrincipal());
        Assert.Equal(1, counts.CardQueue);
        Assert.True(counts.Total > 0);
    }

    [Fact]
    public async Task NothingPending_MeansNoBadges()
    {
        AddItem(RequestStatus.PushedToWinPak);
        var counts = await CreateService().GetAsync(AdminPrincipal());
        Assert.Equal(0, counts.PendingApprovals);
        Assert.Equal(0, counts.CardQueue);
        Assert.Equal(0, counts.Total);
    }

    [Fact]
    public async Task AnonymousUser_GetsZeroes()
    {
        AddItem(RequestStatus.Approved);
        var counts = await CreateService().GetAsync(new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Equal(0, counts.Total);
    }

    [Fact]
    public async Task RegularEmployee_DoesNotSeeCardQueue()
    {
        AddItem(RequestStatus.Approved);
        var employeeUser = new AppUser { UserName = "zamestnanec", Roles = AppRole.Employee };
        _db.Users.Add(employeeUser);
        await _db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, employeeUser.Id.ToString()),
            new Claim(ClaimTypes.Role, nameof(AppRole.Employee)),
        ], "TestAuth"));

        var counts = await CreateService().GetAsync(principal);
        Assert.Equal(0, counts.CardQueue);
    }

    private void AddParkingItem(RequestStatus status)
    {
        var type = new ParkingPermitType { Name = "Zaměstnanec" };
        _db.ParkingPermitTypes.Add(type);
        _db.SaveChanges();
        _db.AccessRequests.Add(new AccessRequest
        {
            RequesterUserId = _admin.Id,
            TargetEmployeeId = _employee.Id,
            Items =
            [
                new AccessRequestItem
                {
                    Status = status,
                    ParkingPermit = new ParkingPermit { EmployeeId = _employee.Id, PermitTypeId = type.Id, ValidFrom = DateTime.UtcNow },
                },
            ],
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task ApprovedParkingItem_CountsInParkingQueue_NotCardQueue()
    {
        AddParkingItem(RequestStatus.Approved);
        var counts = await CreateService().GetAsync(AdminPrincipal());
        Assert.Equal(1, counts.ParkingQueue);
        Assert.Equal(0, counts.CardQueue);
        Assert.Equal(1, counts.Total);
    }

    [Fact]
    public async Task ParkingAdmin_SeesParkingQueue_ButNotCardQueue()
    {
        AddParkingItem(RequestStatus.Approved);
        AddItem(RequestStatus.Approved);
        var parkingAdmin = new AppUser { UserName = "parkovani", Roles = AppRole.ParkingAdmin };
        _db.Users.Add(parkingAdmin);
        await _db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, parkingAdmin.Id.ToString()),
            new Claim(ClaimTypes.Role, nameof(AppRole.ParkingAdmin)),
        ], "TestAuth"));

        var counts = await CreateService().GetAsync(principal);
        Assert.Equal(1, counts.ParkingQueue);
        Assert.Equal(0, counts.CardQueue);
    }
}
