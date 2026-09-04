using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using ReadersIndexModel = Acs.Web.Pages.Catalog.Readers.IndexModel;
using GroupsIndexModel = Acs.Web.Pages.Catalog.Groups.IndexModel;

namespace Acs.Tests;

/// <summary>Hromadné přiřazení schvalovací matice čtečkám a skupinám.</summary>
public sealed class BulkMatrixAssignmentTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly AcsDbContext _pageDb;
    private readonly ReadersIndexModel _page;

    private readonly int _matrixId;
    private readonly int _inactiveMatrixId;
    private readonly int _buildingAId;
    private readonly int _groupId;
    private readonly Dictionary<string, int> _readers = [];

    public BulkMatrixAssignmentTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var matrix = new ApprovalMatrix { Name = "Standardní" };
        var inactive = new ApprovalMatrix { Name = "Vyřazená", IsActive = false };
        _db.ApprovalMatrices.AddRange(matrix, inactive);

        var buildingA = new Building { Name = "MOC" };
        var buildingB = new Building { Name = "Sklad" };
        _db.Buildings.AddRange(buildingA, buildingB);
        _db.SaveChanges();
        _matrixId = matrix.Id;
        _inactiveMatrixId = inactive.Id;
        _buildingAId = buildingA.Id;

        var floorA = new Floor { BuildingId = buildingA.Id, Name = "1NP", SortOrder = 1 };
        var floorB = new Floor { BuildingId = buildingB.Id, Name = "1NP", SortOrder = 1 };
        _db.Floors.AddRange(floorA, floorB);
        _db.SaveChanges();

        var roomA = new Room { FloorId = floorA.Id, Name = "M1001" };
        var roomB = new Room { FloorId = floorB.Id, Name = "S001" };
        _db.Rooms.AddRange(roomA, roomB);
        _db.SaveChanges();

        AddReader("MOC vstup", roomA.Id);
        AddReader("MOC serverovna", roomA.Id);
        AddReader("Sklad vrata", roomB.Id);

        var group = new ReaderGroup { Name = "Chirurgie" };
        _db.ReaderGroups.Add(group);
        _db.SaveChanges();
        _groupId = group.Id;
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = group.Id, ReaderId = _readers["MOC serverovna"] });
        _db.SaveChanges();

        _pageDb = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _page = new ReadersIndexModel(
            _pageDb,
            new ReaderSyncService(
                _pageDb,
                new WinPakClient(new HttpClient(), new SettingsService(_pageDb, new EphemeralDataProtectionProvider())),
                new AuditService(_pageDb),
                new ReaderCleanupService(_pageDb, new AuditService(_pageDb))),
            new ReaderGroupService(_pageDb),
            new ReaderCleanupService(_pageDb, new AuditService(_pageDb)),
            new AuditService(_pageDb))
        {
            PageContext = CreatePageContext(),
        };
    }

    private void AddReader(string name, int roomId)
    {
        var reader = new Reader { Name = name, RoomId = roomId };
        _db.Readers.Add(reader);
        _db.SaveChanges();
        _readers[name] = reader.Id;
    }

    public void Dispose()
    {
        _pageDb.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private static PageContext CreatePageContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModelMetadataProvider>(new EmptyModelMetadataProvider());
        services.AddSingleton<ITempDataProvider, NoopTempDataProvider>();
        services.AddSingleton<ITempDataDictionaryFactory, TempDataDictionaryFactory>();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "Test")),
            RequestServices = services.BuildServiceProvider(),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        return new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
        };
    }

    private sealed class NoopTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private int? MatrixOf(string readerName)
    {
        using var fresh = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        return fresh.Readers.Single(r => r.Id == _readers[readerName]).ApprovalMatrixId;
    }

    [Fact]
    public async Task Oznacenym_ctecm_nastavi_matici_a_ostatnich_se_nedotkne()
    {
        await _page.OnPostAssignAsync([_readers["MOC vstup"], _readers["Sklad vrata"]], _matrixId);

        Assert.Equal(_matrixId, MatrixOf("MOC vstup"));
        Assert.Equal(_matrixId, MatrixOf("Sklad vrata"));
        Assert.Null(MatrixOf("MOC serverovna"));
    }

    [Fact]
    public async Task Prazdny_vyber_nic_nezmeni_a_ohlasi_chybu()
    {
        await _page.OnPostAssignAsync([], _matrixId);

        Assert.Equal("Nevybrali jste žádnou čtečku.", _page.ErrorMessage);
        Assert.Null(MatrixOf("MOC vstup"));
    }

    [Fact]
    public async Task Neaktivni_matici_nelze_priradit()
    {
        await _page.OnPostAssignAsync([_readers["MOC vstup"]], _inactiveMatrixId);

        Assert.Contains("neaktivní", _page.ErrorMessage);
        Assert.Null(MatrixOf("MOC vstup"));
    }

    [Fact]
    public async Task Prirazeni_podle_filtru_budovy_se_tyka_jen_dane_budovy()
    {
        _page.BuildingId = _buildingAId;

        await _page.OnPostAssignFilteredAsync(_matrixId);

        Assert.Equal(_matrixId, MatrixOf("MOC vstup"));
        Assert.Equal(_matrixId, MatrixOf("MOC serverovna"));
        Assert.Null(MatrixOf("Sklad vrata"));
    }

    [Fact]
    public async Task Prirazeni_bez_filtru_se_tyka_vsech_ctecek()
    {
        await _page.OnPostAssignFilteredAsync(_matrixId);

        Assert.Equal(_matrixId, MatrixOf("MOC vstup"));
        Assert.Equal(_matrixId, MatrixOf("MOC serverovna"));
        Assert.Equal(_matrixId, MatrixOf("Sklad vrata"));
    }

    [Fact]
    public async Task Filtr_podle_skupiny_bere_i_ctecky_z_vnorenych_skupin()
    {
        var parent = new ReaderGroup { Name = "Nemocnice" };
        _db.ReaderGroups.Add(parent);
        _db.SaveChanges();
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = parent.Id, ChildGroupId = _groupId });
        _db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = parent.Id, ReaderId = _readers["Sklad vrata"] });
        _db.SaveChanges();

        _page.GroupId = parent.Id;
        await _page.OnPostAssignFilteredAsync(_matrixId);

        Assert.Equal(_matrixId, MatrixOf("MOC serverovna"));   // přes vnořenou skupinu
        Assert.Equal(_matrixId, MatrixOf("Sklad vrata"));
        Assert.Null(MatrixOf("MOC vstup"));
    }

    [Fact]
    public async Task Filtr_bez_matice_najde_jen_ctecky_bez_matice()
    {
        await _page.OnPostAssignAsync([_readers["MOC vstup"]], _matrixId);
        _page.ErrorMessage = null;

        _page.Matrix = ReadersIndexModel.NoMatrixFilter;
        await _page.OnGetAsync();

        Assert.Equal(["MOC serverovna", "Sklad vrata"], _page.Readers.Select(r => r.Name).Order());
    }

    [Fact]
    public async Task Odebrani_matice_nastavi_hodnotu_zpet_na_prazdno()
    {
        await _page.OnPostAssignFilteredAsync(_matrixId);

        await _page.OnPostAssignAsync([_readers["Sklad vrata"]], null);

        Assert.Null(MatrixOf("Sklad vrata"));
        Assert.Equal(_matrixId, MatrixOf("MOC vstup"));
    }

    [Fact]
    public async Task Hromadna_zmena_se_zapise_do_auditu()
    {
        await _page.OnPostAssignAsync([_readers["MOC vstup"], _readers["Sklad vrata"]], _matrixId);

        using var fresh = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        var log = fresh.AuditLogs.Single(a => a.Action == "readers-matrix-bulk-assigned");
        Assert.Equal("admin", log.UserName);
        Assert.Contains("2 čteček", log.Details);
        Assert.Contains("Standardní", log.Details);
    }

    [Fact]
    public async Task Skupinam_lze_priradit_matici_hromadne()
    {
        var groupsPage = new GroupsIndexModel(_pageDb, new ReaderGroupService(_pageDb), new AuditService(_pageDb))
        {
            PageContext = CreatePageContext(),
        };

        await groupsPage.OnPostAssignAsync([_groupId], _matrixId);

        using var fresh = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        Assert.Equal(_matrixId, fresh.ReaderGroups.Single(g => g.Id == _groupId).ApprovalMatrixId);
    }
}
