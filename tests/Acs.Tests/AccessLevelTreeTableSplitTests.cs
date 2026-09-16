using System.Net;
using System.Text;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Strom přístupů (~70 KB na úroveň) je oddělená entita ve stejném řádku. Synchronizace úrovní
/// dřív načítala všechny úrovně i se všemi položkami jedním JOINem — v produkci 217 úrovní × 785
/// položek = 170 tisíc řádků, každý se 70 KB stromem, přes 10 GB na dotaz — a proces každou hodinu
/// zabil OOM killer. Tady se hlídá, že položky se tahají jen po úrovních a strom jen s Include(Tree).
/// </summary>
public sealed class AccessLevelTreeTableSplitTests : IDisposable
{
    private sealed class FakeConnector : HttpMessageHandler
    {
        public List<object> Levels { get; } = [];
        public Dictionary<string, string> Trees { get; } = new();
        public int TreeRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path.EndsWith("/access-levels"))
                return Task.FromResult(Json(Levels));
            if (path.EndsWith("/tree"))
            {
                TreeRequests++;
                return Task.FromResult(Json(new { accessTree = Trees.GetValueOrDefault(path.Split('/')[^2]) }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _sql = [];
    private readonly FakeConnector _connector = new();
    private readonly EphemeralDataProtectionProvider _protection = new();

    public AccessLevelTreeTableSplitTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Readers.Add(new Reader { ExternalId = "R-001", Name = "Vchod", Source = RecordSource.Imported });
        db.Readers.Add(new Reader { ExternalId = "R-002", Name = "Serverovna", Source = RecordSource.Imported });
        db.SaveChanges();
        var settings = new SettingsService(db, _protection);
        settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://stub").GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.WinPakApiKey, "k").GetAwaiter().GetResult();

        _connector.Levels.Add(new { id = "3", name = "AL Serverovna", description = (string?)null });
        _connector.Levels.Add(new { id = "4", name = "AL Vše", description = (string?)null });
        _connector.Trees["AL Serverovna"] = Tree("R-002");
        _connector.Trees["AL Vše"] = Tree("R-001", "R-002", "R-999");
    }

    private static string Tree(params string[] readers)
        => "<AccessLevel><TimeZone TimeZoneID=\"1\" TimeZoneName=\"Always\">"
           + string.Concat(readers.Select(r => $"<Reader HWDeviceID=\"{r}\" ReaderName=\"{r}\" />"))
           + "</TimeZone></AccessLevel>";

    private AcsDbContext NewContext() => new(new DbContextOptionsBuilder<AcsDbContext>()
        .UseSqlite(_connection)
        .LogTo(m => _sql.Add(m), [RelationalEventId.CommandExecuted])
        .Options);

    private AccessLevelSyncService NewSync(AcsDbContext db)
    {
        var settings = new SettingsService(db, _protection);
        return new AccessLevelSyncService(db, new WinPakClient(new HttpClient(_connector), settings), new AuditService(db));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Sync_netaha_polozky_vsech_urovni_najednou_a_strom_uklada_do_oddelene_entity()
    {
        using (var db = NewContext())
        {
            var result = await NewSync(db).SyncAsync("test");
            Assert.Equal((2, 2), (result.Added, result.TreesLoaded));
        }

        // Smrtící kombinace: řádky položek (170 tisíc) a na každém z nich 70 KB strom.
        var fatal = _sql.Where(s => s.Contains("AccessTree") && s.Contains("AccessLevelEntries")).ToList();
        Assert.True(fatal.Count == 0, string.Join("\n-----\n", fatal));
        // Strom se čte jen se seznamem úrovní (217 řádků), položky jen po jedné úrovni.
        Assert.Contains(_sql, s => s.Contains("AccessTree") && s.Contains("FROM \"AccessLevels\""));

        using var check = NewContext();
        var level = await check.AccessLevels.Include(a => a.Tree).Include(a => a.Entries).SingleAsync(a => a.ExternalId == "4");
        Assert.Equal(Tree("R-001", "R-002", "R-999"), level.Tree!.AccessTree);
        Assert.Equal(3, level.Entries.Count);
        Assert.Equal(1, level.Entries.Count(e => e.ReaderId == null));
    }

    [Fact]
    public async Task Druha_sync_bez_zmeny_strom_znovu_necte_a_polozky_nezdvojuje()
    {
        using (var db = NewContext())
            await NewSync(db).SyncAsync("test");
        var treeRequests = _connector.TreeRequests;

        using (var db = NewContext())
        {
            var result = await NewSync(db).SyncAsync("test");
            Assert.Equal(0, result.TreesLoaded);
        }

        // Strom je uložený (v oddělené entitě) — úroveň se nezměnila, WIN-PAK se na strom znovu neptá.
        Assert.Equal(treeRequests, _connector.TreeRequests);
        using var check = NewContext();
        Assert.Equal(4, await check.AccessLevelEntries.CountAsync());
    }

    [Fact]
    public async Task Zmena_nazvu_obnovi_slozeni_bez_starych_polozek()
    {
        using (var db = NewContext())
            await NewSync(db).SyncAsync("test");

        _connector.Levels.RemoveAt(1);
        _connector.Levels.Add(new { id = "4", name = "AL Vše nově", description = (string?)null });
        _connector.Trees["AL Vše nově"] = Tree("R-001");

        using (var db = NewContext())
        {
            var result = await NewSync(db).SyncAsync("test");
            Assert.Equal(1, result.TreesLoaded);
        }

        using var check = NewContext();
        var level = await check.AccessLevels.Include(a => a.Entries).SingleAsync(a => a.ExternalId == "4");
        Assert.Equal(["R-001"], level.Entries.Select(e => e.ReaderExternalId));
        Assert.Equal(2, await check.AccessLevelEntries.CountAsync());
    }

    [Fact]
    public async Task Seznam_urovni_bez_Include_strom_nenacita()
    {
        using (var db = NewContext())
            await NewSync(db).SyncAsync("test");
        _sql.Clear();

        using var db2 = NewContext();
        var levels = await db2.AccessLevels.Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync();

        Assert.Equal(2, levels.Count);
        Assert.All(levels, l => Assert.Null(l.Tree));
        Assert.DoesNotContain("AccessTree", string.Join("\n", _sql));
    }

    [Fact]
    public async Task Prehled_urovni_pocita_ctecky_v_DB_bez_nacitani_polozek()
    {
        using (var db = NewContext())
            await NewSync(db).SyncAsync("test");
        _sql.Clear();

        using var db2 = NewContext();
        var page = new Acs.Web.Pages.Catalog.AccessLevels.IndexModel(db2, null!, new SyncJobRunner(null!, null!));
        await page.OnGetAsync();

        var all = page.Levels.Single(l => l.ExternalId == "4");
        Assert.Equal((3, 1), page.EntryStats[all.Id]);
        Assert.Equal((1, 0), page.EntryStats[page.Levels.Single(l => l.ExternalId == "3").Id]);
        Assert.Empty(page.UnreadableTree);
        Assert.DoesNotContain(_sql, s => s.Contains("ReaderExternalId") && !s.Contains("COUNT"));
    }
}
