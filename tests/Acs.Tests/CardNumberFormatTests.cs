using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Karty z integrační služby jedním dotazem bez filtrů, párování nad databází ACS
/// a převod čísel pro čtečky po podtypech: Homolka (3) posledních 5 číslic,
/// FN Motol (100003) pomlčka → 0, SPZ (4) bez přípony země.
/// </summary>
public sealed class CardNumberFormatTests : IDisposable
{
    private sealed class Stub(Func<string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Url, string Body, HttpRequestHeaders Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers));
            return responder(body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedSourceFactory(ICardSource source) : CardSourceFactory(null!, null!)
    {
        public override Task<ICardSource> CreateAsync(CancellationToken ct = default) => Task.FromResult(source);
    }

    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;

    public CardNumberFormatTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(CardNumberFormat.Last5, "4d-07782", "07782")]
    [InlineData(CardNumberFormat.Last5, "4D-7782", "47782")]
    [InlineData(CardNumberFormat.Last5, "07782", "07782")]
    [InlineData(CardNumberFormat.Last5, "782", "782")]
    [InlineData(CardNumberFormat.DashToZero, "123-45678", "123045678")]
    [InlineData(CardNumberFormat.DashToZero, " 123-45678 ", "123045678")]
    [InlineData(CardNumberFormat.DashToZero, "123045678", "123045678")]
    [InlineData(CardNumberFormat.Raw, "4d-07782", "4d-07782")]
    public void Format_prevede_cislo_pro_ctecky(CardNumberFormat format, string raw, string expected)
        => Assert.Equal(expected, CardNumberFormats.Apply(format, raw));

    [Fact]
    public void Vychozi_pravidla_odpovidaji_sluzbe_ws_integrations()
    {
        var rules = CardNumberFormats.ParseRules(null, out var errors);
        Assert.Empty(errors);
        Assert.Equal(
            [(3, IdentifierType.Card, CardNumberFormat.Last5), (100003, IdentifierType.Card, CardNumberFormat.DashToZero), (4, IdentifierType.LicensePlate, CardNumberFormat.Raw)],
            rules.Select(r => (r.SubType, r.Type, r.Format)));
    }

    [Fact]
    public void Pravidla_se_ctou_cesky_i_anglicky_a_chybne_radky_se_hlasi()
    {
        var rules = CardNumberFormats.ParseRules("3 = karta : posledních 5\n100003=Card:pomlčka→0 # FNM\n4 = SPZ; x = karta\n7 = cosi\n3 = Card", out var errors);
        Assert.Equal(
            [(3, IdentifierType.Card, CardNumberFormat.Last5), (100003, IdentifierType.Card, CardNumberFormat.DashToZero), (4, IdentifierType.LicensePlate, CardNumberFormat.Raw)],
            rules.Select(r => (r.SubType, r.Type, r.Format)));
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("x = karta"));
        Assert.Contains(errors, e => e.Contains("neznámý typ"));
        Assert.Contains(errors, e => e.Contains("dvakrát"));
    }

    private const string BulkResponse = """
        {"output":[
          {"employeeNo":"13483","initialCode":"4d-07782","idIdentifierSubType":3},
          {"employeeNo":"13483","initialCode":"4d-07782","idIdentifierSubType":3},
          {"employeeNo":"13483","initialCode":"1TN7287-CZE","idIdentifierSubType":4},
          {"employeeNo":"20001","initialCode":"123-45678","idIdentifierSubType":100003},
          {"employeeNo":"20001","initialCode":"4d-00001","idIdentifierSubType":3},
          {"employeeNo":"99999","initialCode":"4d-55555","idIdentifierSubType":3},
          {"employeeNo":"13483","initialCode":"neco","idIdentifierSubType":77}
        ],"conclusion":true,"resultType":"Ok"}
        """;

    [Fact]
    public void Rozbor_bez_filtru_vraci_osobni_cislo_a_podtyp_kazdeho_zaznamu()
    {
        var parsed = IdentifiersApiCardSource.ParseAll(BulkResponse);
        Assert.Equal(6, parsed.Count); // duplicitní 4d-07782 u 13483 jednou
        Assert.Contains(parsed, p => p.EmployeeNo == "20001" && p.SubType == 100003 && p.Value == "123-45678");
        Assert.Contains(parsed, p => p.EmployeeNo == "13483" && p.SubType == 4 && p.Value == "1TN7287-CZE");
    }

    [Fact]
    public async Task Synchronizace_stahne_vse_jednim_dotazem_prevede_cisla_a_ulozi_otisk()
    {
        _db.Employees.AddRange(
            new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true },
            new Employee { FirstName = "Jana", LastName = "Nová", PersonalNumber = "20001", IsActive = true });
        await _db.SaveChangesAsync();

        var stub = new Stub(_ => Json(BulkResponse));
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null,
                Rules: CardNumberFormats.Default, FetchAll: true));

        var result = await new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db)).SyncAsync("test");

        // Jediný dotaz s prázdným tělem — žádné filtry.
        var request = Assert.Single(stub.Requests);
        Assert.Equal("{}", request.Body);
        Assert.Contains("vše jedním dotazem", source.Description);
        Assert.Equal(1, source.SkippedUnknownSubType); // podtyp 77 bez pravidla

        // Čísla pro čtečky: Homolka posledních 5, FNM pomlčka → 0, SPZ bez -CZE.
        var identifiers = await _db.EmployeeIdentifiers.Include(i => i.Employee).OrderBy(i => i.Employee!.PersonalNumber).ThenBy(i => i.Value).ToListAsync();
        Assert.Equal(
            [("13483", IdentifierType.Card, "07782"), ("13483", IdentifierType.LicensePlate, "1TN7287"),
             ("20001", IdentifierType.Card, "00001"), ("20001", IdentifierType.Card, "123045678")],
            identifiers.Select(i => (i.Employee!.PersonalNumber, i.Type, i.Value)));
        Assert.Equal("podtyp 100003: 123-45678", identifiers.Single(i => i.Value == "123045678").Note);
        Assert.Equal("07782", (await _db.Employees.SingleAsync(e => e.PersonalNumber == "13483")).CardNumber);

        // Osobní číslo 99999 v ACS není — nespárováno, ale v otisku zůstává k dohledání.
        Assert.Equal(4, result.Added);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal(1, result.UnmatchedPersons);
        Assert.Equal(5, result.Fetched);
        var snapshot = await _db.ImportedIdentifiers.ToListAsync();
        Assert.Equal(5, snapshot.Count);
        var orphan = snapshot.Single(s => s.EmployeeNo == "99999");
        Assert.Null(orphan.EmployeeId);
        Assert.Equal(("4d-55555", "55555", 3), (orphan.RawValue, orphan.Value, orphan.SubType));
        Assert.All(snapshot.Where(s => s.EmployeeNo != "99999"), s => Assert.NotNull(s.EmployeeId));
        Assert.Contains("staženo 5", result.ToString());
        Assert.Contains("1 osobních čísel v ACS není", result.ToString());
    }

    [Fact]
    public async Task Otisk_se_pri_dalsi_synchronizaci_prepise()
    {
        _db.Employees.Add(new Employee { FirstName = "Miroslav", LastName = "Pavlíček", PersonalNumber = "13483", IsActive = true });
        await _db.SaveChangesAsync();

        var response = BulkResponse;
        var source = new IdentifiersApiCardSource(new HttpClient(new Stub(_ => Json(response))),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null,
                Rules: CardNumberFormats.Default, FetchAll: true));
        var sync = new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db));

        await sync.SyncAsync("test");
        Assert.Equal(5, await _db.ImportedIdentifiers.CountAsync());

        response = """{"output":[{"employeeNo":"13483","initialCode":"4d-07782","idIdentifierSubType":3}],"conclusion":true}""";
        var result = await sync.SyncAsync("test");

        Assert.Equal(1, await _db.ImportedIdentifiers.CountAsync());
        Assert.Equal(1, result.Fetched);
        // SPZ, která ze zdroje zmizela, se deaktivuje; karta zůstává.
        Assert.False((await _db.EmployeeIdentifiers.SingleAsync(i => i.Type == IdentifierType.LicensePlate)).IsActive);
        Assert.True((await _db.EmployeeIdentifiers.SingleAsync(i => i.Value == "07782")).IsActive);
    }

    [Fact]
    public async Task Rezim_po_zamestnancich_pouziva_pravidla_podtypu_take()
    {
        _db.Employees.Add(new Employee { FirstName = "Jana", LastName = "Nová", PersonalNumber = "20001", IsActive = true });
        await _db.SaveChangesAsync();

        var stub = new Stub(body => body.Contains("100003")
            ? Json("""{"output":[{"employeeNo":"20001","initialCode":"123-45678","idIdentifierSubType":100003}],"conclusion":true}""")
            : Json("""{"output":[],"conclusion":true}"""));
        var source = new IdentifiersApiCardSource(new HttpClient(stub),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null,
                Rules: CardNumberFormats.Default, FetchAll: false));

        await new CardSyncService(_db, new FixedSourceFactory(source), new AuditService(_db)).SyncAsync("test");

        Assert.Equal(3, stub.Requests.Count); // podtypy 3, 100003, 4
        Assert.All(stub.Requests, r => Assert.Contains("\"employeeNo\":\"20001\"", r.Body));
        Assert.Equal("123045678", (await _db.EmployeeIdentifiers.SingleAsync()).Value);
    }

    [Fact]
    public async Task Chybova_obalka_bez_filtru_zastavi_synchronizaci()
    {
        var source = new IdentifiersApiCardSource(
            new HttpClient(new Stub(_ => Json("""{"conclusion":false,"errorDescription":{"errorType":"NotAuthorized","errorMessage":"No valid token available."}}"""))),
            new IdentifiersApiCardSource.Options("https://x/api/v0/Identifiers", 3, null, null, null, null, Rules: CardNumberFormats.Default, FetchAll: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in source.ReadAsync([], CancellationToken.None)) { }
        });
        Assert.Contains("No valid token available", ex.Message);

        var probe = await source.ProbeAllAsync();
        Assert.Equal("{}", probe.RequestBody);
        Assert.Equal("NotAuthorized: No valid token available.", probe.ServiceError);
    }
}
