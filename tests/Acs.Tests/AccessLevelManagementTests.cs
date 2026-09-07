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

        /// <summary>Když je nastaveno, každý dotaz na strom skončí jako uvázlý WIN-PAK (504 s touto zprávou).</summary>
        public string? TreeError { get; set; }
        /// <summary>Omezí <see cref="TreeError"/> jen na vyjmenované úrovně (null = všechny).</summary>
        public HashSet<string>? TreeErrorOnly { get; set; }
        public int TreeRequests { get; private set; }

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
                    TreeRequests++;
                    var name = path.Split('/')[^2];
                    if (TreeError is not null && (TreeErrorOnly is null || TreeErrorOnly.Contains(name)))
                    {
                        return new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { error = TreeError }), Encoding.UTF8, "application/json"),
                        };
                    }
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

    /// <summary>
    /// Na ostrém serveru běžela synchronizace 28 minut a složení se nedoplnilo u žádné úrovně:
    /// WIN-PAK na každý strom neodpověděl, konektor po 90 s volání opustil a ACS chybu potichu
    /// spolkla a šla na další z 55 úrovní. Teď se po třech neúspěších v řadě čtení vzdá, seznam
    /// úrovní zůstane v zrcadle a výsledek řekne, co WIN-PAK hlásil.
    /// </summary>
    [Fact]
    public async Task Sync_se_vzda_cteni_slozeni_kdyz_WinPak_neodpovida_a_rekne_proc()
    {
        for (var i = 1; i <= 10; i++)
            _connector.Levels.Add(new { id = i.ToString(), name = $"AL {i}", description = (string?)null });
        _connector.TreeError = "WIN-PAK neodpověděl na GetAccessTreeAsync do 90 s";
        var reports = new List<string>();

        var result = await new AccessLevelSyncService(_db, _client, _audit)
            .SyncAsync("test", progress: reports.Add);

        Assert.Equal(10, result.Added);
        Assert.Equal(AccessLevelSyncService.GiveUpAfterConsecutiveFailures, result.TreesFailed);
        Assert.Equal(AccessLevelSyncService.GiveUpAfterConsecutiveFailures, _connector.TreeRequests);
        Assert.Equal(10 - AccessLevelSyncService.GiveUpAfterConsecutiveFailures, result.TreesSkipped);
        Assert.Contains("504", result.LastTreeError);
        Assert.Contains("neodpověděl na GetAccessTreeAsync do 90 s", result.LastTreeError);
        Assert.Contains("čtení složení zastaveno", result.ToString());

        // Seznam úrovní je v zrcadle i bez složení; průběh hlásil pořadí i aktuální úroveň.
        Assert.Equal(10, await _db.AccessLevels.CountAsync(a => a.IsActive));
        Assert.Contains(reports, r => r.StartsWith("složení 1/10, aktuálně „AL 1“"));
        Assert.Contains(reports, r => r.Contains("selhalo 2"));
        Assert.Contains(await _db.AuditLogs.ToListAsync(), a => a.Action == "access-levels-synced" && a.Details!.Contains("zastaveno"));
    }

    [Fact]
    public async Task Sync_pokracuje_pres_ojedinely_neuspech_stromu()
    {
        _connector.Levels.Add(new { id = "3", name = "AL Serverovna", description = (string?)null });
        _connector.Levels.Add(new { id = "4", name = "AL Vchody", description = (string?)null });
        _connector.Trees["AL Serverovna"] = TreeWithOneReader;
        var sync = new AccessLevelSyncService(_db, _client, _audit);
        await sync.SyncAsync("test");
        Assert.Equal(2, _connector.TreeRequests);

        // Druhý běh na vyžádání: strom jedné úrovně selže, druhé ne — po jednom neúspěchu se to nevzdává.
        _connector.TreeError = "chyba";
        _connector.TreeErrorOnly = ["AL Serverovna"];
        _connector.Trees["AL Vchody"] = TreeWithOneReader;
        var result = await new AccessLevelSyncService(_db, _client, _audit).SyncAsync("test", refreshTrees: true);

        Assert.Equal(1, result.TreesFailed);
        Assert.Equal(1, result.TreesLoaded);
        Assert.Equal(0, result.TreesSkipped);
        Assert.Equal(4, _connector.TreeRequests);
        var levels = await _db.AccessLevels.Include(a => a.Entries).ToListAsync();
        Assert.Single(levels.Single(a => a.Name == "AL Serverovna").Entries); // původní složení zůstalo
        Assert.Single(levels.Single(a => a.Name == "AL Vchody").Entries);     // nové se načetlo
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

    /// <summary>Strom z WIN-PAKu 4.9 (Motol): větve s rodičem, čtečky jen s názvem (= číslo čtečky) a názvem zóny, žádná id.</summary>
    private const string MotolTree =
        "<AccessTree><Branch><Name>FN Motol</Name><Parent>AccessArea</Parent></Branch>"
        + "<Branch><Name>23 MOC</Name><Parent>FN Motol</Parent></Branch>"
        + "<Reader><Name>334001</Name><Parent>23 MOC</Parent><Timezone>Always On</Timezone></Reader>"
        + "<Reader><Name>334002</Name><Parent>23 MOC</Parent><Timezone>Always On</Timezone></Reader>"
        + "<Reader><Name>341011</Name><Parent>23 MOC</Parent><Timezone>Pracovní doba</Timezone></Reader></AccessTree>";

    [Fact]
    public void Parser_rozumi_stromu_WinPaku_49_s_vetvemi_a_ctečkami_bez_id()
    {
        var entries = AccessTreeParser.Parse(MotolTree)!;

        Assert.Equal(["334001", "334002", "341011"], entries.Select(e => e.ReaderName).Order());
        Assert.All(entries, e => Assert.Null(e.ReaderExternalId));
        Assert.Equal("Pracovní doba", entries.Single(e => e.ReaderName == "341011").TimeZoneName);
        Assert.Equal("Always On", entries.First(e => e.ReaderName == "334001").TimeZoneName);
    }

    [Fact]
    public void Osnova_stromu_sklada_vetve_do_hierarchie_a_ctecky_pod_ne()
    {
        var roots = AccessTreeOutline.Parse(MotolTree)!;

        var motol = Assert.Single(roots);
        Assert.Equal("FN Motol", motol.Name);
        Assert.Empty(motol.Readers);
        var moc = Assert.Single(motol.Children);
        Assert.Equal("23 MOC", moc.Name);
        Assert.Equal(3, moc.Readers.Count);
        Assert.Equal(3, motol.ReaderCount);
        Assert.Equal(("334001", "Always On"), (moc.Readers[0].Name, moc.Readers[0].TimeZoneName));

        // Strom bez větví (jiná instalace): jeden bezejmenný kořen se všemi čtečkami.
        var flat = AccessTreeOutline.Parse(TreeWithOneReader)!;
        Assert.Equal("", Assert.Single(flat).Name);
        Assert.Equal("Serverovna", Assert.Single(flat[0].Readers).Name);

        Assert.Null(AccessTreeOutline.Parse("není xml"));
        Assert.Empty(AccessTreeOutline.Parse("<AccessTree />")!);
    }

    /// <summary>
    /// Strom Motolu nenese id čteček, jen jejich čísla — a ACS má čtečky z dokumentace EKV právě
    /// pod číslem. Synchronizace je spáruje: položka dostane čtečku ACS (a z ní id WIN-PAKu, když
    /// ho čtečka má), detail úrovně ji ukáže zaškrtnutou a úroveň s jedinou čtečkou se jí namapuje.
    /// </summary>
    [Fact]
    public async Task Sync_sparuje_ctecky_ze_stromu_podle_cisla_a_doplni_id()
    {
        var documented = new Reader { DeviceNumber = "334001", Name = "334001 — 23-50216 — DÍLNA", Source = RecordSource.Imported };
        var synced = new Reader { ExternalId = "77", Name = "334002", Source = RecordSource.Imported };
        var withZeroId = new Reader { ExternalId = "0", DeviceNumber = "0341011", Name = "341011 — SKLAD", Source = RecordSource.Imported };
        var otherWithZeroId = new Reader { ExternalId = "0", Name = "Vrátnice", Source = RecordSource.Manual };
        _db.Readers.AddRange(documented, synced, withZeroId, otherWithZeroId);
        await _db.SaveChangesAsync();

        _connector.Levels.Add(new { id = "3", name = "23 MOC", description = (string?)null });
        _connector.Levels.Add(new { id = "5", name = "Jen dílna", description = (string?)null });
        _connector.Trees["23 MOC"] = MotolTree;
        _connector.Trees["Jen dílna"] = "<AccessTree><Reader><Name>334001</Name><Timezone>Always On</Timezone></Reader></AccessTree>";

        var result = await new AccessLevelSyncService(_db, _client, _audit).SyncAsync("test");

        Assert.Equal(4, result.EntriesPaired);
        Assert.Equal(0, result.EntriesUnknown);
        var level = await _db.AccessLevels.Include(a => a.Entries).SingleAsync(a => a.Name == "23 MOC");
        var byName = level.Entries.ToDictionary(e => e.ReaderName!);
        Assert.Equal(documented.Id, byName["334001"].ReaderId);
        Assert.Null(byName["334001"].ReaderExternalId);                 // čtečka z dokumentace id nemá — nevymýšlí se
        Assert.Equal((synced.Id, "77"), (byName["334002"].ReaderId, byName["334002"].ReaderExternalId));
        Assert.Equal(withZeroId.Id, byName["341011"].ReaderId);         // id „0“ sdílí dvě čtečky → rozhodlo číslo

        // Úroveň s jedinou čtečkou je úroveň té čtečky, i když čtečka nemá id WIN-PAKu.
        Assert.Equal(1, result.ReadersMapped);
        Assert.Equal("5", (await _db.Readers.SingleAsync(r => r.Id == documented.Id)).AccessLevelExternalId);
        Assert.Contains("spárováno s ACS: 4", result.ToString());
    }

    [Fact]
    public async Task Sync_dopáruje_ctecky_ktere_pribyly_az_po_nacteni_stromu()
    {
        _connector.Levels.Add(new { id = "3", name = "23 MOC", description = (string?)null });
        _connector.Trees["23 MOC"] = MotolTree;
        var first = await new AccessLevelSyncService(_db, _client, _audit).SyncAsync("test");
        Assert.Equal((0, 3), (first.EntriesPaired, first.EntriesUnknown));
        Assert.Contains("ACS nezná: 3", first.ToString());

        // Import z EKV přidal čtečku; strom se znovu nečte (úroveň se nezměnila), přesto se spáruje.
        var reader = new Reader { DeviceNumber = "334002", Name = "334002 — CHODBA", Source = RecordSource.Imported };
        _db.Readers.Add(reader);
        await _db.SaveChangesAsync();
        _connector.TreeError = "strom se číst nemá";

        var second = await new AccessLevelSyncService(_db, _client, _audit).SyncAsync("test");

        Assert.Equal((1, 2), (second.EntriesPaired, second.EntriesUnknown));
        Assert.Equal(0, second.TreesFailed);
        Assert.Equal(reader.Id, (await _db.AccessLevelEntries.SingleAsync(e => e.ReaderName == "334002")).ReaderId);
    }

    [Fact]
    public void Parovani_ctecek_je_jen_jednoznacne()
    {
        var a = new Reader { Id = 1, DeviceNumber = "334001", Name = "334001 — A", Source = RecordSource.Imported };
        var b = new Reader { Id = 2, DeviceNumber = "334001", Name = "334001 — B", Source = RecordSource.Imported };
        var c = new Reader { Id = 3, ExternalId = "9", Name = "Hlavní vchod", Source = RecordSource.Imported };
        var matcher = new ReaderMatcher([a, b, c]);

        Assert.Null(matcher.Match(null, "334001"));            // dvě čtečky se stejným číslem — nerozhodovat za správce
        Assert.Same(c, matcher.Match("9", "cokoliv"));         // id má přednost
        Assert.Same(c, matcher.Match(null, "Hlavní vchod"));   // název čtečky ze synchronizace
        Assert.Null(matcher.Match(null, "Vchod"));             // podřetězec nestačí
        Assert.Null(matcher.Match("nope", null));
    }
}
