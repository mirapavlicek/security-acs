using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Karty z integrační služby (POST …/Identifiers { employeeNo, idIdentifierSubType }):
/// tolerantní rozbor odpovědi, jejíž schéma dokumentace neuvádí, a synchronizace
/// identifikátorů po zaměstnancích.
/// </summary>
public sealed class IdentifiersApiCardSourceTests : IDisposable
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Url, JsonElement Body, HttpRequestHeaders Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), JsonDocument.Parse(body).RootElement.Clone(), request.Headers));
            return responder(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Theory]
    [InlineData("""[{"identifier":"100234","validFrom":"2024-01-01","validTo":null,"active":true}]""", "100234")]
    [InlineData("""{"items":[{"cardNumber":"0012345"},{"cardNumber":"777"}]}""", "0012345", "777")]
    [InlineData("""{"employeeNo":"13483","identifiers":[{"idIdentifier":55,"identifierNo":"ABC-1","idIdentifierSubType":3}]}""", "ABC-1")]
    [InlineData("""["100234","100235"]""", "100234", "100235")]
    [InlineData("""{"identifier":"single"}""", "single")]
    [InlineData("""{"data":{"cards":[{"value":"1"}]}}""")]
    [InlineData("""[]""")]
    public void Rozbor_odpovedi_najde_hodnoty_v_ruznych_tvarech(string json, params string[] expected)
        => Assert.Equal(expected, IdentifiersApiCardSource.Parse(json).Select(i => i.Value));

    [Fact]
    public void Rozbor_cte_platnost_a_stav()
    {
        var parsed = IdentifiersApiCardSource.Parse("""
            [
              {"identifier":"1","validFrom":"2024-01-01T00:00:00","validTo":"2027-06-30T00:00:00","state":"Aktivní"},
              {"identifier":"2","status":"Blokovaná"},
              {"identifier":"3","isActive":false}
            ]
            """);

        Assert.Equal(new DateTime(2024, 1, 1), parsed[0].ValidFrom);
        Assert.Equal(new DateTime(2027, 6, 30), parsed[0].ValidTo);
        Assert.True(parsed[0].Active);
        Assert.False(parsed[1].Active);
        Assert.False(parsed[2].Active);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AcsDbContext _db;
    private readonly SettingsService _settings;

    public IdentifiersApiCardSourceTests()
    {
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FixedSourceFactory(ICardSource source) : CardSourceFactory(null!, null!)
    {
        public override Task<ICardSource> CreateAsync(CancellationToken ct = default) => Task.FromResult(source);
    }

    [Fact]
    public async Task Synchronizace_se_pta_po_osobnim_cisle_a_zalozi_karty_zamestnancum()
    {
        _db.Employees.AddRange(
            new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true },
            new Employee { FirstName = "Jana", LastName = "Nová", PersonalNumber = "20001", IsActive = true },
            new Employee { FirstName = "Bez", LastName = "Čísla", IsActive = true });
        await _db.SaveChangesAsync();

        var stub = new Stub((_, body) => body.Contains("13483")
            ? Json("""[{"identifier":"100234","validTo":"2027-01-01","active":true},{"identifier":"100235","active":false}]""")
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://ws-integrations.nnh.local/api/v0/Identifiers", 3, null, "tajny", null, null, CardApiAuth.ApiKey));
        var sync = new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db));

        var result = await sync.SyncAsync("test");

        // Dotaz jen na zaměstnance s osobním číslem, tělo přesně podle API, klíč v hlavičce.
        Assert.Equal(2, stub.Requests.Count);
        var request = stub.Requests.Single(r => r.Body.GetProperty("employeeNo").GetString() == "13483");
        Assert.Equal(3, request.Body.GetProperty("idIdentifierSubType").GetInt32());
        Assert.Equal("tajny", request.Headers.GetValues("X-Api-Key").Single());
        Assert.EndsWith("/api/v0/Identifiers", request.Url);

        // Aktivní karta založena, neaktivní vynechána, 404 = bez karty (ne chyba).
        Assert.Equal(1, result.Added);
        var identifier = await _db.EmployeeIdentifiers.Include(i => i.Employee).SingleAsync();
        Assert.Equal(("100234", IdentifierType.Card, "13483"), (identifier.Value, identifier.Type, identifier.Employee!.PersonalNumber));
        Assert.Equal(new DateTime(2027, 1, 1), identifier.ValidTo);
        Assert.Equal("100234", (await _db.Employees.SingleAsync(e => e.PersonalNumber == "13483")).CardNumber);
    }

    [Fact]
    public async Task Karta_ktera_z_API_zmizela_se_deaktivuje()
    {
        var employee = new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true };
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();
        _db.EmployeeIdentifiers.Add(new EmployeeIdentifier { EmployeeId = employee.Id, Type = IdentifierType.Card, Value = "OLD", Source = RecordSource.Imported, IsActive = true });
        await _db.SaveChangesAsync();

        var source = new IdentifiersApiCardSource(new HttpClient(new Stub((_, _) => Json("""[{"identifier":"NEW"}]"""))),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null));

        var result = await new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db)).SyncAsync("test");

        Assert.Equal((1, 1), (result.Added, result.Deactivated));
        Assert.False((await _db.EmployeeIdentifiers.SingleAsync(i => i.Value == "OLD")).IsActive);
    }

    private sealed class ListSource(params CardRecord[] records) : ICardSource
    {
        public string Description => "seznam";

        public async IAsyncEnumerable<CardRecord> ReadAsync(IReadOnlyList<Employee> employees,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var record in records)
                yield return record;
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Vice_karet_se_zalozi_vsechny_a_stejna_karta_u_tehoz_cloveka_jen_jednou()
    {
        _db.Employees.AddRange(
            new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true },
            new Employee { FirstName = "Jana", LastName = "Nová", PersonalNumber = "20001", IsActive = true });
        await _db.SaveChangesAsync();

        var source = new ListSource(
            new CardRecord(null, "13483", "100234", IdentifierType.Card, ValidTo: new DateTime(2027, 1, 1)),
            new CardRecord(null, "13483", "100235", IdentifierType.Card),
            new CardRecord(null, "13483", "100 234", IdentifierType.Card, ValidTo: new DateTime(2030, 1, 1)), // duplicita (po normalizaci)
            new CardRecord(null, "13483", "100234", IdentifierType.LicensePlate),                             // jiný typ = jiný identifikátor
            new CardRecord(null, "20001", "100234", IdentifierType.Card));                                     // jiný člověk = jiný identifikátor

        var result = await new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db)).SyncAsync("test");

        Assert.Equal((4, 1), (result.Added, result.Duplicates));
        Assert.Contains("přeskočeno duplicit 1", result.ToString());
        var pavlicek = await _db.EmployeeIdentifiers.Include(i => i.Employee)
            .Where(i => i.Employee!.PersonalNumber == "13483" && i.Type == IdentifierType.Card)
            .OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(["100234", "100235"], pavlicek.Select(i => i.Value));
        // První záznam platí — platnost z duplicity se nepřepsala.
        Assert.Equal(new DateTime(2027, 1, 1), pavlicek[0].ValidTo);
        Assert.Equal(4, await _db.EmployeeIdentifiers.CountAsync());
    }

    [Fact]
    public async Task Duplicity_uz_v_databazi_synchronizaci_neshodi_a_deaktivuji_se()
    {
        var employee = new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true };
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();
        _db.EmployeeIdentifiers.AddRange(
            new EmployeeIdentifier { EmployeeId = employee.Id, Type = IdentifierType.Card, Value = "100234", Source = RecordSource.Imported, IsActive = true },
            new EmployeeIdentifier { EmployeeId = employee.Id, Type = IdentifierType.Card, Value = "100234", Source = RecordSource.Imported, IsActive = true });
        await _db.SaveChangesAsync();

        var source = new ListSource(new CardRecord(null, "13483", "100234", IdentifierType.Card));
        var result = await new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db)).SyncAsync("test");

        Assert.Equal((0, 1), (result.Added, result.Deactivated));
        var rows = await _db.EmployeeIdentifiers.OrderBy(i => i.Id).ToListAsync();
        Assert.True(rows[0].IsActive);
        Assert.False(rows[1].IsActive);
    }

    [Fact]
    public async Task SPZ_se_stahuji_podtypem_4_a_zakladaji_jako_SPZ()
    {
        _db.Employees.Add(new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true });
        await _db.SaveChangesAsync();

        var stub = new Stub((_, body) => body.Contains("\"idIdentifierSubType\":3")
            ? Json("""[{"identifier":"100234"}]""")
            : body.Contains("\"idIdentifierSubType\":4")
                ? Json("""[{"identifier":"1AB 2345"},{"identifier":"9ZZ 0000","active":false}]""")
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null, PlateSubType: 4));

        var result = await new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db)).SyncAsync("test");

        // Dva dotazy na jednoho zaměstnance: karty (3) a SPZ (4).
        Assert.Equal([3, 4], stub.Requests.Select(r => r.Body.GetProperty("idIdentifierSubType").GetInt32()).Order());
        Assert.Equal(2, result.Added);
        var identifiers = await _db.EmployeeIdentifiers.OrderBy(i => i.Type).ToListAsync();
        Assert.Equal(IdentifierType.Card, identifiers[0].Type);
        Assert.Equal(IdentifierType.LicensePlate, identifiers[1].Type);
        Assert.Equal(EmployeeIdentifier.Normalize("1AB 2345"), identifiers[1].Value);
        Assert.Contains("SPZ podtyp 4", source.Description);
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData("", 4)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("7", 7)]
    [InlineData("x", null)]
    public void Podtyp_SPZ_z_nastaveni(string? raw, int? expected)
        => Assert.Equal(expected, IdentifiersApiCardSource.ParsePlateSubType(raw));

    [Fact]
    public async Task Zkouska_jde_poslat_na_zvoleny_podtyp()
    {
        var stub = new Stub((_, _) => Json("[]"));
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null, PlateSubType: 4));

        var probe = await source.ProbeAsync("13483", 4);

        Assert.Equal(4, probe.SubType);
        Assert.Contains("\"idIdentifierSubType\":4", probe.RequestBody);
        Assert.Equal(4, stub.Requests.Single().Body.GetProperty("idIdentifierSubType").GetInt32());
        Assert.Equal([(3, IdentifierType.Card), (4, IdentifierType.LicensePlate)], source.SubTypes);
    }

    [Theory]
    [InlineData("NNH\\svc-acs", null, "svc-acs", "NNH")]
    [InlineData("svc-acs@nnh.local", "nnh.local", "svc-acs@nnh.local", "")]
    [InlineData("svc-acs", "nnh.local", "svc-acs", "nnh.local")]
    [InlineData(" svc-acs ", null, "svc-acs", "")]
    public void Windows_ucet_se_rozlozi_na_uzivatele_a_domenu(string account, string? defaultDomain, string user, string domain)
    {
        var credential = IdentifiersApiCardSource.WindowsCredential(account, "pwd", defaultDomain);

        Assert.Equal((user, domain, "pwd"), (credential.UserName, credential.Domain, credential.Password));
    }

    [Fact]
    public void Windows_ucet_se_registruje_jen_pro_NTLM_a_pro_cely_server_sluzby()
    {
        var credential = new NetworkCredential("svc-acs", "pwd", "NNH");
        var cache = IdentifiersApiCardSource.NtlmCredentials("https://ws-integrations.nnh.local/api/v0/Identifiers", credential);

        var api = new Uri("https://ws-integrations.nnh.local/api/v0/Identifiers");
        // Handler .NET dává přednost Negotiate (SPNEGO → Kerberos); pro něj nesmí být žádný účet, aby se použilo NTLM.
        Assert.Null(cache.GetCredential(api, "Negotiate"));
        Assert.Null(cache.GetCredential(api, "Basic"));
        Assert.Same(credential, cache.GetCredential(api, "NTLM"));
        Assert.Same(credential, cache.GetCredential(api, "ntlm"));
        // Stejný server, jiná cesta (přesměrování, přihlašovací endpoint) — pořád NTLM stejným účtem.
        Assert.Same(credential, cache.GetCredential(new Uri("https://ws-integrations.nnh.local/api/v0/Auth/Login"), "NTLM"));
        Assert.Null(cache.GetCredential(new Uri("https://jiny-server.nnh.local/api/v0/Identifiers"), "NTLM"));
    }

    [Fact]
    public async Task Zkouska_pozna_ze_sluzba_NTLM_nenabizi()
    {
        var stub = new Stub((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
            return response;
        });
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, "svc", "pwd", CardApiAuth.Windows));

        var probe = await source.ProbeAsync("1");

        Assert.Equal(401, probe.StatusCode);
        Assert.Equal(["Negotiate"], probe.OfferedAuthSchemes);
        Assert.True(probe.NtlmNotOffered);
    }

    [Fact]
    public async Task Zkouska_vypise_nabizena_schemata_a_NTLM_mezi_nimi_neni_chyba()
    {
        var stub = new Stub((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
            return response;
        });
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, "svc", "pwd", CardApiAuth.Windows));

        var probe = await source.ProbeAsync("1");

        Assert.Equal(["Negotiate", "NTLM"], probe.OfferedAuthSchemes);
        Assert.False(probe.NtlmNotOffered);
    }

    [Fact]
    public async Task Basic_a_API_klic_se_posilaji_jen_ve_zvolenem_rezimu()
    {
        var stub = new Stub((_, _) => Json("[]"));
        var basic = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, "klic", "svc", "heslo", CardApiAuth.Basic));

        await basic.ProbeAsync("1");

        var headers = stub.Requests.Single().Headers;
        Assert.Equal("Basic", headers.Authorization!.Scheme);
        Assert.False(headers.Contains("X-Api-Key"));
    }

    [Theory]
    [InlineData("{\"token\":\"abc\"}", null, "abc")]
    [InlineData("{\"conclusion\":true,\"data\":{\"accessToken\":\"xyz\",\"expires\":3600}}", null, "xyz")]
    [InlineData("{\"jwt\":\"j\"}", null, "j")]
    [InlineData("{\"result\":{\"customName\":\"c\"}}", "customName", "c")]
    [InlineData("\"plain-token\"", null, "plain-token")]
    [InlineData("eyJhbGciOi...", null, "eyJhbGciOi...")]
    [InlineData("{\"conclusion\":false}", null, null)]
    public void Token_se_najde_v_ruznych_tvarech_odpovedi(string body, string? field, string? expected)
        => Assert.Equal(expected, IdentifiersApiCardSource.ExtractToken(body, field));

    [Fact]
    public async Task Prihlaseni_tokenem_posle_uzivatele_a_heslo_a_pak_Bearer()
    {
        var stub = new Stub((request, body) => request.RequestUri!.AbsolutePath.EndsWith("/Auth/Login")
            ? Json("{\"conclusion\":true,\"data\":{\"token\":\"TOKEN123\"}}")
            : request.Headers.Authorization?.Parameter == "TOKEN123"
                ? Json("[{\"identifier\":\"100234\"}]")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"conclusion\":false,\"errorDescription\":{\"errorMessage\":\"No valid token available.\"}}") });
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, "sysAcsApp@nnh.local", "tajne", CardApiAuth.Token,
                TokenUrl: "https://x/api/v0/Auth/Login"));

        var probe = await source.ProbeAsync("205094");
        await source.ProbeAsync("205095");

        Assert.True(probe.Success);
        Assert.Equal("100234", Assert.Single(probe.Parsed).Value);
        var login = stub.Requests.Single(r => r.Url.EndsWith("/Auth/Login"));
        Assert.Equal("sysAcsApp@nnh.local", login.Body.GetProperty("username").GetString());
        Assert.Equal("tajne", login.Body.GetProperty("password").GetString());
        // Token se drží — druhý dotaz se nepřihlašuje znovu.
        Assert.Equal(1, stub.Requests.Count(r => r.Url.EndsWith("/Auth/Login")));
        Assert.Contains("token získán", source.TokenStepDescription);
    }

    [Fact]
    public async Task Pevny_token_jde_jako_Bearer()
    {
        var stub = new Stub((_, _) => Json("[]"));
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null, CardApiAuth.Bearer, BearerToken: " pevny "));

        await source.ProbeAsync("1");

        Assert.Equal(("Bearer", "pevny"), (stub.Requests.Single().Headers.Authorization!.Scheme, stub.Requests.Single().Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task Zkouska_vrati_surovou_odpoved_i_rozbor()
    {
        var source = new IdentifiersApiCardSource(new HttpClient(new Stub((_, _) => Json("""{"items":[{"cardNo":"42"}]}"""))),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, "Authorization-Key", "k", null, null, CardApiAuth.ApiKey));

        var probe = await source.ProbeAsync("13483");

        Assert.True(probe.Success);
        Assert.Contains("\"cardNo\":\"42\"", probe.Body);
        Assert.Equal("42", Assert.Single(probe.Parsed).Value);
        Assert.Contains("\"employeeNo\":\"13483\"", probe.RequestBody);
        Assert.Contains("\"idIdentifierSubType\":3", probe.RequestBody);
    }

    [Fact]
    public async Task Zkouska_u_chyby_vrati_stav_a_telo_misto_vyjimky()
    {
        var source = new IdentifiersApiCardSource(new HttpClient(new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("{\"title\":\"Not Found\"}", Encoding.UTF8, "application/problem+json") })),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null));

        var probe = await source.ProbeAsync("999");

        Assert.False(probe.Success);
        Assert.Equal(404, probe.StatusCode);
        Assert.Contains("problem+json", probe.ContentType);
        Assert.Contains("Not Found", probe.Body);
        Assert.Empty(probe.Parsed);
    }
}
