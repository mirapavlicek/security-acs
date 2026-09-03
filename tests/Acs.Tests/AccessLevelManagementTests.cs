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
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Správa přístupových úrovní WIN-PAKu z ACS: zrcadlo (synchronizace seznamu
/// a složení), automatické mapování čteček na jejich úrovně a zápis definice
/// přes konektor. Konektor nahrazuje HTTP atrapa, která si pamatuje, co dostala.
/// </summary>
public sealed class AccessLevelManagementTests : IDisposable
{
    private sealed class FakeConnector : HttpMessageHandler
    {
        public List<object> Levels { get; } = [];
        public Dictionary<string, string> Trees { get; } = new();
        public List<object> TimeZones { get; } =
        [
            new { id = "1", name = "Always", description = (string?)null, accountName = "A" },
            new { id = "7", name = "Pracovní doba", description = (string?)null, accountName = "A" },
        ];

        public List<(HttpMethod Method, string Path, string? Body)> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/access-levels"))
                    return Json(Levels);
                if (path.EndsWith("/time-zones"))
                    return Json(TimeZones);
                if (path.EndsWith("/tree"))
                {
                    var name = path.Split('/')[^2];
                    return Json(new { accessTree = Trees.GetValueOrDefault(name) });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Writes.Add((request.Method, path, body));

            // Konektor po PUT /access-levels/0 založí úroveň; atrapa ji přidá do seznamu jako WIN-PAK.
            if (request.Method == HttpMethod.Put && path.EndsWith("/access-levels/0") && body is not null)
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                Levels.Add(new { id = "900", name, description = (string?)null });
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AcsDbContext _db;
    private readonly AuditService _audit;
    private readonly FakeConnector _connector = new();
    private readonly WinPakClient _client;

    public AccessLevelManagementTests()
    {
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _audit = new AuditService(_db);
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://stub").GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.WinPakApiKey, "k").GetAwaiter().GetResult();
        _client = new WinPakClient(new HttpClient(_connector), settings);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string TreeWithOneReader = """
        <AccessLevel Name="AL Serverovna">
          <TimeZone TimeZoneID="1" TimeZoneName="Always">
            <Reader HWDeviceID="R-002" ReaderName="Serverovna" />
          </TimeZone>
        </AccessLevel>
        """;

    [Fact]
    public async Task Sync_zrcadli_urovne_a_slozeni_a_namapuje_ctecku_s_jedinou_urovni()
    {
        _db.Readers.Add(new Reader { ExternalId = "R-001", Name = "Hlavní vchod", Source = RecordSource.Imported });
        _db.Readers.Add(new Reader { ExternalId = "R-002", Name = "Serverovna", Source = RecordSource.Imported });
        _db.AccessLevels.Add(new AccessLevel { ExternalId = "9", Name = "Zrušená", IsActive = true });
        await _db.SaveChangesAsync();

        _connector.Levels.Add(new { id = "3", name = "AL Serverovna", description = "jen serverovna" });
        _connector.Levels.Add(new { id = "4", name = "AL Vchody", description = (string?)null });
        _connector.Trees["AL Serverovna"] = TreeWithOneReader;
        _connector.Trees["AL Vchody"] = """
            <AccessLevel>
              <Reader HWDeviceID="R-001" ReaderName="Hlavní vchod"><TimeZone TimeZoneID="7" TimeZoneName="Pracovní doba" /></Reader>
              <Reader HWDeviceID="R-002" ReaderName="Serverovna"><TimeZone TimeZoneID="1" TimeZoneName="Always" /></Reader>
            </AccessLevel>
            """;

        var result = await new AccessLevelSyncService(_db, _client, _audit).SyncAsync("test");

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Deactivated);
        Assert.Equal(1, result.ReadersMapped);

        var levels = await _db.AccessLevels.Include(a => a.Entries).OrderBy(a => a.Name).ToListAsync();
        var serverovna = levels.Single(l => l.Name == "AL Serverovna");
        Assert.Equal(("R-002", "Serverovna", "1", "Always"),
            (serverovna.Entries.Single().ReaderExternalId, serverovna.Entries.Single().ReaderName,
                serverovna.Entries.Single().TimeZoneExternalId, serverovna.Entries.Single().TimeZoneName));
        Assert.Equal(2, levels.Single(l => l.Name == "AL Vchody").Entries.Count);
        Assert.False(levels.Single(l => l.Name == "Zrušená").IsActive);

        // Serverovna má jedinou „svou“ úroveň → mapování doplněno; vchod je jen ve vícečtečkové úrovni → nic.
        Assert.Equal("3", (await _db.Readers.SingleAsync(r => r.ExternalId == "R-002")).AccessLevelExternalId);
        Assert.Null((await _db.Readers.SingleAsync(r => r.ExternalId == "R-001")).AccessLevelExternalId);
    }

    [Fact]
    public async Task Sync_necte_strom_znovu_kdyz_se_uroven_nezmenila()
    {
        _connector.Levels.Add(new { id = "3", name = "AL Serverovna", description = (string?)null });
        _connector.Trees["AL Serverovna"] = TreeWithOneReader;
        var sync = new AccessLevelSyncService(_db, _client, _audit);

        await sync.SyncAsync("test");
        _connector.Trees["AL Serverovna"] = "<AccessLevel />";
        await sync.SyncAsync("test");

        Assert.Single((await _db.AccessLevels.Include(a => a.Entries).SingleAsync()).Entries);

        await sync.SyncAsync("test", refreshTrees: true);
        Assert.Empty((await _db.AccessLevels.Include(a => a.Entries).SingleAsync()).Entries);
    }

    [Fact]
    public async Task Zalozeni_urovne_zapise_definici_do_WinPaku_a_namapuje_ctecku()
    {
        var reader = new Reader { ExternalId = "R-002", Name = "Serverovna", Source = RecordSource.Imported };
        _db.Readers.Add(reader);
        await _db.SaveChangesAsync();
        var sync = new AccessLevelSyncService(_db, _client, _audit);
        var admin = new AccessLevelAdminService(_db, _client, sync, _audit);

        var level = await admin.CreateAsync("test", "AL Serverovna", "popis", [new(reader.Id, "1")]);

        var write = Assert.Single(_connector.Writes);
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.EndsWith("/access-levels/0", write.Path);
        var body = JsonDocument.Parse(write.Body!).RootElement;
        Assert.Equal("AL Serverovna", body.GetProperty("name").GetString());
        Assert.Equal(["R-002"], body.GetProperty("readerIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["1"], body.GetProperty("readerTimeZoneIds").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal("900", level.ExternalId);
        Assert.Equal(("R-002", "Always"), (level.Entries.Single().ReaderExternalId, level.Entries.Single().TimeZoneName));
        Assert.Equal("900", (await _db.Readers.SingleAsync()).AccessLevelExternalId);
        Assert.Contains(await _db.AuditLogs.ToListAsync(), a => a.Action == "access-level-created");
    }

    [Fact]
    public async Task Uprava_prepise_definici_pod_id_urovne_a_zruseni_odmapuje_ctecky()
    {
        var reader = new Reader { ExternalId = "R-001", Name = "Vchod", Source = RecordSource.Imported, AccessLevelExternalId = "4" };
        _db.Readers.Add(reader);
        var level = new AccessLevel { ExternalId = "4", Name = "AL Vchody" };
        _db.AccessLevels.Add(level);
        await _db.SaveChangesAsync();
        _connector.Levels.Add(new { id = "4", name = "AL Vchody", description = (string?)null });
        var sync = new AccessLevelSyncService(_db, _client, _audit);
        var admin = new AccessLevelAdminService(_db, _client, sync, _audit);

        await admin.UpdateAsync("test", level.Id, "AL Vchody", "nový popis", [new(reader.Id, "7")]);

        var put = Assert.Single(_connector.Writes);
        Assert.EndsWith("/access-levels/4", put.Path);
        Assert.Equal("7", (await _db.AccessLevelEntries.SingleAsync()).TimeZoneExternalId);

        await admin.DeleteAsync("test", level.Id);

        Assert.Equal(HttpMethod.Delete, _connector.Writes[^1].Method);
        Assert.EndsWith("/access-levels/AL Vchody", _connector.Writes[^1].Path);
        Assert.False((await _db.AccessLevels.SingleAsync()).IsActive);
        Assert.Null((await _db.Readers.SingleAsync()).AccessLevelExternalId);
    }

    [Fact]
    public async Task Ctecka_bez_id_WinPaku_do_urovne_nejde()
    {
        var manual = new Reader { Name = "Ruční", Source = RecordSource.Manual };
        _db.Readers.Add(manual);
        await _db.SaveChangesAsync();
        var admin = new AccessLevelAdminService(_db, _client, new AccessLevelSyncService(_db, _client, _audit), _audit);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => admin.CreateAsync("test", "X", null, [new(manual.Id, "1")]));

        Assert.Contains("nemá id ve WIN-PAKu", error.Message);
        Assert.Empty(_connector.Writes);
    }

    [Theory]
    [InlineData("""<Root><Entrance EntranceID="12" EntranceName="Dveře A" TZID="3" TZName="Noc" /></Root>""", "12", "Dveře A", "3", "Noc")]
    [InlineData("""<Root><Reader><HWDeviceID>55</HWDeviceID><DeviceName>Sklad</DeviceName><TimeZoneName>Always</TimeZoneName></Reader></Root>""", "55", "Sklad", null, "Always")]
    public void Parser_stromu_rozumi_atributum_i_vnorenym_prvkum(string tree, string readerId, string readerName, string? tzId, string tzName)
    {
        var entry = Assert.Single(AccessTreeParser.Parse(tree));

        Assert.Equal((readerId, readerName, tzId, tzName),
            (entry.ReaderExternalId, entry.ReaderName, entry.TimeZoneExternalId, entry.TimeZoneName));
    }

    [Fact]
    public void Parser_stromu_rozlisuje_necitelny_text_od_urovne_bez_ctecek()
    {
        Assert.Null(AccessTreeParser.Parse("AL Serverovna: Serverovna (Always)"));
        Assert.Null(AccessTreeParser.Parse("<not xml"));
        Assert.Null(AccessTreeParser.Parse(null));
        Assert.Empty(AccessTreeParser.Parse("<AccessLevel />")!);
    }
}
