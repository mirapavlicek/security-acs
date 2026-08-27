using System.Net;
using System.Net.Http.Json;
using Acs.WinPakConnector.Models;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>REST nad rozšířenou částí API, ověřený proti mock provideru.</summary>
public sealed class CatalogEndpointTests(ConnectorFactory factory) : IClassFixture<ConnectorFactory>
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");
        return client;
    }

    private sealed record CreatedId(string Id);

    // ---------- Časové zóny ----------

    [Fact]
    public async Task Casove_zony_lze_vypsat_zalozit_upravit_i_smazat()
    {
        var client = CreateClient();

        var created = await client.PostAsJsonAsync("/api/v1/time-zones",
            new UpsertTimeZoneRequest("Noční", "22:00–06:00"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var zones = await client.GetFromJsonAsync<List<TimeZoneDto>>("/api/v1/time-zones");
        Assert.Contains(zones!, z => z.Id == id && z.Name == "Noční");

        var edited = await client.PutAsJsonAsync("/api/v1/time-zones/by-name/Noční",
            new UpsertTimeZoneRequest("Noční provoz"));
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);

        var byName = await client.GetAsync("/api/v1/time-zones/by-name/Noční provoz");
        Assert.Equal(HttpStatusCode.OK, byName.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/time-zones/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/time-zones/by-name/Noční provoz")).StatusCode);
    }

    [Fact]
    public async Task Intervaly_casove_zony_se_ulozi_a_daji_smazat()
    {
        var client = CreateClient();

        var save = await client.PutAsJsonAsync("/api/v1/time-zones/1/ranges", new[]
        {
            new TimeZoneRangeRequest(1, "06:00", "18:00"),
            new TimeZoneRangeRequest(2, "08:00", "12:00"),
        });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var ranges = await client.GetFromJsonAsync<List<TimeZoneRangeDto>>("/api/v1/time-zones/1/ranges");
        Assert.Equal(2, ranges!.Count);

        await client.DeleteAsync($"/api/v1/time-zones/1/ranges/{ranges[0].Id}");
        ranges = await client.GetFromJsonAsync<List<TimeZoneRangeDto>>("/api/v1/time-zones/1/ranges");
        Assert.Single(ranges!);
    }

    [Fact]
    public async Task Casova_zona_bez_nazvu_se_odmitne()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/time-zones",
            new UpsertTimeZoneRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Svátky ----------

    [Fact]
    public async Task Svatky_a_jejich_skupiny_maji_plny_cyklus()
    {
        var client = CreateClient();

        var created = await client.PostAsJsonAsync("/api/v1/holidays",
            new UpsertHolidayRequest("Velikonoce", 2026, 4, 6));
        var holidayId = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var holiday = await client.GetFromJsonAsync<HolidayDto>($"/api/v1/holidays/{holidayId}");
        Assert.Equal(4, holiday!.Month);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/holiday-groups",
            new UpsertHolidayGroupRequest("Jaro", [holidayId]))).StatusCode);

        var groups = await client.GetFromJsonAsync<List<HolidayGroupDto>>("/api/v1/holiday-groups");
        var group = Assert.Single(groups!, g => g.Name == "Jaro");

        var inGroup = await client.GetFromJsonAsync<List<HolidayDto>>($"/api/v1/holiday-groups/{group.Id}/holidays");
        Assert.Equal("Velikonoce", Assert.Single(inGroup!).Name);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/holiday-groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/holidays/{holidayId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/holidays/{holidayId}")).StatusCode);
    }

    // ---------- Přístupové úrovně ----------

    [Fact]
    public async Task Pristupovou_uroven_lze_najit_podle_nazvu_a_zjistit_jeji_strom()
    {
        var client = CreateClient();

        var level = await client.GetFromJsonAsync<AccessLevelDto>("/api/v1/access-levels/Serverovna");
        Assert.Equal("AL-03", level!.Id);

        var tree = await client.GetStringAsync("/api/v1/access-levels/Serverovna/tree");
        Assert.Contains("Serverovna", tree);
    }

    [Fact]
    public async Task Karty_pouzivajici_uroven_a_kandidati_na_prerazeni()
    {
        var client = CreateClient();

        var cards = await client.GetFromJsonAsync<List<CardDto>>("/api/v1/access-levels/Vstup budova A/cards");
        Assert.NotEmpty(cards!);

        var candidates = await client.GetFromJsonAsync<List<AccessLevelDto>>(
            "/api/v1/access-levels/Vstup budova A/reassign-candidates");
        Assert.DoesNotContain(candidates!, al => al.Name == "Vstup budova A");
    }

    [Fact]
    public async Task Zalozeni_urovne_bez_nazvu_se_odmitne()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/access-levels",
            new CreateAccessLevelRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Karty a držitelé ----------

    [Fact]
    public async Task Hromadne_zalozeni_karet_vyzaduje_rozsah()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/cards/bulk",
            new BulkAddCardsRequest("", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Vyhledavaci_pole_a_vyhledavani_drzitelu_funguji()
    {
        var client = CreateClient();

        var fields = await client.GetFromJsonAsync<List<CardHolderSearchFieldDto>>("/api/v1/cardholders/search-fields");
        Assert.Contains(fields!, f => f.Name == "LastName");

        var holders = await client.PostAsJsonAsync("/api/v1/cardholders/search",
            new CardHolderSearchRequest([new CardHolderSearchCriterion("LastName", "Novák")]));
        var found = await holders.Content.ReadFromJsonAsync<List<CardHolderDto>>();
        Assert.Contains(found!, h => h.LastName == "Novák");
    }

    [Fact]
    public async Task Fotku_drzitele_lze_nahrat_precist_i_smazat()
    {
        var client = CreateClient();
        var content = Convert.ToBase64String([1, 2, 3, 4]);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            "/api/v1/cardholders/CH-1001/photo/0", new ImportImageRequest(content))).StatusCode);

        var photo = await client.GetFromJsonAsync<CardHolderImageDto>("/api/v1/cardholders/CH-1001/photo/0");
        Assert.Equal(content, photo!.ContentBase64);
        Assert.Equal(4, photo.Size);

        await client.DeleteAsync("/api/v1/cardholders/CH-1001/photo/0");
        photo = await client.GetFromJsonAsync<CardHolderImageDto>("/api/v1/cardholders/CH-1001/photo/0");
        Assert.Null(photo!.ContentBase64);
    }

    // ---------- Hardware ----------

    [Fact]
    public async Task Panely_maji_vystupy_skupiny_casove_zony_i_svatky()
    {
        var client = CreateClient();

        var panels = await client.GetFromJsonAsync<List<PanelDto>>("/api/v1/panels");
        Assert.NotEmpty(panels!);

        Assert.NotEmpty((await client.GetFromJsonAsync<List<PanelPointDto>>("/api/v1/panels/1/outputs"))!);
        Assert.NotEmpty((await client.GetFromJsonAsync<List<PanelPointDto>>("/api/v1/panels/1/groups"))!);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/v1/panels/1/time-zones", new[] { "1", "2" })).StatusCode);
        var configured = await client.GetFromJsonAsync<List<TimeZoneDto>>("/api/v1/panels/1/time-zones?configured=true");
        Assert.Equal(2, configured!.Count);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/v1/panels/1/holiday-groups", new[] { "1" })).StatusCode);
        var groups = await client.GetFromJsonAsync<List<HolidayGroupDto>>("/api/v1/panels/1/holiday-groups");
        Assert.Single(groups!);
    }

    [Fact]
    public async Task Pristupove_oblasti_a_hardware_jsou_dostupne()
    {
        var client = CreateClient();

        Assert.NotEmpty((await client.GetFromJsonAsync<List<AccessAreaBranchDto>>("/api/v1/access-areas"))!);
        Assert.NotEmpty((await client.GetFromJsonAsync<List<HardwareDeviceDto>>("/api/v1/hardware"))!);
    }

    // ---------- Systém ----------

    [Fact]
    public async Task Systemove_udaje_obsahuji_operatora_i_domeny()
    {
        var info = await CreateClient().GetFromJsonAsync<SystemInfoDto>("/api/v1/system");

        Assert.Equal("WINPAK-MOCK", info!.DataSourceName);
        Assert.Equal("mock-operator", info.CurrentOperator?.Name);
        Assert.NotEmpty(info.Domains);
    }

    [Fact]
    public async Task Plan_sablona_a_odznak_jsou_ctitelne()
    {
        var client = CreateClient();

        Assert.NotNull(await client.GetFromJsonAsync<ScheduleDto>("/api/v1/schedules/1"));
        Assert.NotNull(await client.GetFromJsonAsync<TemplateDto>("/api/v1/templates/1"));

        var badge = await client.GetFromJsonAsync<BadgeDto>("/api/v1/badges/1");
        Assert.Equal(54, badge!.Height);
    }

    // ---------- Povely ----------

    [Theory]
    [InlineData("alarm/acknowledge")]
    [InlineData("alarm/clear")]
    [InlineData("shunt")]
    [InlineData("unshunt")]
    [InlineData("buffer")]
    [InlineData("unbuffer")]
    [InlineData("energize")]
    [InlineData("de-energize")]
    [InlineData("restore-time-zone")]
    public async Task Povely_pro_zarizeni_vraci_204(string command)
    {
        var response = await CreateClient().PostAsync($"/api/v1/devices/23/{command}", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Inicializace_panelu_a_jeji_zruseni_prochazi()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/panels/12/initialize",
            new PanelInitializeRequest(3, [1, 4]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/v1/panels/12/cancel-initialize", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/v1/panels/12/refresh-time-zones", null)).StatusCode);
    }

    [Fact]
    public async Task Rezim_dveri_NetAXS_se_ulozi_a_precte()
    {
        var client = CreateClient();
        var mode = new NetAxsDoorModeDto(0, 0, 2, 0, 0, 3, 1, 0, 0, 2);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/v1/doors/23/netaxs-mode", mode)).StatusCode);

        var stored = await client.GetFromJsonAsync<NetAxsDoorModeDto>("/api/v1/doors/23/netaxs-mode");
        Assert.Equal(mode, stored);
    }

    [Fact]
    public async Task Filtry_udalosti_se_pridavaji_a_odebiraji()
    {
        var client = CreateClient();

        await client.PostAsync("/api/v1/event-filters/23", null);
        var filters = await client.GetFromJsonAsync<List<string>>("/api/v1/event-filters");
        Assert.Contains("23", filters!);

        await client.DeleteAsync("/api/v1/event-filters/23");
        filters = await client.GetFromJsonAsync<List<string>>("/api/v1/event-filters");
        Assert.DoesNotContain("23", filters!);
    }

    [Fact]
    public async Task Muster_a_stav_zarizeni_vraci_data()
    {
        var client = CreateClient();

        var muster = await client.GetFromJsonAsync<MusterElementDto>("/api/v1/muster?areaId=2&accountId=8");
        Assert.Contains("Muster", muster!.Raw);

        var status = await client.GetStringAsync("/api/v1/devices/23/status");
        Assert.Contains("statusId", status);
    }

    [Theory]
    [InlineData("DeviceName", "101")]
    [InlineData("AccessLevelName", "AL-01")]
    [InlineData("TimeZoneName", "1")]
    [InlineData("AccountEmails", "")]
    [InlineData("ReaderDirectPoint", "1")]
    [InlineData("PanelGroupCheck", "1")]
    public async Task Drobne_dotazy_vraci_vysledek(string kind, string value)
    {
        var result = await CreateClient()
            .GetFromJsonAsync<LookupResultDto>($"/api/v1/lookup/{kind}?value={Uri.EscapeDataString(value)}");

        Assert.NotNull(result!.Result);
    }

    [Fact]
    public async Task Navazana_casova_zona_se_da_zjistit_pro_ctecku_i_vystup()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/associated-time-zone?accessLevelName=Serverovna&readerName=104")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/associated-time-zone?panelId=1&outputId=101")).StatusCode);
    }

    [Fact]
    public async Task Objektovy_zapis_karty_projde_pres_REST()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/v1/cards/300111/object", new UpsertCardRequest("CH-1001"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            "/api/v1/cards/300111/object", new UpsertCardRequest("CH-1001", CardStatus.Inactive))).StatusCode);

        var card = await client.GetFromJsonAsync<CardDto>("/api/v1/cards/300111");
        Assert.Equal(CardStatus.Inactive, card!.Status);
    }

    [Fact]
    public async Task Hromadne_zamknuti_dveri_a_refresh_prochazi()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/v1/doors/lock-all?accountId=8", new LockAllDoorsRequest(true))).StatusCode);

        var refresh = await client.PostAsync("/api/v1/doors/refresh?accountId=8", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }
}
