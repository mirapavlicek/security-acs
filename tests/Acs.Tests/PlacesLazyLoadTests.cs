using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using System.Security.Claims;
using Acs.Web.Pages.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Strom budov se načítá po částech — úvodní stránka nesmí tahat patra ani místnosti.</summary>
public sealed class PlacesLazyLoadTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly AcsDbContext _pageDb;
    private readonly PlacesModel _page;
    private readonly int _floorId;
    private readonly int _buildingId;

    public PlacesLazyLoadTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var building = new Building { Name = "MOC" };
        _db.Buildings.Add(building);
        _db.SaveChanges();
        _buildingId = building.Id;

        var section = new BuildingSection { BuildingId = building.Id, Name = "G", SortOrder = 1 };
        _db.BuildingSections.Add(section);
        _db.SaveChanges();

        var floor = new Floor { BuildingId = building.Id, SectionId = section.Id, Name = "2NP", SortOrder = 2 };
        var otherFloor = new Floor { BuildingId = building.Id, Name = "1NP", SortOrder = 1 };
        _db.Floors.AddRange(floor, otherFloor);
        _db.SaveChanges();
        _floorId = floor.Id;

        var corridor = new Corridor { FloorId = floor.Id, Name = "A200" };
        _db.Corridors.Add(corridor);
        _db.SaveChanges();

        var room = new Room { FloorId = floor.Id, Name = "M2001", CorridorId = corridor.Id };
        _db.Rooms.Add(room);
        _db.Rooms.Add(new Room { FloorId = otherFloor.Id, Name = "M1001" });
        _db.SaveChanges();

        _db.Readers.Add(new Reader { Name = "Čtečka M2001", RoomId = room.Id });
        _db.Readers.Add(new Reader { Name = "Čtečka A200", CorridorId = corridor.Id });
        _db.SaveChanges();

        // Stránka běží nad vlastním kontextem, aby ji neovlivnily entity načtené při přípravě dat.
        _pageDb = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _page = new PlacesModel(_pageDb, new AuditService(_pageDb))
        {
            PageContext = CreatePageContext(),
        };
    }

    /// <summary>Minimální kontext stránky — <c>Partial()</c> a audit potřebují ViewData a přihlášeného uživatele.</summary>
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

    public void Dispose()
    {
        _pageDb.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Uvodni_seznam_ma_jen_budovy_a_souhrnne_pocty()
    {
        await _page.OnGetAsync();

        var summary = Assert.Single(_page.Buildings);
        Assert.Equal("MOC", summary.Building.Name);
        Assert.Equal(1, summary.Sections);
        Assert.Equal(2, summary.Floors);
        Assert.Equal(1, summary.Corridors);
        Assert.Equal(2, summary.Rooms);
        Assert.Equal(2, summary.Readers);

        // Nic pod budovou se do paměti netahá — to je smysl lazy loadu.
        Assert.Empty(summary.Building.Floors);
        Assert.Empty(summary.Building.Sections);
    }

    [Fact]
    public async Task Rozbaleni_budovy_vrati_casti_a_patra_s_pocty()
    {
        var result = Assert.IsType<PartialViewResult>(await _page.OnGetBuildingAsync(_buildingId));
        Assert.Equal("_BuildingContent", result.ViewName);

        var content = Assert.IsType<BuildingContent>(result.Model);
        Assert.Equal("G", Assert.Single(content.Sections).Name);
        Assert.Equal(["1NP", "2NP"], content.Floors.Select(f => f.Name));

        var counts = content.FloorCounts[_floorId];
        Assert.Equal(1, counts.Rooms);
        Assert.Equal(1, counts.Corridors);
        Assert.Equal(2, counts.Readers);
    }

    [Fact]
    public async Task Rozbaleni_patra_vrati_jen_jeho_chodby_a_mistnosti()
    {
        var result = Assert.IsType<PartialViewResult>(await _page.OnGetFloorAsync(_floorId));
        Assert.Equal("_FloorContent", result.ViewName);

        var content = Assert.IsType<FloorContent>(result.Model);
        Assert.Equal("A200", Assert.Single(content.Corridors).Name);
        Assert.Equal("M2001", Assert.Single(content.Rooms).Name);
        Assert.Equal(1, content.RoomReaderCounts[content.Rooms[0].Id]);
        Assert.Equal(1, content.CorridorReaderCounts[content.Corridors[0].Id]);
    }

    [Fact]
    public async Task Neznamy_uzel_vrati_404_misto_vyjimky()
    {
        Assert.IsType<NotFoundResult>(await _page.OnGetBuildingAsync(9999));
        Assert.IsType<NotFoundResult>(await _page.OnGetFloorAsync(9999));
    }

    [Fact]
    public async Task Zmena_v_patre_vraci_odkaz_na_znovurozbaleni_uzlu()
    {
        var result = Assert.IsType<RedirectToPageResult>(
            await _page.OnPostAddCorridorAsync(_floorId, "A201"));

        Assert.Equal($"floor-{_floorId}", result.RouteValues!["open"]);
        Assert.Equal($"building-{_buildingId}", result.RouteValues["parent"]);
    }
}
