using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Sekce „Funkce“ dává správcům GUI na části WIN-PAK API, které ACS nepoužívá.
/// Testuje se, že stránky vyžadují přihlášení, načtou se v režimu Mock
/// a že jednotlivé akce opravdu volají provider.
/// </summary>
public sealed class FeaturePagesTests(AdminUiFactory factory) : IClassFixture<AdminUiFactory>
{
    private static readonly string[] Pages =
    [
        "/ui/features",
        "/ui/features/doors",
        "/ui/features/panels",
        "/ui/features/cards",
        "/ui/features/access-levels",
        "/ui/features/catalog",
        "/ui/features/system",
    ];

    private async Task<HttpClient> SignInAsync()
    {
        var client = factory.CreateDefaultClient(new RedirectHandler(7), new CookieContainerHandler());
        var loginPage = await client.GetStringAsync("/ui/login");

        var response = await client.PostAsync("/ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Password"] = AdminUiFactory.ApiKey,
                ["__RequestVerificationToken"] = ExtractToken(loginPage),
            }));
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static string ExtractToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token nenalezen.");
        return match.Groups[1].Value;
    }

    /// <summary>Odešle akci na stránku sekce Funkce a vrátí výslednou stránku po přesměrování.</summary>
    private static async Task<string> PostAsync(
        HttpClient client, string page, string handler, Dictionary<string, string> fields)
    {
        var form = await client.GetStringAsync(page);
        fields["__RequestVerificationToken"] = ExtractToken(form);

        var response = await client.PostAsync($"{page}?handler={handler}", new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Vsechny_stranky_vyzaduji_prihlaseni()
    {
        var client = factory.CreateDefaultClient(new CookieContainerHandler());

        foreach (var page in Pages)
        {
            var response = await client.GetAsync(page);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Contains("/ui/login", response.Headers.Location?.ToString());
        }
    }

    [Fact]
    public async Task Vsechny_stranky_se_v_rezimu_Mock_nactou()
    {
        var client = await SignInAsync();

        foreach (var page in Pages)
        {
            var html = await client.GetStringAsync(page);

            Assert.DoesNotContain("Internal Server Error", html);
            // Načtení dat nesmí skončit chybou providera.
            Assert.DoesNotContain("alert alert-error", html);
        }
    }

    /// <summary>
    /// V režimu, který danou část API neumí, se musí ukázat důvod. Chybová stránka
    /// by správci neřekla, že stačí přepnout režim.
    /// </summary>
    [Fact]
    public async Task V_nepodporovanem_rezimu_stranky_vysvetli_duvod_misto_chyby()
    {
        using var readOnly = new ReadOnlyUiFactory();
        var client = readOnly.CreateDefaultClient(new RedirectHandler(7), new CookieContainerHandler());
        var loginPage = await client.GetStringAsync("/ui/login");
        await client.PostAsync("/ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Password"] = AdminUiFactory.ApiKey,
                ["__RequestVerificationToken"] = ExtractToken(loginPage),
            }));

        foreach (var page in Pages.Skip(1))
        {
            var response = await client.GetAsync(page);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Stránka se vykreslí a vysvětlí, proč data nejsou — místo chybové stránky.
            Assert.Contains("alert alert-error", await response.Content.ReadAsStringAsync());
        }

        // U částí, které umí jen COM API, musí být z hlášky poznat, že stačí přepnout režim.
        var catalog = await client.GetStringAsync("/ui/features/catalog");
        Assert.Contains("nepodporuje", catalog);
        Assert.Contains("režim Com", catalog);
    }

    [Fact]
    public async Task Rozcestnik_odkazuje_na_vsechny_oblasti()
    {
        var client = await SignInAsync();

        var html = await client.GetStringAsync("/ui/features");

        foreach (var page in Pages.Skip(1))
            Assert.Contains(page, html);
    }

    [Fact]
    public async Task Ovladani_dveri_zavola_provider()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/doors", "Unlock",
            new Dictionary<string, string> { ["hid"] = "23" });

        Assert.Contains("Odemknutí dveří 23 — hotovo", html);
    }

    [Fact]
    public async Task Rezim_dveri_se_nastavi_vybranou_hodnotou()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/doors", "Mode",
            new Dictionary<string, string> { ["hid"] = "23", ["mode"] = "5" });

        Assert.Contains("CardOnly", html);
    }

    [Fact]
    public async Task Detail_zarizeni_ukaze_stav_dveri()
    {
        var client = await SignInAsync();

        var html = await client.GetStringAsync("/ui/features/doors?hid=23");

        Assert.Contains("Zařízení 23", html);
        Assert.Contains("Mock dveře 23", html);
    }

    /// <summary>
    /// Odkaz „Detail“ v seznamu čteček musí vést na skutečný detail. Dřív se rozbil tím,
    /// že id čtečky nebylo číslo, a stránka se tvářila, že se nic nestalo.
    /// </summary>
    [Fact]
    public async Task Odkaz_Detail_v_seznamu_ctecek_otevre_detail()
    {
        var client = await SignInAsync();
        var list = await client.GetStringAsync("/ui/features/doors");

        var link = Regex.Match(list, @"href=""(/ui/features/doors\?hid=[^""]+)""");
        Assert.True(link.Success, "V seznamu čteček není odkaz na detail.");

        var detail = await client.GetStringAsync(WebUtility.HtmlDecode(link.Groups[1].Value));

        Assert.Contains("Ovládání dveří", detail);
        Assert.DoesNotContain("nemá číselné HID", detail);
    }

    [Fact]
    public async Task Necislene_id_zarizeni_se_vysvetli()
    {
        var client = await SignInAsync();

        var html = await client.GetStringAsync("/ui/features/doors?hid=R-001");

        Assert.Contains("nemá číselné HID", html);
    }

    [Fact]
    public async Task Alarm_lze_potvrdit_i_okomentovat()
    {
        var client = await SignInAsync();

        Assert.Contains("Potvrzení alarmu 23/1 — hotovo", await PostAsync(client, "/ui/features/doors", "Acknowledge",
            new Dictionary<string, string> { ["hid"] = "23", ["point"] = "1" }));

        Assert.Contains("Poznámka k transakci 23/1 — hotovo", await PostAsync(client, "/ui/features/doors", "Note",
            new Dictionary<string, string> { ["hid"] = "23", ["point"] = "1", ["note"] = "Prověřeno" }));
    }

    [Fact]
    public async Task Panel_prijme_nastaveni_casovych_zon()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/panels", "TimeZones",
            new Dictionary<string, string> { ["panelId"] = "1", ["timeZoneIds"] = "1" });

        Assert.Contains("Nastavení časových zón panelu 1 — hotovo", html);
    }

    [Fact]
    public async Task Inicializace_panelu_projde_i_se_seznamem_kroku()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/panels", "Initialize",
            new Dictionary<string, string> { ["hid"] = "12", ["panelType"] = "3", ["tasks"] = "1, 4" });

        Assert.Contains("Inicializace panelu 12 — hotovo", html);
    }

    [Theory]
    [InlineData("1,4", new[] { 1, 4 })]
    [InlineData(" 2 ; 3 ", new[] { 2, 3 })]
    [InlineData("", new int[0])]
    [InlineData("x,5", new[] { 5 })]
    public void Kroky_inicializace_se_ctou_z_textu(string input, int[] expected)
        => Assert.Equal(expected, Acs.WinPakConnector.Pages.Features.PanelsModel.ParseTasks(input));

    [Fact]
    public async Task Karta_se_najde_a_ulozi()
    {
        var client = await SignInAsync();

        var found = await client.GetStringAsync("/ui/features/cards?cardNumber=100234");
        Assert.Contains("Karta 100234", found);

        var html = await PostAsync(client, "/ui/features/cards", "SaveCard", new Dictionary<string, string>
        {
            ["cardNumber"] = "100234",
            ["cardHolderId"] = "CH-1001",
            ["status"] = "2",
            ["issue"] = "1",
        });

        Assert.Contains("Uložení karty 100234 — hotovo", html);
        Assert.Contains("Inactive", await client.GetStringAsync("/ui/features/cards?cardNumber=100234"));
    }

    [Fact]
    public async Task Hromadne_zalozeni_karet_zavola_provider()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/cards", "BulkAdd", new Dictionary<string, string>
        {
            ["startNumber"] = "5000",
            ["stopNumber"] = "5010",
            ["status"] = "1",
        });

        Assert.Contains("Hromadné založení karet 5000–5010 — hotovo", html);
    }

    [Fact]
    public async Task Drzitele_lze_zalozit_a_vyhledat()
    {
        var client = await SignInAsync();

        var created = await PostAsync(client, "/ui/features/cards", "AddHolder", new Dictionary<string, string>
        {
            ["firstName"] = "Eva",
            ["lastName"] = "Testovací",
        });
        Assert.Contains("Založení držitele: id CH-", created);

        var found = await PostAsync(client, "/ui/features/cards", "SearchHolders", new Dictionary<string, string>
        {
            ["field"] = "LastName",
            ["value"] = "Testovací",
            ["comparisonType"] = "0",
        });
        Assert.Contains("Testovací", found);
    }

    [Fact]
    public async Task Pristupova_uroven_ukaze_detail_i_dotcene_karty()
    {
        var client = await SignInAsync();

        var html = await client.GetStringAsync("/ui/features/access-levels?name=Vstup budova A");

        Assert.Contains("Úroveň Vstup budova A", html);
        Assert.Contains("Karty používající úroveň", html);
    }

    [Fact]
    public async Task Konfigurace_ctecek_urovne_zavola_provider()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/access-levels", "ConfigureReaders",
            new Dictionary<string, string>
            {
                ["name"] = "Serverovna",
                ["readerNames"] = "Serverovna A-105",
                ["timeZoneName"] = "Nepřetržitě",
            });

        Assert.Contains("Nastavení čteček úrovně Serverovna — hotovo", html);
    }

    [Fact]
    public async Task Casova_zona_se_zalozi_a_dostane_intervaly()
    {
        var client = await SignInAsync();

        var created = await PostAsync(client, "/ui/features/catalog", "AddTimeZone",
            new Dictionary<string, string> { ["name"] = "Test zóna" });
        Assert.Contains("Založení časové zóny Test zóna: id", created);

        var saved = await PostAsync(client, "/ui/features/catalog", "SaveRanges", new Dictionary<string, string>
        {
            ["timeZoneId"] = "1",
            ["dayType"] = "1",
            ["startTime"] = "06:00",
            ["endTime"] = "18:00",
        });
        Assert.Contains("Uložení intervalů časové zóny 1 — hotovo", saved);
        Assert.Contains("06:00", await client.GetStringAsync("/ui/features/catalog?timeZoneId=1"));
    }

    [Fact]
    public async Task Svatek_a_skupina_svatku_se_zalozi()
    {
        var client = await SignInAsync();

        var holiday = await PostAsync(client, "/ui/features/catalog", "AddHoliday", new Dictionary<string, string>
        {
            ["name"] = "Testovací svátek",
            ["year"] = "2026",
            ["month"] = "5",
            ["day"] = "1",
            ["type"] = "0",
        });
        Assert.Contains("Založení svátku Testovací svátek: id", holiday);

        var group = await PostAsync(client, "/ui/features/catalog", "AddHolidayGroup",
            new Dictionary<string, string> { ["name"] = "Testovací skupina" });
        Assert.Contains("Založení skupiny svátků Testovací skupina — hotovo", group);
    }

    [Fact]
    public async Task Vyuziti_casove_zony_se_zobrazi()
    {
        var client = await SignInAsync();

        var html = await client.GetStringAsync("/ui/features/catalog?timeZoneId=1");

        Assert.Contains("Kdo zónu používá", html);
        Assert.Contains("Přeřazení na jinou zónu", html);
    }

    [Fact]
    public async Task Systemova_stranka_ukaze_udaje_instalace()
    {
        var client = await SignInAsync();

        var html = await client.GetStringAsync("/ui/features/system");

        Assert.Contains("WINPAK-MOCK", html);
        Assert.Contains("mock-operator", html);
    }

    [Fact]
    public async Task Drobny_dotaz_vrati_vysledek()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/system", "Lookup",
            new Dictionary<string, string> { ["kind"] = "DeviceName", ["value"] = "101" });

        Assert.Contains("Hlavní vchod", html);
    }

    [Fact]
    public async Task Filtr_udalosti_se_prida_a_zobrazi()
    {
        var client = await SignInAsync();

        var added = await PostAsync(client, "/ui/features/system", "AddFilter",
            new Dictionary<string, string> { ["id"] = "23", ["commServer"] = "false" });
        Assert.Contains("Přidání filtru 23 — hotovo", added);

        Assert.Contains("23", await client.GetStringAsync("/ui/features/system"));

        var removed = await PostAsync(client, "/ui/features/system", "RemoveFilter",
            new Dictionary<string, string> { ["id"] = "23", ["commServer"] = "false" });
        Assert.Contains("Odebrání filtru 23 — hotovo", removed);
    }

    [Fact]
    public async Task Muster_report_vrati_data()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/system", "Muster", new Dictionary<string, string>
        {
            ["areaId"] = "2",
            ["accountId"] = "1",
            ["sortField"] = "0",
            ["sortOrder"] = "0",
        });

        Assert.Contains("Muster report", html);
    }

    [Fact]
    public async Task Ulozeni_planu_reportu_projde()
    {
        var client = await SignInAsync();

        var html = await PostAsync(client, "/ui/features/system", "SaveSchedule", new Dictionary<string, string>
        {
            ["scheduleId"] = "7",
            ["name"] = "Denní report",
            ["scheduleType"] = "1",
            ["frequency"] = "2",
            ["reportType"] = "3",
        });

        Assert.Contains("Uložení plánu reportu 7 — hotovo", html);
    }
}
