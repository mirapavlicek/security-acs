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

/// <summary>HTTP handler vracející předpřipravené JSON odpovědi podle cesty.</summary>
file sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
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

public sealed class SyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;

    public SyncServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
        _audit = new AuditService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private WinPakClient CreateWinPakClient(params object[] readers)
    {
        _settings.SetAsync(SettingKeys.WinPakBaseUrl, "http://stub").GetAwaiter().GetResult();
        _settings.SetAsync(SettingKeys.WinPakApiKey, "k").GetAwaiter().GetResult();
        var http = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/readers")
                ? (HttpStatusCode.OK, JsonSerializer.Serialize(readers))
                : (HttpStatusCode.NotFound, "{}")));
        return new WinPakClient(http, _settings);
    }

    [Fact]
    public async Task ReaderSync_AddsAndUpdatesAndDeactivates()
    {
        // Předchozí stav: jedna importovaná čtečka (zmizí z WIN-PAK) a jedna ruční.
        _db.Readers.Add(new Reader { ExternalId = "R-OLD", Name = "Stará", Source = RecordSource.Imported, IsActive = true });
        _db.Readers.Add(new Reader { Name = "Ruční čtečka", Source = RecordSource.Manual, IsActive = true });
        _db.Readers.Add(new Reader { ExternalId = "R-001", Name = "Původní název", Source = RecordSource.Imported, IsActive = true });
        await _db.SaveChangesAsync();

        var client = CreateWinPakClient(
            new { id = "R-001", name = "Hlavní vchod", description = "x", panelName = "P1", accountName = "A", isActive = true },
            new { id = "R-002", name = "Serverovna", description = (string?)null, panelName = "P1", accountName = "A", isActive = true });

        var sync = new ReaderSyncService(_db, client, _audit, new ReaderCleanupService(_db, _audit));
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Deactivated);

        var r001 = await _db.Readers.SingleAsync(r => r.ExternalId == "R-001");
        Assert.Equal("Hlavní vchod", r001.Name);

        var old = await _db.Readers.SingleAsync(r => r.ExternalId == "R-OLD");
        Assert.False(old.IsActive);

        var manual = await _db.Readers.SingleAsync(r => r.Source == RecordSource.Manual);
        Assert.True(manual.IsActive); // ruční záznamy synchronizace nesmí měnit
    }

    [Fact]
    public async Task EmployeeSync_FromApi_Upserts()
    {
        await _settings.SetAsync(SettingKeys.EmployeeSourceMode, "Api");
        await _settings.SetAsync(SettingKeys.EmployeeApiUrl, "http://hr/api/employees");

        _db.Employees.Add(new Employee
        {
            ExternalId = "E1", FirstName = "Jan", LastName = "Starý",
            Source = RecordSource.Imported, IsActive = true,
        });
        await _db.SaveChangesAsync();

        var employees = new[]
        {
            new { externalId = "E1", personalNumber = "1001", firstName = "Jan", lastName = "Novák",
                  email = (string?)"jan@fnmh.cz", department = "IT", adAccount = "jnovak", cardNumber = (string?)"100234" },
            new { externalId = "E2", personalNumber = "1002", firstName = "Petra", lastName = "Svobodová",
                  email = (string?)null, department = "HR", adAccount = "psvobodova", cardNumber = (string?)null },
        };
        var http = new HttpClient(new StubHandler(_ =>
            (HttpStatusCode.OK, JsonSerializer.Serialize(employees))));

        var source = new ApiEmployeeSource(http, _settings);
        var records = await source.FetchAsync();
        Assert.Equal(2, records.Count);

        // Upsert přes EmployeeSyncService s podvrženou factory
        var sync = new EmployeeSyncService(_db, new StubSourceFactory(source, _settings), _audit);
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);

        var e1 = await _db.Employees.SingleAsync(e => e.ExternalId == "E1");
        Assert.Equal("Novák", e1.LastName);
        Assert.Equal("jnovak", e1.AdAccount);
    }

    /// <summary>
    /// Hodnoty s mezerami (AD, nebo CHAR sloupce z MSSQL doplněné na pevnou délku)
    /// se musí ořezat — jinak osobní číslo nesedne na kartu a v seznamech vypadá divně.
    /// </summary>
    [Fact]
    public async Task EmployeeSync_TrimsWhitespaceAndEmptyValues()
    {
        var source = new StubSource([
            new EmployeeRecord(
                ExternalId: " E1 ",
                PersonalNumber: "  13483   ",
                FirstName: " Miroslav ",
                LastName: "Pavlíček ",
                Email: "   ",
                Department: " IT ",
                AdAccount: " 13483 ",
                CardNumber: null),
        ]);

        var sync = new EmployeeSyncService(_db, new StubSourceFactory(source, _settings), _audit);
        await sync.SyncAsync("test");

        var employee = await _db.Employees.SingleAsync();
        Assert.Equal("E1", employee.ExternalId);
        Assert.Equal("13483", employee.PersonalNumber);
        Assert.Equal("Miroslav", employee.FirstName);
        Assert.Equal("Pavlíček", employee.LastName);
        Assert.Equal("IT", employee.Department);
        Assert.Equal("13483", employee.AdAccount);
        // Hodnota jen z mezer nemá zůstat jako prázdný text.
        Assert.Null(employee.Email);
    }

    // ---------- Nadřízený (AD atribut manager) ----------

    /// <summary>
    /// AD vrací nadřízeného jako DN; na účet se převádí podle DN ostatních záznamů dávky.
    /// Nadřízený mimo dávku (jiná OU, zablokovaný) zůstane nespárovaný, DN se ale zachová.
    /// </summary>
    [Fact]
    public void LdapAttributes_ResolveManagers_prevadi_DN_na_ucet()
    {
        var director = new EmployeeRecord("dreditelova", null, "Dana", "Ředitelová", null, null, "dreditelova", null,
            ManagerRaw: "CN=Externí Šéf,OU=Jinde,DC=fnmh,DC=cz");
        var manager = new EmployeeRecord("vvedouci", null, "Věra", "Vedoucí", null, null, "vvedouci", null,
            ManagerRaw: "cn=Ředitelová Dana,ou=Vedení,dc=fnmh,dc=cz"); // jiná velikost písmen
        var employee = new EmployeeRecord("jnovak", null, "Jan", "Novák", null, null, "jnovak", null,
            ManagerRaw: "CN=Vedoucí Věra,OU=IT,DC=fnmh,DC=cz");
        var loner = new EmployeeRecord("self", null, "Sám", "Sobě", null, null, "self", null,
            ManagerRaw: "CN=Sám Sobě,OU=IT,DC=fnmh,DC=cz");
        var byAccount = new EmployeeRecord("acc", null, "Podle", "Účtu", null, null, "acc", null,
            ManagerRaw: "vvedouci"); // atribut s účtem místo DN

        var resolved = LdapAttributes.ResolveManagers(
        [
            (director, "CN=Ředitelová Dana,OU=Vedení,DC=fnmh,DC=cz"),
            (manager, "CN=Vedoucí Věra,OU=IT,DC=fnmh,DC=cz"),
            (employee, "CN=Novák Jan,OU=IT,DC=fnmh,DC=cz"),
            (loner, "CN=Sám Sobě,OU=IT,DC=fnmh,DC=cz"),
            (byAccount, "CN=Podle Účtu,OU=IT,DC=fnmh,DC=cz"),
        ]);

        var byId = resolved.ToDictionary(r => r.ExternalId);
        Assert.Equal("vvedouci", byId["jnovak"].ManagerExternalId);
        Assert.Equal("dreditelova", byId["vvedouci"].ManagerExternalId);
        Assert.Null(byId["dreditelova"].ManagerExternalId);            // nadřízený mimo dávku
        Assert.Equal("CN=Externí Šéf,OU=Jinde,DC=fnmh,DC=cz", byId["dreditelova"].ManagerRaw);
        Assert.Null(byId["self"].ManagerExternalId);                   // sám sobě → nic
        Assert.Equal("vvedouci", byId["acc"].ManagerExternalId);
    }

    [Fact]
    public void LdapAttributes_RequestedAttributes_obsahuji_manager_a_distinguishedName()
    {
        var requested = LdapAttributes.RequestedAttributes(["employeeID"], "extensionAttribute5");
        Assert.Contains("distinguishedName", requested);
        Assert.Contains("extensionAttribute5", requested);
        Assert.Contains(LdapAttributes.MappingDescription(["employeeID"], "extensionAttribute5"),
            m => m.Attribute == "extensionAttribute5" && m.MapsTo.Contains("nadřízený"));
    }

    [Fact]
    public void LdapAttributes_MapEmployee_uklada_surove_DN_nadrizeneho()
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = ["jnovak"],
            ["givenName"] = ["Jan"],
            ["sn"] = ["Novák"],
            ["manager"] = ["CN=Vedoucí Věra,OU=IT,DC=fnmh,DC=cz"],
        };
        var record = LdapAttributes.MapEmployee(name => values.GetValueOrDefault(name, []), ["employeeID"]);

        Assert.NotNull(record);
        Assert.Equal("CN=Vedoucí Věra,OU=IT,DC=fnmh,DC=cz", record!.ManagerRaw);
        Assert.Null(record.ManagerExternalId);
        Assert.Equal("Vedoucí Věra", LdapAttributes.DescribeManager(record.ManagerRaw));
    }

    /// <summary>
    /// Druhý průchod synchronizace nastaví ManagerId podle ExternalId nadřízeného — i když
    /// je nadřízený v dávce až za podřízeným. Ručně zadaný nadřízený se nepřepisuje.
    /// Statistika říká, u kolika zaměstnanců se nadřízený našel.
    /// </summary>
    [Fact]
    public async Task EmployeeSync_paruje_nadrizeneho_a_respektuje_rucni_zadani()
    {
        var manualBoss = new Employee { FirstName = "Ruční", LastName = "Šéf", Source = RecordSource.Manual };
        var manualEmployee = new Employee
        {
            ExternalId = "manual", FirstName = "Petr", LastName = "Ruční", Source = RecordSource.Imported,
            Manager = manualBoss, ManagerManual = true,
        };
        _db.Employees.AddRange(manualBoss, manualEmployee);
        await _db.SaveChangesAsync();

        var source = new StubSource([
            // podřízený dřív než nadřízený — druhý průchod si musí poradit
            new EmployeeRecord("jnovak", null, "Jan", "Novák", null, "IT", "jnovak", null,
                ManagerExternalId: "vvedouci", ManagerRaw: "CN=Vedoucí Věra,OU=IT,DC=fnmh,DC=cz"),
            new EmployeeRecord("vvedouci", null, "Věra", "Vedoucí", null, "IT", "vvedouci", null,
                ManagerExternalId: null, ManagerRaw: "CN=Externí,OU=Jinde,DC=fnmh,DC=cz"),
            new EmployeeRecord("bezsefa", null, "Bez", "Šéfa", null, "IT", "bezsefa", null),
            new EmployeeRecord("manual", null, "Petr", "Ruční", null, "IT", "manual", null,
                ManagerExternalId: "vvedouci", ManagerRaw: "CN=Vedoucí Věra,OU=IT,DC=fnmh,DC=cz"),
        ]);

        var sync = new EmployeeSyncService(_db, new StubSourceFactory(source, _settings), _audit, settings: _settings);
        var result = await sync.SyncAsync("test");

        var novak = await _db.Employees.Include(e => e.Manager).SingleAsync(e => e.ExternalId == "jnovak");
        var vedouci = await _db.Employees.SingleAsync(e => e.ExternalId == "vvedouci");
        var bezsefa = await _db.Employees.SingleAsync(e => e.ExternalId == "bezsefa");
        var manual = await _db.Employees.SingleAsync(e => e.ExternalId == "manual");

        Assert.Equal(vedouci.Id, novak.ManagerId);
        Assert.Equal("vvedouci", novak.ManagerAdAccount);
        Assert.Null(vedouci.ManagerId);
        Assert.Equal("CN=Externí,OU=Jinde,DC=fnmh,DC=cz", vedouci.ManagerAdAccount); // diagnostika: proč nespárováno
        Assert.Null(bezsefa.ManagerId);
        Assert.Null(bezsefa.ManagerAdAccount);
        Assert.Equal(manualBoss.Id, manual.ManagerId);   // ruční zadání import nepřepsal
        Assert.True(manual.ManagerManual);

        // Statistika: nadřízený uveden u 2 z 4 (ruční se nepočítá), spárován u 1.
        Assert.Equal(4, result.EmployeesTotal);
        Assert.Equal(2, result.ManagersInSource);
        Assert.Equal(1, result.ManagersLinked);
        Assert.Contains("nadřízený nalezen u 1 z 4", result.ToString());
        Assert.Equal(result.ManagerSummary, await _settings.GetAsync(SettingKeys.EmployeeLastManagerStats));
    }

    [Fact]
    public async Task EmployeeSync_odebere_nadrizeneho_kdyz_ho_zdroj_uz_neuvadi()
    {
        var boss = new Employee { ExternalId = "boss", FirstName = "Boss", LastName = "B", Source = RecordSource.Imported };
        var sub = new Employee { ExternalId = "sub", FirstName = "Sub", LastName = "S", Source = RecordSource.Imported, Manager = boss };
        _db.Employees.AddRange(boss, sub);
        await _db.SaveChangesAsync();

        var source = new StubSource([
            new EmployeeRecord("boss", null, "Boss", "B", null, null, "boss", null),
            new EmployeeRecord("sub", null, "Sub", "S", null, null, "sub", null),
        ]);
        var result = await new EmployeeSyncService(_db, new StubSourceFactory(source, _settings), _audit).SyncAsync("test");

        Assert.Null((await _db.Employees.SingleAsync(e => e.ExternalId == "sub")).ManagerId);
        Assert.Contains("neuvádí nadřízeného", result.ManagerSummary);
    }

    private sealed class StubSource(IReadOnlyList<EmployeeRecord> records) : IEmployeeSource
    {
        public Task<IReadOnlyList<EmployeeRecord>> FetchAsync(CancellationToken ct = default)
            => Task.FromResult(records);
    }

    private sealed class StubSourceFactory(IEmployeeSource source, SettingsService settings)
        : EmployeeSourceFactory(settings, null!, null!)
    {
        public override Task<IEmployeeSource?> CreateAsync(CancellationToken ct = default)
            => Task.FromResult<IEmployeeSource?>(source);
    }

    [Fact]
    public async Task ReaderSync_paruje_ctecky_z_dokumentace_podle_cisla_a_nechava_jim_jmeno_i_umisteni()
    {
        // Čtečka z EKV: číslo 330011, jméno s místností, rozvaděč ACS.27, bez id WIN-PAKu.
        var documented = new Reader
        {
            Name = "330011 — 23-10225 — CHODBA", DeviceNumber = "330011", PanelName = "ACS.27",
            Source = RecordSource.Manual, IsActive = true,
        };
        _db.Readers.Add(documented);
        await _db.SaveChangesAsync();

        var client = CreateWinPakClient(
            new { id = "5001", name = "330011", description = (string?)null, panelName = "330011", accountName = "FN Motol", isActive = true });

        var result = await new ReaderSyncService(_db, client, _audit, new ReaderCleanupService(_db, _audit)).SyncAsync("test");

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Added);
        var reader = await _db.Readers.SingleAsync();
        Assert.Equal("5001", reader.ExternalId);
        Assert.Equal("330011 — 23-10225 — CHODBA", reader.Name);
        Assert.Equal("ACS.27", reader.PanelName);
        Assert.Equal("FN Motol", reader.AccountName);
    }

    [Fact]
    public async Task ReaderSync_slouci_duplicitu_z_drivejsi_synchronizace_do_ctecky_z_dokumentace()
    {
        // Stav z ostrého: sync založil „330011“ (WIN-PAK, bez umístění) vedle EKV čtečky se stejným číslem.
        var synced = new Reader { ExternalId = "5001", Name = "330011", PanelName = "330011", Source = RecordSource.Imported, IsActive = true, AccessLevelExternalId = "77" };
        var documented = new Reader { Name = "330011 — 23-10225 — CHODBA", DeviceNumber = "330011", PanelName = "ACS.27", Source = RecordSource.Manual, IsActive = true };
        _db.Readers.AddRange(synced, documented);
        await _db.SaveChangesAsync();

        var client = CreateWinPakClient(
            new { id = "5001", name = "330011", description = (string?)null, panelName = "330011", accountName = "FN Motol", isActive = true });

        var result = await new ReaderSyncService(_db, client, _audit, new ReaderCleanupService(_db, _audit)).SyncAsync("test");

        Assert.Equal(1, result.Merged);
        var reader = await _db.Readers.SingleAsync();
        Assert.Equal(documented.Id, reader.Id);
        Assert.Equal(("5001", "77", "330011 — 23-10225 — CHODBA"), (reader.ExternalId, reader.AccessLevelExternalId, reader.Name));
    }

    [Fact]
    public async Task ReaderSync_duplicitu_se_zadosti_jen_deaktivuje()
    {
        var synced = new Reader { ExternalId = "5001", Name = "330011", Source = RecordSource.Imported, IsActive = true };
        var documented = new Reader { Name = "330011 — CHODBA", DeviceNumber = "330011", Source = RecordSource.Manual, IsActive = true };
        _db.Readers.AddRange(synced, documented);
        await _db.SaveChangesAsync();
        _db.ReaderGroups.Add(new ReaderGroup { Name = "G", Members = [new ReaderGroupMember { ReaderId = synced.Id }] });
        await _db.SaveChangesAsync();

        var client = CreateWinPakClient(
            new { id = "5001", name = "330011", description = (string?)null, panelName = "P", accountName = "A", isActive = true });

        await new ReaderSyncService(_db, client, _audit, new ReaderCleanupService(_db, _audit)).SyncAsync("test");

        // Deaktivace jde přes ExecuteUpdate mimo sledované entity — čte se čerstvě z databáze.
        _db.ChangeTracker.Clear();
        var readers = await _db.Readers.OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, readers.Count);
        Assert.False(readers[0].IsActive);
        Assert.Null(readers[0].ExternalId);
        Assert.Equal("5001", readers[1].ExternalId);
    }

    [Theory]
    [InlineData("330011", "330011")]
    [InlineData(" 0330011 ", "330011")]
    [InlineData("abc-01", "ABC-01")]
    public void Normalizace_cisla_ctecky(string input, string expected)
        => Assert.Equal(expected, ReaderSyncService.Normalize(input));
}
