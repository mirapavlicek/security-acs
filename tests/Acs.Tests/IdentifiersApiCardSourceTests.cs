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
            new IdentifiersApiCardSource.Options("https://ws-integrations.nnh.local/api/v0/Identifiers", 3, null, "tajny", null, null));
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

    [Fact]
    public async Task Zkouska_vrati_surovou_odpoved_i_rozbor()
    {
        var source = new IdentifiersApiCardSource(new HttpClient(new Stub((_, _) => Json("""{"items":[{"cardNo":"42"}]}"""))),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, "Authorization-Key", "k", null, null));

        var (raw, parsed) = await source.ProbeAsync("13483");

        Assert.Contains("\"cardNo\":\"42\"", raw);
        Assert.Equal("42", Assert.Single(parsed).Value);
    }
}
