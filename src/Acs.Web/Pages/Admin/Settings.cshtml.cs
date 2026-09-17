using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Integration;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Sync;
using Acs.Infrastructure.WinPak;
using Acs.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Admin;

public class SettingsModel(SettingsService settings, AuditService audit, WinPakClient winPak,
    IHttpClientFactory httpClientFactory, ParkingConnectorClient parkingConnector,
    Acs.Infrastructure.Notifications.EmailNotificationService email, Acs.Infrastructure.Data.AcsDbContext db) : PageModel
{
    [TempData] public string? SmtpTestResult { get; set; }

    public Dictionary<string, string?> Values { get; } = new();

    [TempData] public string? SavedSection { get; set; }
    [TempData] public string? WinPakTestResult { get; set; }
    [TempData] public string? ParkingSystemTestResult { get; set; }
    /// <summary>Výsledek zkoušky přihlášení Windows (nastavuje stránka /Account/WindowsLogin?test=1).</summary>
    [TempData] public string? SsoTestResult { get; set; }

    /// <summary>Diagnostika přihlášení Windows na tomto nodu (keytab, očekávané SPN).</summary>
    public string? SsoKeytabPath { get; private set; }
    public bool SsoKeytabExists { get; private set; }
    public string SsoHostName => Request.Host.Host;
    public string SsoHostInfo => $"{Environment.MachineName} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})";

    /// <summary>Je nastavený klíč integračního API (hodnota se nezobrazuje)?</summary>
    public bool ParkingApiKeySet { get; private set; }

    /// <summary>Absolutní adresa integračního API pro předání dodavateli parkovacího systému.</summary>
    public string IntegrationApiUrl => $"{Request.Scheme}://{Request.Host}{IntegrationApi.Prefix}";

    private static readonly string[] DisplayedKeys =
    [
        SettingKeys.AppTitle, SettingKeys.DefaultTheme,
        SettingKeys.LdapEnabled, SettingKeys.LdapServer, SettingKeys.LdapPort, SettingKeys.LdapUseSsl,
        SettingKeys.LdapBaseDn, SettingKeys.LdapDomain, SettingKeys.LdapUserFilter, SettingKeys.LdapGroupRoleMap,
        SettingKeys.LdapBindUser, SettingKeys.LdapUseDcLocator,
        SettingKeys.SsoEnabled, SettingKeys.SsoAutoLogin, SettingKeys.SsoAllowedDomains,
        SettingKeys.WinPakBaseUrl, SettingKeys.WinPakSyncEnabled, SettingKeys.WinPakSyncIntervalMinutes,
        SettingKeys.WinPakAccessSyncEnabled, SettingKeys.WinPakAccessSyncIntervalMinutes,
        SettingKeys.EmployeeSourceMode, SettingKeys.EmployeeMssqlQuery, SettingKeys.EmployeeApiUrl,
        SettingKeys.EmployeeLdapFilter, SettingKeys.EmployeeLdapPageSize, SettingKeys.EmployeeLdapTimeoutMinutes,
        SettingKeys.EmployeePersonalNumberAttribute, SettingKeys.EmployeeLdapManagerAttribute,
        SettingKeys.EmployeeLastManagerStats,
        SettingKeys.CardsMssqlQuery, SettingKeys.CardsSyncEnabled, SettingKeys.CardsSyncIntervalMinutes,
        SettingKeys.AutomationEnabled, SettingKeys.AutomationIntervalMinutes, SettingKeys.AutoOffboardingEnabled,
        SettingKeys.AutoDepartmentChangeEnabled, SettingKeys.AutoExpirationEnabled, SettingKeys.AutoRemindersEnabled,
        SettingKeys.AutoReminderAfterDays, SettingKeys.AutoEscalationAfterDays, SettingKeys.AutoPushEnabled,
        SettingKeys.SmtpHost, SettingKeys.SmtpPort, SettingKeys.SmtpUser, SettingKeys.SmtpFrom,
        SettingKeys.SmtpUseTls, SettingKeys.SmtpIgnoreTlsErrors,
        SettingKeys.CardsSource, SettingKeys.CardsApiUrl, SettingKeys.CardsApiSubType, SettingKeys.CardsApiPlateSubType, SettingKeys.CardsApiAuth, SettingKeys.CardsApiKeyHeader,
        SettingKeys.CardsApiTokenUrl, SettingKeys.CardsApiTokenBody, SettingKeys.CardsApiTokenField,
        SettingKeys.CardsApiUser, SettingKeys.CardsApiIgnoreTls, SettingKeys.CardsApiFetchMode, SettingKeys.CardsApiSubTypeRules,
        SettingKeys.CardsLastImportStats,
        SettingKeys.ParkingSystemEnabled, SettingKeys.ParkingSystemAllowedIps, SettingKeys.ParkingSystemCacheTtlSeconds,
        SettingKeys.ParkingSystemExitAlwaysAllowed, SettingKeys.ParkingSystemConnectorBaseUrl,
        SettingKeys.ParkingSystemPushEnabled,
    ];

    /// <summary>Výsledek zkoušky integračního API karet (surová odpověď a co z ní konektor přečte).</summary>
    [TempData] public string? CardsApiProbe { get; set; }

    /// <summary>Odkaz do administrace konektoru — vlastní nastavení má konektor u sebe na serveru.</summary>
    public string? WinPakAdminUrl { get; private set; }

    /// <param name="ssoTest">Kód selhání zkoušky přihlášení Windows, když selhal už samotný Negotiate handler.</param>
    public async Task OnGetAsync(string? ssoTest = null)
    {
        await LoadAsync();
        if (ssoTest is not null)
        {
            // Přímo z query (ne TempData), aby se zpráva neukázala ještě jednou při dalším otevření stránky.
            SsoTestFromQuery = "Selhalo — server token prohlížeče neověřil (chybí keytab, jiné SPN nebo NTLM bez podpory); "
                             + "podrobnosti v logu acs-web (journalctl -u acs-web).";
            SectionFromQuery = "sso";
        }
    }

    public string? SsoTestFromQuery { get; private set; }
    public string? SectionFromQuery { get; private set; }

    private async Task LoadAsync()
    {
        foreach (var key in DisplayedKeys)
            Values[key] = await settings.GetAsync(key);
        ParkingApiKeySet = !string.IsNullOrEmpty(await settings.GetAsync(SettingKeys.ParkingSystemApiKey));

        SsoKeytabPath = KerberosKeytab.IsConfigured ? KerberosKeytab.Path : null;
        SsoKeytabExists = KerberosKeytab.IsAvailable;

        WinPakAdminUrl = Values[SettingKeys.WinPakBaseUrl] is { Length: > 0 } baseUrl
                         && Uri.TryCreate(baseUrl.TrimEnd('/') + "/ui", UriKind.Absolute, out var uri)
            ? uri.ToString()
            : null;
    }

    private string UserName => User.Identity?.Name ?? "?";

    public async Task<IActionResult> OnPostGeneralAsync(string? appTitle, string? defaultTheme)
    {
        await settings.SetAsync(SettingKeys.AppTitle, appTitle, UserName);
        await settings.SetAsync(SettingKeys.DefaultTheme, defaultTheme, UserName);
        return await SavedAsync("Obecné");
    }

    public async Task<IActionResult> OnPostLdapAsync(
        string? ldapEnabled, string? ldapServer, string? ldapPort, string? ldapUseSsl,
        string? ldapBaseDn, string? ldapDomain, string? ldapUserFilter, string? ldapBindPassword,
        string? ldapGroupRoleMap, string? ldapBindUser, string? ldapUseDcLocator)
    {
        await settings.SetAsync(SettingKeys.LdapUseDcLocator, ldapUseDcLocator == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.LdapBindUser, ldapBindUser, UserName);
        await settings.SetAsync(SettingKeys.LdapGroupRoleMap, ldapGroupRoleMap, UserName);
        await settings.SetAsync(SettingKeys.LdapEnabled, ldapEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.LdapServer, ldapServer, UserName);
        await settings.SetAsync(SettingKeys.LdapPort, ldapPort, UserName);
        await settings.SetAsync(SettingKeys.LdapUseSsl, ldapUseSsl == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.LdapBaseDn, ldapBaseDn, UserName);
        await settings.SetAsync(SettingKeys.LdapDomain, ldapDomain, UserName);
        await settings.SetAsync(SettingKeys.LdapUserFilter, ldapUserFilter, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.LdapBindPassword, ldapBindPassword, UserName);
        return await SavedAsync("Active Directory");
    }

    public async Task<IActionResult> OnPostSsoAsync(string? ssoEnabled, string? ssoAutoLogin, string? ssoAllowedDomains)
    {
        await settings.SetAsync(SettingKeys.SsoEnabled, ssoEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.SsoAutoLogin, ssoAutoLogin == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.SsoAllowedDomains, ssoAllowedDomains?.Trim(), UserName);
        return await SavedAsync("Přihlášení Windows");
    }

    public async Task<IActionResult> OnPostWinPakAsync(
        string? winPakBaseUrl, string? winPakApiKey, string? winPakSyncEnabled, string? winPakSyncIntervalMinutes,
        string? winPakAccessSyncEnabled, string? winPakAccessSyncIntervalMinutes)
    {
        await settings.SetAsync(SettingKeys.WinPakBaseUrl, winPakBaseUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.WinPakApiKey, winPakApiKey, UserName);
        await settings.SetAsync(SettingKeys.WinPakSyncEnabled, winPakSyncEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.WinPakSyncIntervalMinutes, winPakSyncIntervalMinutes, UserName);
        await settings.SetAsync(SettingKeys.WinPakAccessSyncEnabled, winPakAccessSyncEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.WinPakAccessSyncIntervalMinutes, winPakAccessSyncIntervalMinutes, UserName);
        return await SavedAsync("WIN-PAK");
    }

    public async Task<IActionResult> OnPostWinPakTestAsync(
        string? winPakBaseUrl, string? winPakApiKey, string? winPakSyncEnabled, string? winPakSyncIntervalMinutes,
        string? winPakAccessSyncEnabled, string? winPakAccessSyncIntervalMinutes)
    {
        await OnPostWinPakAsync(winPakBaseUrl, winPakApiKey, winPakSyncEnabled, winPakSyncIntervalMinutes,
            winPakAccessSyncEnabled, winPakAccessSyncIntervalMinutes);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var info = await winPak.GetInfoAsync(cts.Token);
            WinPakTestResult = info is null
                ? "konektor neodpověděl"
                : $"OK — režim {info.ProviderMode}, verze {info.Version}, zápis: {(info.SupportsWrite ? "ano" : "ne")}";
        }
        catch (Exception ex)
        {
            WinPakTestResult = $"Chyba: {ex.Message}";
        }

        ActiveSection = "winpak";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEmployeesAsync(
        string? employeeSourceMode, string? employeeMssqlConnectionString,
        string? employeeMssqlQuery, string? employeeApiUrl, string? employeeApiKey,
        string? employeeLdapFilter, string? employeeLdapPageSize, string? employeeLdapTimeoutMinutes,
        string? employeePersonalNumberAttribute, string? employeeLdapManagerAttribute)
    {
        await settings.SetAsync(SettingKeys.EmployeeLdapManagerAttribute, employeeLdapManagerAttribute, UserName);
        await settings.SetAsync(SettingKeys.EmployeeLdapPageSize, employeeLdapPageSize, UserName);
        await settings.SetAsync(SettingKeys.EmployeeLdapTimeoutMinutes, employeeLdapTimeoutMinutes, UserName);
        await settings.SetAsync(SettingKeys.EmployeeSourceMode, employeeSourceMode, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.EmployeeMssqlConnectionString, employeeMssqlConnectionString, UserName);
        await settings.SetAsync(SettingKeys.EmployeeMssqlQuery, employeeMssqlQuery, UserName);
        await settings.SetAsync(SettingKeys.EmployeeApiUrl, employeeApiUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.EmployeeApiKey, employeeApiKey, UserName);
        await settings.SetAsync(SettingKeys.EmployeeLdapFilter, employeeLdapFilter, UserName);
        await settings.SetAsync(SettingKeys.EmployeePersonalNumberAttribute, employeePersonalNumberAttribute, UserName);
        return await SavedAsync("Zdroj zaměstnanců");
    }

    public async Task<IActionResult> OnPostAutomationAsync(
        string? automationEnabled, string? automationIntervalMinutes,
        string? autoOffboardingEnabled, string? autoDepartmentChangeEnabled, string? autoExpirationEnabled,
        string? autoRemindersEnabled, string? autoReminderAfterDays, string? autoEscalationAfterDays,
        string? autoPushEnabled)
    {
        await settings.SetAsync(SettingKeys.AutomationEnabled, Flag(automationEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutomationIntervalMinutes, automationIntervalMinutes, UserName);
        await settings.SetAsync(SettingKeys.AutoOffboardingEnabled, Flag(autoOffboardingEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoDepartmentChangeEnabled, Flag(autoDepartmentChangeEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoExpirationEnabled, Flag(autoExpirationEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoRemindersEnabled, Flag(autoRemindersEnabled), UserName);
        await settings.SetAsync(SettingKeys.AutoReminderAfterDays, autoReminderAfterDays, UserName);
        await settings.SetAsync(SettingKeys.AutoEscalationAfterDays, autoEscalationAfterDays, UserName);
        await settings.SetAsync(SettingKeys.AutoPushEnabled, Flag(autoPushEnabled), UserName);
        return await SavedAsync("Automatizace");
    }

    private static string Flag(string? value) => value == "true" ? "true" : "false";

    public async Task<IActionResult> OnPostCardsAsync(
        string? cardsSource, string? cardsMssqlConnectionString, string? cardsMssqlQuery,
        string? cardsApiUrl, string? cardsApiSubType, string? cardsApiPlateSubType, string? cardsApiAuth, string? cardsApiKeyHeader, string? cardsApiKey,
        string? cardsApiUser, string? cardsApiPassword, string? cardsApiIgnoreTls,
        string? cardsApiBearerToken, string? cardsApiTokenUrl, string? cardsApiTokenBody, string? cardsApiTokenField,
        string? cardsSyncEnabled, string? cardsSyncIntervalMinutes,
        string? cardsApiFetchMode = null, string? cardsApiSubTypeRules = null)
    {
        // Pravidla podtypů se uloží jen srozumitelná; chybné řádky se vypíší a nastavení se neuloží.
        var rules = CardNumberFormats.ParseRules(cardsApiSubTypeRules, out var ruleErrors);
        if (ruleErrors.Count > 0 || rules.Count == 0)
        {
            CardsApiProbe = "Pravidla podtypů se neuložila:\n" + string.Join("\n", ruleErrors)
                + (rules.Count == 0 ? "\nNezůstalo žádné pravidlo." : "")
                + "\n\nTvar: „podtyp = typ : formát“ na řádek, např. 100003 = Card : DashToZero";
            ActiveSection = "cards";
            return RedirectToPage();
        }
        await settings.SetAsync(SettingKeys.CardsApiSubTypeRules, string.IsNullOrWhiteSpace(cardsApiSubTypeRules) ? null : cardsApiSubTypeRules.Trim(), UserName);
        await settings.SetAsync(SettingKeys.CardsApiFetchMode,
            cardsApiFetchMode == CardApiFetchMode.PerEmployee ? CardApiFetchMode.PerEmployee : CardApiFetchMode.All, UserName);

        await settings.SetAsync(SettingKeys.CardsSource, cardsSource == CardSources.Api ? CardSources.Api : CardSources.Mssql, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.CardsMssqlConnectionString, cardsMssqlConnectionString, UserName);
        await settings.SetAsync(SettingKeys.CardsMssqlQuery, cardsMssqlQuery, UserName);
        await settings.SetAsync(SettingKeys.CardsApiUrl, cardsApiUrl?.Trim(), UserName);
        await settings.SetAsync(SettingKeys.CardsApiSubType, IdentifiersApiCardSource.ParseCardSubType(cardsApiSubType).ToString(), UserName);
        // Prázdné pole = SPZ nestahovat; ukládá se jako "0", protože chybějící klíč znamená výchozí podtyp 4.
        await settings.SetAsync(SettingKeys.CardsApiPlateSubType, string.IsNullOrWhiteSpace(cardsApiPlateSubType) ? "0" : cardsApiPlateSubType.Trim(), UserName);
        await settings.SetAsync(SettingKeys.CardsApiAuth,
            cardsApiAuth is CardApiAuth.ApiKey or CardApiAuth.Basic or CardApiAuth.Windows or CardApiAuth.Bearer or CardApiAuth.Token
                ? cardsApiAuth : CardApiAuth.None, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.CardsApiBearerToken, cardsApiBearerToken, UserName);
        await settings.SetAsync(SettingKeys.CardsApiTokenUrl, cardsApiTokenUrl?.Trim(), UserName);
        await settings.SetAsync(SettingKeys.CardsApiTokenBody, string.IsNullOrWhiteSpace(cardsApiTokenBody) ? null : cardsApiTokenBody.Trim(), UserName);
        await settings.SetAsync(SettingKeys.CardsApiTokenField, cardsApiTokenField?.Trim(), UserName);
        await settings.SetAsync(SettingKeys.CardsApiKeyHeader, cardsApiKeyHeader?.Trim(), UserName);
        await settings.SetIfProvidedAsync(SettingKeys.CardsApiKey, cardsApiKey, UserName);
        await settings.SetAsync(SettingKeys.CardsApiUser, cardsApiUser?.Trim(), UserName);
        await settings.SetIfProvidedAsync(SettingKeys.CardsApiPassword, cardsApiPassword, UserName);
        await settings.SetAsync(SettingKeys.CardsApiIgnoreTls, cardsApiIgnoreTls == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.CardsSyncEnabled, cardsSyncEnabled == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.CardsSyncIntervalMinutes, cardsSyncIntervalMinutes, UserName);
        return await SavedAsync("Karty");
    }

    /// <summary>Zkouška integračního API na jednom osobním čísle — ukáže surovou odpověď i rozbor, protože schéma odpovědi dokumentace neuvádí.</summary>
    /// <param name="employeeNo">Osobní číslo; prázdné = zkouška dotazu bez filtrů (všechny identifikátory).</param>
    public async Task<IActionResult> OnPostCardsApiTestAsync(string? employeeNo)
    {
        try
        {
            using var source = await IdentifiersApiCardSource.CreateAsync(settings, httpClientFactory, HttpContext.RequestAborted);
            var lines = new List<string> { $"Přihlášení: {source.AuthDescription}" };
            if (string.IsNullOrWhiteSpace(employeeNo))
            {
                lines.Add("");
                lines.Add("=== Všechny identifikátory jedním dotazem bez filtrů (tělo {}) ===");
                var probe = await source.ProbeAllAsync(HttpContext.RequestAborted);
                lines.AddRange(DescribeBulkProbe(probe, source.Rules, await db.Employees
                    .Where(e => e.IsActive && e.PersonalNumber != null).Select(e => e.PersonalNumber!).ToListAsync()));
                lines.AddRange(DescribeProbe(probe, source.Auth, includeParsed: false));
            }
            else
            {
                // Karty a (když jsou zapnuté) SPZ — každý podtyp je samostatný dotaz.
                foreach (var (subType, type) in source.SubTypes)
                {
                    lines.Add("");
                    lines.Add($"=== {(type == IdentifierType.LicensePlate ? "SPZ" : "Karty")} (idIdentifierSubType {subType}) ===");
                    lines.AddRange(DescribeProbe(await source.ProbeAsync(employeeNo.Trim(), subType, HttpContext.RequestAborted), source.Auth));
                }
            }

            if (source.TokenStepDescription is { } tokenStep)
            {
                lines.Add("");
                lines.Add(tokenStep);
            }

            CardsApiProbe = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            CardsApiProbe = $"Zkouška selhala: {ex.GetBaseException().Message}\n\nKdyž jde o spojení: adresa není dostupná z nodů ACS (DNS, firewall), nebo certifikát interní CA (zaškrtněte „Neověřovat certifikát TLS“ a uložte). Když jde o přihlášení tokenem: zkontrolujte adresu přihlašovacího endpointu a tvar těla podle Swaggeru služby.";
        }

        ActiveSection = "cards";
        return RedirectToPage();
    }

    /// <summary>Souhrn dotazu bez filtrů: kolik záznamů, po podtypech, převod čísel podle pravidel a kolik osobních čísel v ACS je.</summary>
    private static List<string> DescribeBulkProbe(IdentifiersApiCardSource.ProbeResult probe,
        IReadOnlyList<IdentifierSubTypeRule> rules, IReadOnlyList<string> knownPersonalNumbers)
    {
        var lines = new List<string>();
        if (!probe.Success || probe.ServiceError is not null)
            return lines;
        if (probe.Parsed.Count == 0)
        {
            lines.Add("Služba odpověděla, ale bez filtrů nevrátila žádný záznam — buď filtry vyžaduje (přepněte režim na „po zaměstnancích“), nebo má jiný tvar odpovědi (tělo níže).");
            return lines;
        }

        var known = knownPersonalNumbers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var persons = probe.Parsed.Where(p => p.EmployeeNo is { Length: > 0 }).Select(p => p.EmployeeNo!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lines.Add($"Přečteno {probe.Parsed.Count} identifikátorů u {persons.Count} osobních čísel; v ACS je {persons.Count(known.Contains)} z nich (zbytek se při synchronizaci nespáruje).");
        foreach (var group in probe.Parsed.GroupBy(p => p.SubType).OrderBy(g => g.Key))
        {
            var rule = rules.FirstOrDefault(r => r.SubType == group.Key);
            var sample = group.Take(3).Select(p => rule is null
                ? p.Value
                : $"{p.Value} → {(rule.Type == IdentifierType.LicensePlate ? IdentifiersApiCardSource.StripPlateCountry(p.Value) : CardNumberFormats.Apply(rule.Format, p.Value))}");
            lines.Add(rule is null
                ? $"  podtyp {group.Key?.ToString() ?? "?"}: {group.Count()} záznamů — BEZ PRAVIDLA, přeskočí se (např. {string.Join(", ", sample)})"
                : $"  podtyp {group.Key}: {group.Count()} záznamů → {(rule.Type == IdentifierType.LicensePlate ? "SPZ" : "karta")}, {CardNumberFormats.Describe(rule.Format)} (např. {string.Join(", ", sample)})");
        }

        return lines;
    }

    private static List<string> DescribeProbe(IdentifiersApiCardSource.ProbeResult probe, string auth, bool includeParsed = true)
    {
        var lines = new List<string>();
        {
            if (!probe.Success)
            {
                lines.Add($"Zkouška selhala: služba odpověděla {probe.StatusCode} {probe.ReasonPhrase}.");
                if (probe.StatusCode == 404)
                    lines.Add("404 může být i „tento zaměstnanec tento druh identifikátoru nemá“ — synchronizace ho tak bere.");
                lines.Add(probe.StatusCode switch
                {
                    404 when probe.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || probe.Body.Trim().StartsWith('{')
                        => "404 s JSON tělem = služba adresu zná, ale tohoto zaměstnance/identifikátor nenašla — porovnejte formát osobního čísla s tím, co služba očekává (úvodní nuly, jiné číslo než AD employeenumber).",
                    404 => "404 bez JSON těla = nejspíš špatná cesta (adresa) nebo metoda; zkontrolujte adresu ve Swaggeru služby (…/swagger) — musí odpovídat přesně včetně /api/v0/Identifiers.",
                    401 when probe.NtlmNotOffered
                        => $"Služba nabízí přihlášení jen schématy {string.Join(", ", probe.OfferedAuthSchemes)} — ACS ověřuje účet domény přes NTLM, které služba nenabízí. Správce služby musí u Windows Authentication povolit poskytovatele NTLM, nebo zvolte jiný způsob přihlášení.",
                    401 when probe.NoChallenge && auth == CardApiAuth.Windows
                        => "Služba odmítla bez výzvy WWW-Authenticate — NTLM handshake tedy neproběhl (není na co odpovědět). Buď na službě není zapnutá Windows Authentication (IIS), nebo služba čeká token: zkuste „Token z přihlášení“ s přihlašovacím endpointem ze Swaggeru služby.",
                    401 when probe.NoChallenge
                        => "Služba odmítla bez výzvy WWW-Authenticate — čeká token v hlavičce Authorization (viz tělo odpovědi). Zvolte „Token z přihlášení“ (endpoint a tvar těla ze Swaggeru služby) nebo „Pevný token“.",
                    401 or 403 => "Služba přihlášení odmítla — zkuste jiný způsob přihlášení (Windows účet domény přes NTLM / API klíč) nebo jiný účet.",
                    405 => "Služba metodu POST na této adrese nepřijímá — ověřte ve Swaggeru, zda není správně GET s parametrem.",
                    415 or 400 => "Služba nepřijala tělo požadavku — porovnejte s příkladem ve Swaggeru (názvy a typy polí).",
                    _ => "Podívejte se na tělo odpovědi níže.",
                });
            }
            else if (probe.ServiceError is { } error)
            {
                lines.Add($"Služba odpověděla {probe.StatusCode}, ale v těle hlásí chybu: {error}. Synchronizace takovou odpověď bere jako chybu (ne jako „zaměstnanec nic nemá“).");
            }
            else if (!includeParsed)
            {
            }
            else if (probe.Parsed.Count == 0)
            {
                lines.Add("Služba odpověděla, ale konektor z odpovědi nepřečetl žádný identifikátor — pošlete tělo odpovědi níže vývoji, rozbor se doplní o skutečné názvy polí.");
            }
            else
            {
                lines.Add($"Přečteno ({probe.Parsed.Count}, každá hodnota jednou): " + string.Join("; ", probe.Parsed.Select(p =>
                    $"{p.Value}{(p.ValidFrom is { } f ? $" od {f:d}" : "")}{(p.ValidTo is { } t ? $" do {t:d}" : "")}{(p.Active is { } a ? (a ? " (aktivní)" : " (neaktivní)") : "")}")));
            }

            lines.Add($"Požadavek: POST {probe.Url}");
            lines.Add(probe.RequestBody);
            lines.Add($"Odpověď: {probe.StatusCode} {probe.ReasonPhrase} · Content-Type: {probe.ContentType ?? "(žádný)"}");
            if (probe.OfferedAuthSchemes.Count > 0)
                lines.Add($"WWW-Authenticate: {string.Join(", ", probe.OfferedAuthSchemes)}");
            lines.Add(string.IsNullOrWhiteSpace(probe.Body) ? "(prázdné tělo)" : probe.Body.Length > 4000 ? probe.Body[..4000] + "…" : probe.Body);
        }

        return lines;
    }

    public async Task<IActionResult> OnPostSmtpAsync(
        string? smtpHost, string? smtpPort, string? smtpUser, string? smtpPassword, string? smtpFrom,
        string? smtpUseTls, string? smtpIgnoreTlsErrors)
    {
        await settings.SetAsync(SettingKeys.SmtpHost, smtpHost, UserName);
        await settings.SetAsync(SettingKeys.SmtpPort, smtpPort, UserName);
        await settings.SetAsync(SettingKeys.SmtpUser, smtpUser, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.SmtpPassword, smtpPassword, UserName);
        await settings.SetAsync(SettingKeys.SmtpFrom, smtpFrom, UserName);
        await settings.SetAsync(SettingKeys.SmtpUseTls, smtpUseTls == "true" ? "true" : "false", UserName);
        await settings.SetAsync(SettingKeys.SmtpIgnoreTlsErrors, smtpIgnoreTlsErrors == "true" ? "true" : "false", UserName);
        return await SavedAsync("SMTP");
    }

    /// <summary>Uloží SMTP nastavení a pošle zkušební e-mail na zadanou adresu (výchozí: adresa odesílatele).</summary>
    public async Task<IActionResult> OnPostSmtpTestAsync(
        string? smtpHost, string? smtpPort, string? smtpUser, string? smtpPassword, string? smtpFrom,
        string? smtpUseTls, string? smtpIgnoreTlsErrors, string? smtpTestTo)
    {
        await OnPostSmtpAsync(smtpHost, smtpPort, smtpUser, smtpPassword, smtpFrom, smtpUseTls, smtpIgnoreTlsErrors);
        var to = string.IsNullOrWhiteSpace(smtpTestTo) ? smtpFrom?.Trim() : smtpTestTo.Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            SmtpTestResult = "Zadejte adresu příjemce zkušebního e-mailu (nebo vyplňte odesílatele).";
        }
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            SmtpTestResult = await email.SendTestAsync(to, cts.Token);
        }

        ActiveSection = "smtp";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostParkingSystemAsync(
        string? parkingSystemEnabled, string? parkingSystemApiKey, string? parkingSystemAllowedIps,
        string? parkingSystemCacheTtlSeconds, string? parkingSystemExitAlwaysAllowed,
        string? parkingSystemConnectorBaseUrl, string? parkingSystemConnectorApiKey, string? parkingSystemPushEnabled)
    {
        await settings.SetAsync(SettingKeys.ParkingSystemEnabled, Flag(parkingSystemEnabled), UserName);
        await settings.SetIfProvidedAsync(SettingKeys.ParkingSystemApiKey, parkingSystemApiKey, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemAllowedIps, parkingSystemAllowedIps, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemCacheTtlSeconds, parkingSystemCacheTtlSeconds, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemExitAlwaysAllowed, Flag(parkingSystemExitAlwaysAllowed), UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemConnectorBaseUrl, parkingSystemConnectorBaseUrl, UserName);
        await settings.SetIfProvidedAsync(SettingKeys.ParkingSystemConnectorApiKey, parkingSystemConnectorApiKey, UserName);
        await settings.SetAsync(SettingKeys.ParkingSystemPushEnabled, Flag(parkingSystemPushEnabled), UserName);
        return await SavedAsync("Parkovací systém");
    }

    public async Task<IActionResult> OnPostParkingSystemTestAsync(
        string? parkingSystemEnabled, string? parkingSystemApiKey, string? parkingSystemAllowedIps,
        string? parkingSystemCacheTtlSeconds, string? parkingSystemExitAlwaysAllowed,
        string? parkingSystemConnectorBaseUrl, string? parkingSystemConnectorApiKey, string? parkingSystemPushEnabled)
    {
        await OnPostParkingSystemAsync(parkingSystemEnabled, parkingSystemApiKey, parkingSystemAllowedIps,
            parkingSystemCacheTtlSeconds, parkingSystemExitAlwaysAllowed, parkingSystemConnectorBaseUrl,
            parkingSystemConnectorApiKey, parkingSystemPushEnabled);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await parkingConnector.IsConfiguredAsync(cts.Token))
            {
                ParkingSystemTestResult = "adresa konektoru není vyplněná — ACS do parkovacího systému nezapisuje "
                                          + "(online autorizace a hlášení událostí přes integrační API fungují i bez konektoru)";
            }
            else
            {
                var caps = await parkingConnector.GetCapabilitiesAsync(cts.Token);
                ParkingSystemTestResult = caps is null
                    ? "konektor neodpověděl"
                    : $"OK — {caps.Connector.Name} {caps.Connector.Version}"
                      + (caps.Connector.TargetSystem is { Length: > 0 } t ? $" ({t})" : "")
                      + $", kontrakt {caps.ContractVersion}, operace: {string.Join(", ", caps.Operations)}"
                      + (caps.SupportsValidity == false ? " — POZOR: systém neumí platnost, expirace řeší ACS" : "");
            }
        }
        catch (Exception ex)
        {
            ParkingSystemTestResult = $"Chyba: {ex.Message}";
        }

        ActiveSection = "parking";
        return RedirectToPage();
    }

    private async Task<IActionResult> SavedAsync(string section)
    {
        await audit.LogAsync(UserName, "settings-updated", "Settings", section);
        SavedSection = section;
        ActiveSection = SectionIds.GetValueOrDefault(section);
        return RedirectToPage();
    }

    /// <summary>Po uložení nebo zkoušce se stránka otevře na téže sekci.</summary>
    [TempData] public string? ActiveSection { get; set; }

    private static readonly Dictionary<string, string> SectionIds = new()
    {
        ["Obecné"] = "general", ["Active Directory"] = "ldap", ["Přihlášení Windows"] = "sso", ["WIN-PAK"] = "winpak", ["Zdroj zaměstnanců"] = "employees",
        ["Automatizace"] = "automation", ["Karty"] = "cards", ["Parkovací systém"] = "parking", ["SMTP"] = "smtp",
    };
}
