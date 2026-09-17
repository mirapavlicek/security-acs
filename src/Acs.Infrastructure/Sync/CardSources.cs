using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Acs.Domain.Entities;
using Acs.Infrastructure.Settings;
using Microsoft.Data.SqlClient;

namespace Acs.Infrastructure.Sync;

/// <summary>Jeden identifikátor ze zdroje (řádek na identifikátor, ne na osobu).</summary>
/// <param name="RawValue">Hodnota přesně ze zdroje, když se <paramref name="Value"/> převáděla pro čtečky.</param>
/// <param name="SubType">Podtyp identifikátoru ve zdroji (idIdentifierSubType), je-li znám.</param>
/// <param name="SkipReason">Záznam se do identifikátorů nepřenáší (např. podtyp bez pravidla) — jen do otisku, s důvodem.</param>
public record CardRecord(
    string? AdAccount,
    string? PersonalNumber,
    string Value,
    IdentifierType Type,
    string? Note = null,
    DateTime? ValidFrom = null,
    DateTime? ValidTo = null,
    string? WinPakCardHolderId = null,
    string? RawValue = null,
    int? SubType = null,
    string? SkipReason = null);

/// <summary>Odkud se berou identifikátory zaměstnanců (karty, SPZ…).</summary>
public interface ICardSource
{
    string Description { get; }

    /// <summary>Načte identifikátory; zdroje, které se ptají po zaměstnancích, dostanou aktuální seznam.</summary>
    IAsyncEnumerable<CardRecord> ReadAsync(IReadOnlyList<Employee> employees, CancellationToken ct);
}

/// <summary>Volba zdroje karet v nastavení.</summary>
public static class CardSources
{
    public const string Mssql = "Mssql";
    public const string Api = "Api";
}

public class CardSourceFactory(SettingsService settings, IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "IdentifiersApi";

    public virtual async Task<ICardSource> CreateAsync(CancellationToken ct = default)
    {
        var source = await settings.GetAsync(SettingKeys.CardsSource, ct) ?? CardSources.Mssql;
        return source switch
        {
            CardSources.Api => await IdentifiersApiCardSource.CreateAsync(settings, httpClientFactory, ct),
            _ => await MssqlCardSource.CreateAsync(settings, ct),
        };
    }
}

/// <summary>
/// Identifikátory z MSSQL dotazu. Očekávané sloupce: <c>AdAccount</c> nebo
/// <c>PersonalNumber</c> (párování), <c>Value</c>/<c>CardNumber</c>/<c>LicensePlate</c>,
/// volitelně <c>Type</c>, <c>WinPakCardHolderId</c>, <c>Note</c>, <c>ValidFrom</c>, <c>ValidTo</c>.
/// </summary>
public sealed class MssqlCardSource(string connectionString, string query) : ICardSource
{
    public string Description => "MSSQL dotaz";

    public static async Task<MssqlCardSource> CreateAsync(SettingsService settings, CancellationToken ct)
    {
        var connectionString = await settings.GetAsync(SettingKeys.CardsMssqlConnectionString, ct)
            ?? throw new InvalidOperationException("Není nastaven MSSQL connection string pro karty (Nastavení → Karty).");
        var query = await settings.GetAsync(SettingKeys.CardsMssqlQuery, ct)
            ?? throw new InvalidOperationException("Není nastaven SQL dotaz pro karty (Nastavení → Karty).");
        return new MssqlCardSource(connectionString, query);
    }

    public async IAsyncEnumerable<CardRecord> ReadAsync(IReadOnlyList<Employee> employees, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 300 };
        await using var reader = await command.ExecuteReaderAsync(ct);

        var columns = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(i => reader.GetName(i), i => i, StringComparer.OrdinalIgnoreCase);
        string? Get(string name)
            => columns.TryGetValue(name, out var i) && !reader.IsDBNull(i)
                ? reader.GetValue(i).ToString()?.Trim()
                : null;

        while (await reader.ReadAsync(ct))
        {
            var rawValue = Get("Value") ?? Get("CardNumber") ?? Get("LicensePlate");
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            yield return new CardRecord(
                Get("AdAccount"),
                Get("PersonalNumber"),
                rawValue,
                CardSyncService.ParseType(Get("Type"), columns.ContainsKey("LicensePlate") && Get("CardNumber") is null),
                Get("Note"),
                ParseDate(Get("ValidFrom")),
                ParseDate(Get("ValidTo")),
                Get("WinPakCardHolderId"));
        }
    }

    private static DateTime? ParseDate(string? raw)
        => DateTime.TryParse(raw, out var value) ? value : null;
}

/// <summary>Způsob přihlášení k integračnímu API karet.</summary>
public static class CardApiAuth
{
    public const string None = "None";
    public const string ApiKey = "ApiKey";
    public const string Basic = "Basic";
    /// <summary>NTLM účtem domény (ověření proti AD) — výchozí je servisní účet, kterým ACS čte AD.</summary>
    public const string Windows = "Windows";
    /// <summary>Pevný token v hlavičce <c>Authorization: Bearer</c>.</summary>
    public const string Bearer = "Bearer";
    /// <summary>Token získaný přihlášením na endpoint služby (uživatel a heslo, tělo podle šablony).</summary>
    public const string Token = "Token";
}

/// <summary>Identifikátor tak, jak ho vrátilo integrační API, po rozboru odpovědi.</summary>
/// <param name="SourceFormat">Tvar hodnoty podle služby (<c>initialFormat</c>: NATIVE / NOHYPHEN), když ho posílá.</param>
public record ApiIdentifier(string Value, DateTime? ValidFrom, DateTime? ValidTo, bool? Active, string? Note,
    string? EmployeeNo = null, int? SubType = null, string? SourceFormat = null);

/// <summary>Jak se integrační služba čte.</summary>
public static class CardApiFetchMode
{
    /// <summary>Jeden dotaz bez filtrů — všechny identifikátory všech osob; párování se dělá nad databází ACS (výchozí).</summary>
    public const string All = "All";
    /// <summary>Dotaz po zaměstnanci a podtypu (původní režim; tisíce volání).</summary>
    public const string PerEmployee = "PerEmployee";
}

/// <summary>
/// Identifikátory z integrační služby (<c>POST …/api/v0/Identifiers</c> s tělem
/// <c>{ employeeNo, idIdentifierSubType }</c>; podtyp 3 = identifikační karta, 4 = SPZ).
/// Služba se ptá po zaměstnanci — jedno volání na osobní číslo a podtyp, několik souběžně.
///
/// Skutečná odpověď služby je obálka <c>{ output: [ { employeeNo, initialCode, idIdentifierSubType } ], conclusion, resultType }</c>
/// (při chybě <c>conclusion: false</c> a <c>errorDescription</c>); hodnota identifikátoru je <c>initialCode</c>.
/// Rozbor zůstává tolerantní i k jiným tvarům (pole nebo objekt s polem, hodnota pod různými
/// názvy) a v Nastavení je zkouška, která ukáže surovou odpověď i to, co z ní konektor přečetl.
/// Služba tentýž identifikátor vrací opakovaně — rozbor vrací každou hodnotu jednou (první výskyt);
/// u SPZ se odstraní přípona země (<c>1TN7287-CZE</c> → <c>1TN7287</c>), aby seděla na čtení kamer.
/// </summary>
public sealed class IdentifiersApiCardSource(HttpClient http, IdentifiersApiCardSource.Options options, bool ownsHttpClient = false)
    : ICardSource, IDisposable
{
    /// <summary>
    /// Klient s vlastním handlerem (Windows účet, vypnuté ověření TLS) vzniká pro každou synchronizaci
    /// znovu — bez uvolnění by po každém běhu zůstal handler se svými spojeními až do finalizace.
    /// Klient z <see cref="IHttpClientFactory"/> se neuvolňuje (spravuje ho továrna).
    /// </summary>
    public void Dispose()
    {
        if (ownsHttpClient)
            http.Dispose();
    }

    /// <param name="SubType">Podtyp identifikační karty (3).</param>
    /// <param name="PlateSubType">Podtyp SPZ (4); null = SPZ se z API nestahují.</param>
    /// <param name="Rules">Pravidla po podtypech (typ + převod čísla pro čtečky); null = z <paramref name="SubType"/> a <paramref name="PlateSubType"/> beze změny hodnot.</param>
    /// <param name="FetchAll">Jeden dotaz bez filtrů na všechny identifikátory (místo dotazu po zaměstnanci).</param>
    public sealed record Options(
        string Url, int SubType, string? ApiKeyHeader, string? ApiKey, string? User, string? Password, string Auth = CardApiAuth.None,
        string? BearerToken = null, string? TokenUrl = null, string? TokenBody = null, string? TokenField = null,
        int? PlateSubType = null, IReadOnlyList<IdentifierSubTypeRule>? Rules = null, bool FetchAll = false);

    public const int DefaultCardSubType = 3;
    public const int DefaultPlateSubType = 4;

    public const string DefaultTokenBody = "{\"username\":\"{user}\",\"password\":\"{password}\"}";

    private string? _token;
    private DateTime _tokenObtainedUtc;

    /// <summary>Popis posledního získání tokenu pro zkoušku (stav, tělo) — bez tokenu samotného.</summary>
    public string? TokenStepDescription { get; private set; }

    /// <summary>Jak se ACS ke službě hlásí (režim, účet, doména) — pro zkoušku v Nastavení, bez hesla.</summary>
    public string AuthDescription { get; private set; } = DescribeAuth(options, null);

    private const int Parallelism = 4;

    public string Description => options.FetchAll
        ? $"integrační API {options.Url} (vše jedním dotazem; podtypy {string.Join(", ", Rules.Select(r => r.SubType))})"
        : $"integrační API {options.Url} (podtyp {options.SubType}"
          + (options.PlateSubType is { } plate ? $", SPZ podtyp {plate})" : ")");

    /// <summary>Pravidla po podtypech: co je karta, co SPZ a jak se číslo převádí pro čtečky.</summary>
    public IReadOnlyList<IdentifierSubTypeRule> Rules => options.Rules ?? (options.PlateSubType is { } plate
        ? [new IdentifierSubTypeRule(options.SubType, IdentifierType.Card, CardNumberFormat.Raw), new IdentifierSubTypeRule(plate, IdentifierType.LicensePlate, CardNumberFormat.Raw)]
        : [new IdentifierSubTypeRule(options.SubType, IdentifierType.Card, CardNumberFormat.Raw)]);

    /// <summary>Podtypy, na které se služba ptá, a typ identifikátoru, který z nich vznikne.</summary>
    public IReadOnlyList<(int SubType, IdentifierType Type)> SubTypes => Rules.Select(r => (r.SubType, r.Type)).ToList();

    public bool FetchAll => options.FetchAll;

    /// <summary>Kolik záznamů mělo podtyp, pro který není pravidlo (přeskočeno) — po posledním čtení.</summary>
    public int SkippedUnknownSubType { get; private set; }

    /// <summary>Podtyp karet z nastavení: prázdné, nečíselné nebo nekladné (např. omylem uložená 0) = výchozí 3.</summary>
    public static int ParseCardSubType(string? raw)
        => int.TryParse(raw?.Trim(), out var value) && value > 0 ? value : DefaultCardSubType;

    /// <summary>Podtyp SPZ z nastavení: prázdné = výchozí 4, <c>0</c> (nebo záporné) = nestahovat.</summary>
    public static int? ParsePlateSubType(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? DefaultPlateSubType
            : int.TryParse(raw.Trim(), out var value) && value > 0 ? value : null;

    /// <summary>Zvolený způsob přihlášení (<see cref="CardApiAuth"/>) — pro rady ve zkoušce.</summary>
    public string Auth => options.Auth;

    public static async Task<IdentifiersApiCardSource> CreateAsync(SettingsService settings, IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.CardsApiUrl, ct)
            ?? throw new InvalidOperationException("Není nastavena adresa integračního API pro karty (Nastavení → Karty).");
        var subType = ParseCardSubType(await settings.GetAsync(SettingKeys.CardsApiSubType, ct));
        var plateSubType = ParsePlateSubType(await settings.GetAsync(SettingKeys.CardsApiPlateSubType, ct));
        var auth = await settings.GetAsync(SettingKeys.CardsApiAuth, ct) ?? CardApiAuth.None;
        var user = await settings.GetAsync(SettingKeys.CardsApiUser, ct);
        var password = await settings.GetAsync(SettingKeys.CardsApiPassword, ct);
        if (auth is CardApiAuth.Windows or CardApiAuth.Token && string.IsNullOrWhiteSpace(user))
        {
            // Stejný servisní účet, kterým ACS čte AD — jedny přihlašovací údaje pro obě služby domény.
            user = await settings.GetAsync(SettingKeys.LdapBindUser, ct);
            password = await settings.GetAsync(SettingKeys.LdapBindPassword, ct);
        }

        var rules = CardNumberFormats.ParseRules(await settings.GetAsync(SettingKeys.CardsApiSubTypeRules, ct), out _);
        if (rules.Count == 0)
            rules = CardNumberFormats.Default;
        var fetchAll = (await settings.GetAsync(SettingKeys.CardsApiFetchMode, ct) ?? CardApiFetchMode.All) != CardApiFetchMode.PerEmployee;

        var options = new Options(url, subType,
            await settings.GetAsync(SettingKeys.CardsApiKeyHeader, ct),
            await settings.GetAsync(SettingKeys.CardsApiKey, ct),
            user, password, auth,
            await settings.GetAsync(SettingKeys.CardsApiBearerToken, ct),
            await settings.GetAsync(SettingKeys.CardsApiTokenUrl, ct),
            await settings.GetAsync(SettingKeys.CardsApiTokenBody, ct),
            await settings.GetAsync(SettingKeys.CardsApiTokenField, ct),
            plateSubType, rules, fetchAll);

        // Interní služba často běží s certifikátem vlastní CA, kterou nody neznají.
        var ignoreTls = await settings.GetAsync(SettingKeys.CardsApiIgnoreTls, ct) == "true";
        if (auth == CardApiAuth.Windows || ignoreTls)
        {
            // Windows účet vyžaduje vlastní handler s přihlašovacími údaji (viz NtlmCredentials).
            var handler = new HttpClientHandler();
            if (ignoreTls)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            System.Net.NetworkCredential? credential = null;
            if (auth == CardApiAuth.Windows)
            {
                if (string.IsNullOrWhiteSpace(user))
                    throw new InvalidOperationException("Přihlášení Windows účtem: není zadaný účet ani servisní účet pro AD (Nastavení → Active Directory).");
                credential = WindowsCredential(user, password ?? "", await settings.GetLdapDomainAsync(ct));
                handler.Credentials = NtlmCredentials(url, credential);
            }

            return new IdentifiersApiCardSource(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) }, options, ownsHttpClient: true)
            {
                AuthDescription = DescribeAuth(options, credential),
            };
        }

        return new IdentifiersApiCardSource(httpClientFactory.CreateClient(CardSourceFactory.HttpClientName), options);
    }

    private static string DescribeAuth(Options options, System.Net.NetworkCredential? credential) => options.Auth switch
    {
        CardApiAuth.Windows when credential is not null => $"Windows účet domény přes NTLM — uživatel „{credential.UserName}“, doména „{(string.IsNullOrEmpty(credential.Domain) ? "(žádná — UPN)" : credential.Domain)}“, pracovní stanice „{Environment.MachineName}“.",
        CardApiAuth.Windows => $"Windows účet domény přes NTLM — účet „{options.User}“.",
        CardApiAuth.Token => $"Token z přihlášení na {options.TokenUrl ?? "(endpoint nenastaven)"} účtem „{options.User}“.",
        CardApiAuth.Bearer => "Pevný token (Authorization: Bearer).",
        CardApiAuth.ApiKey => $"API klíč v hlavičce {(string.IsNullOrWhiteSpace(options.ApiKeyHeader) ? "X-Api-Key" : options.ApiKeyHeader)}.",
        CardApiAuth.Basic => $"Basic účtem „{options.User}“.",
        _ => "bez přihlášení.",
    };

    /// <summary>Schéma HTTP autentizace, kterým se účet domény ověřuje proti AD.</summary>
    public const string NtlmScheme = "NTLM";

    /// <summary>
    /// Přihlašovací údaje zaregistrované jen pro schéma <c>NTLM</c> (pro celý server služby).
    /// Služba s integrovaným přihlášením Windows nabízí zpravidla <c>Negotiate</c> i <c>NTLM</c>
    /// a .NET by dal přednost Negotiate (SPNEGO → Kerberos), což na linuxových nodech bez
    /// Kerberos ticketu a konfigurace krb5 skončí 401. Registrací pouze pod NTLM se výzva
    /// Negotiate ignoruje a účet se ověří proti AD přes NTLM — spravovanou implementací .NET
    /// (<c>System.Net.Security.UseManagedNtlm</c> v Acs.Web.csproj), tedy bez balíku gssntlmssp.
    /// </summary>
    public static System.Net.CredentialCache NtlmCredentials(string url, System.Net.NetworkCredential credential)
    {
        var cache = new System.Net.CredentialCache();
        cache.Add(new Uri(new Uri(url).GetLeftPart(UriPartial.Authority)), NtlmScheme, credential);
        return cache;
    }

    /// <summary>
    /// Účet pro NTLM z toho, jak je zapsaný v nastavení: <c>DOMÉNA\uživatel</c>,
    /// UPN <c>uživatel@doména</c> (předá se celý), nebo prosté jméno + doména z nastavení AD.
    /// </summary>
    public static System.Net.NetworkCredential WindowsCredential(string account, string password, string? defaultDomain)
    {
        account = account.Trim();
        var backslash = account.IndexOf('\\');
        if (backslash > 0)
            return new System.Net.NetworkCredential(account[(backslash + 1)..], password, account[..backslash]);
        if (account.Contains('@') || string.IsNullOrWhiteSpace(defaultDomain))
            return new System.Net.NetworkCredential(account, password);
        return new System.Net.NetworkCredential(account, password, defaultDomain.Trim());
    }

    public async IAsyncEnumerable<CardRecord> ReadAsync(IReadOnlyList<Employee> employees, [EnumeratorCancellation] CancellationToken ct)
    {
        SkippedUnknownSubType = 0;
        if (options.FetchAll)
        {
            // Jeden dotaz bez filtrů: služba vrátí identifikátory všech osob; párování na
            // zaměstnance dělá CardSyncService nad databází ACS podle osobního čísla.
            var body = await PostAsync(null, null, ct);
            if (ServiceError(body) is { } error)
                throw new InvalidOperationException($"Integrační API ohlásilo chybu: {error}");

            foreach (var identifier in ParseAll(body))
            {
                if (identifier.Active == false)
                    continue;
                if (identifier.SubType is null || Rules.FirstOrDefault(r => r.SubType == identifier.SubType) is not { } rule)
                {
                    // Bez pravidla se nepřenáší, ale v otisku zůstane vidět, co služba vrací navíc.
                    SkippedUnknownSubType++;
                    yield return new CardRecord(null, identifier.EmployeeNo, identifier.Value.Trim(), IdentifierType.Other,
                        RawValue: identifier.Value.Trim(), SubType: identifier.SubType,
                        SkipReason: identifier.SubType is null ? "bez podtypu" : $"podtyp {identifier.SubType} bez pravidla");
                    continue;
                }

                yield return ToRecord(identifier, rule);
            }

            yield break;
        }

        var numbers = employees
            .Where(e => !string.IsNullOrWhiteSpace(e.PersonalNumber))
            .Select(e => e.PersonalNumber!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Jeden dotaz na osobní číslo a podtyp (karty, případně SPZ).
        var queries = numbers.SelectMany(_ => Rules, (number, rule) => (number, rule));
        foreach (var batch in queries.Chunk(Parallelism))
        {
            var results = await Task.WhenAll(batch.Select(async q => (q.number, q.rule, identifiers: await QueryAsync(q.number, q.rule.SubType, q.rule.Type, ct))));
            foreach (var (number, rule, identifiers) in results)
            {
                foreach (var identifier in identifiers)
                {
                    if (identifier.Active == false)
                        continue;
                    yield return ToRecord(identifier with { EmployeeNo = number, SubType = rule.SubType }, rule);
                }
            }
        }
    }

    /// <summary>Záznam pro synchronizaci: číslo převedené pro čtečky podle pravidla podtypu, původní hodnota v poznámce.</summary>
    private static CardRecord ToRecord(ApiIdentifier identifier, IdentifierSubTypeRule rule)
    {
        var value = rule.Type == IdentifierType.LicensePlate
            ? StripPlateCountry(identifier.Value.Trim())
            : CardNumberFormats.Apply(rule.Format, identifier.Value, identifier.SourceFormat);
        var note = rule.Format == CardNumberFormat.Raw || value == identifier.Value.Trim()
            ? identifier.Note
            : $"podtyp {rule.SubType}: {identifier.Value.Trim()}";
        return new CardRecord(null, identifier.EmployeeNo, value, rule.Type,
            note, identifier.ValidFrom, identifier.ValidTo, RawValue: identifier.Value.Trim(), SubType: rule.SubType);
    }

    /// <summary>Výsledek zkoušky z Nastavení — všechno, co je třeba k rozlišení „špatná adresa“, „jiný formát čísla“ a „jiné schéma odpovědi“.</summary>
    public sealed record ProbeResult(
        string Url,
        int SubType,
        string RequestBody,
        int StatusCode,
        string? ReasonPhrase,
        string? ContentType,
        string Body,
        IReadOnlyList<ApiIdentifier> Parsed,
        IReadOnlyList<string> OfferedAuthSchemes,
        string? ServiceError = null)
    {
        public bool Success => StatusCode is >= 200 and < 300;

        /// <summary>Služba při 401 nabídla jen jiná schémata než NTLM (typicky samotné Negotiate) — přihlášení Windows účtem přes NTLM nemá jak proběhnout.</summary>
        public bool NtlmNotOffered => StatusCode == 401 && OfferedAuthSchemes.Count > 0
            && !OfferedAuthSchemes.Contains(NtlmScheme, StringComparer.OrdinalIgnoreCase);

        /// <summary>Služba odmítla (401) bez jakékoli výzvy <c>WWW-Authenticate</c> — Windows účet nemá na co odpovědět; služba zřejmě čeká token.</summary>
        public bool NoChallenge => StatusCode == 401 && OfferedAuthSchemes.Count == 0;
    }

    /// <summary>Zkouška z Nastavení: odeslaný požadavek, odpověď se stavem a hlavičkami a co z ní konektor přečte.</summary>
    /// <param name="subType">Podtyp identifikátoru; null = podtyp karet z nastavení.</param>
    public async Task<ProbeResult> ProbeAsync(string employeeNo, int? subType = null, CancellationToken ct = default)
    {
        var effectiveSubType = subType ?? options.SubType;
        var type = Rules.FirstOrDefault(r => r.SubType == effectiveSubType)?.Type
            ?? (effectiveSubType == options.PlateSubType ? IdentifierType.LicensePlate : IdentifierType.Card);
        var requestBody = JsonSerializer.Serialize(new { employeeNo, idIdentifierSubType = effectiveSubType });
        using var request = await BuildRequestAsync(employeeNo, effectiveSubType, ct);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = response.IsSuccessStatusCode ? SafeParse(body, type) : [];
        return new ProbeResult(options.Url, effectiveSubType, requestBody, (int)response.StatusCode, response.ReasonPhrase,
            response.Content.Headers.ContentType?.ToString(), body, parsed,
            response.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList(),
            response.IsSuccessStatusCode ? SafeServiceError(body) : null);
    }

    /// <summary>Zkouška dotazu bez filtrů (všechny identifikátory) — surová odpověď a rozbor včetně osobních čísel a podtypů.</summary>
    public async Task<ProbeResult> ProbeAllAsync(CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(null, null, ct);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        IReadOnlyList<ApiIdentifier> parsed = [];
        if (response.IsSuccessStatusCode)
        {
            try { parsed = ParseAll(body); }
            catch (JsonException) { parsed = []; }
        }

        return new ProbeResult(options.Url, 0, "{}", (int)response.StatusCode, response.ReasonPhrase,
            response.Content.Headers.ContentType?.ToString(), body, parsed,
            response.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList(),
            response.IsSuccessStatusCode ? SafeServiceError(body) : null);
    }

    private static IReadOnlyList<ApiIdentifier> SafeParse(string body, IdentifierType type)
    {
        try
        {
            return Parse(body, type);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? SafeServiceError(string body)
    {
        try
        {
            return ServiceError(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ApiIdentifier>> QueryAsync(string employeeNo, int subType, IdentifierType type, CancellationToken ct)
    {
        try
        {
            var body = await PostAsync(employeeNo, subType, ct);
            // Chyba v obálce s 200 OK nesmí projít jako „zaměstnanec nic nemá“ — synchronizace by mu karty zrušila.
            if (ServiceError(body) is { } error)
                throw new InvalidOperationException($"Integrační API ohlásilo chybu pro zaměstnance {employeeNo} (podtyp {subType}): {error}");
            return Parse(body, type);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Zaměstnanec bez identifikátoru — služba odpoví 404; není to chyba synchronizace.
            return [];
        }
    }

    /// <param name="employeeNo">Osobní číslo; null spolu s null podtypem = dotaz bez filtrů (tělo <c>{}</c>).</param>
    private async Task<HttpRequestMessage> BuildRequestAsync(string? employeeNo, int? subType, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, options.Url)
        {
            Content = employeeNo is null && subType is null
                ? new StringContent("{}", Encoding.UTF8, "application/json")
                : JsonContent.Create(new { employeeNo, idIdentifierSubType = subType }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        if (options.Auth == CardApiAuth.Bearer && !string.IsNullOrWhiteSpace(options.BearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken.Trim());
        if (options.Auth == CardApiAuth.Token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        if (options.Auth == CardApiAuth.ApiKey && !string.IsNullOrWhiteSpace(options.ApiKey))
            request.Headers.TryAddWithoutValidation(string.IsNullOrWhiteSpace(options.ApiKeyHeader) ? "X-Api-Key" : options.ApiKeyHeader, options.ApiKey);
        if (options.Auth == CardApiAuth.Basic && !string.IsNullOrWhiteSpace(options.User))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.User}:{options.Password}")));
        // Windows účet (NTLM) vyřizuje handler klienta podle Credentials — viz NtlmCredentials.
        return request;
    }

    /// <summary>
    /// Přihlášení na endpoint služby a token z odpovědi. Tělo podle šablony
    /// ({user}, {password}); token se hledá pod obvyklými názvy i o úroveň hlouběji
    /// (data/result), nebo je odpověď rovnou řetězec. Drží se 50 minut na instanci.
    /// </summary>
    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow - _tokenObtainedUtc < TimeSpan.FromMinutes(50))
            return _token;
        if (string.IsNullOrWhiteSpace(options.TokenUrl))
            throw new InvalidOperationException("Přihlášení tokenem: není nastavená adresa přihlašovacího endpointu (Nastavení → Karty).");
        if (string.IsNullOrWhiteSpace(options.User))
            throw new InvalidOperationException("Přihlášení tokenem: není zadaný uživatel ani servisní účet pro AD.");

        var body = (string.IsNullOrWhiteSpace(options.TokenBody) ? DefaultTokenBody : options.TokenBody)
            .Replace("{user}", JsonEncodedText.Encode(options.User).ToString())
            .Replace("{password}", JsonEncodedText.Encode(options.Password ?? "").ToString());
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var shown = text.Length > 600 ? text[..600] + "…" : text;
        if (!response.IsSuccessStatusCode)
        {
            TokenStepDescription = $"Přihlášení {options.TokenUrl}: {(int)response.StatusCode} {response.ReasonPhrase}\n{shown}";
            throw new HttpRequestException($"Přihlášení na {options.TokenUrl} odpovědělo {(int)response.StatusCode}: {Truncate(text, 300)}", null, response.StatusCode);
        }

        var token = ExtractToken(text, options.TokenField);
        if (string.IsNullOrWhiteSpace(token))
        {
            TokenStepDescription = $"Přihlášení {options.TokenUrl}: {(int)response.StatusCode}, ale v odpovědi není token (zadejte název pole)\n{shown}";
            throw new InvalidOperationException($"Přihlášení na {options.TokenUrl} proběhlo, ale v odpovědi není token — zadejte název pole s tokenem. Odpověď: {Truncate(text, 300)}");
        }

        TokenStepDescription = $"Přihlášení {options.TokenUrl}: {(int)response.StatusCode}, token získán ({token.Length} znaků).";
        _token = token;
        _tokenObtainedUtc = DateTime.UtcNow;
        return token;
    }

    private static readonly string[] TokenProperties = ["token", "accessToken", "access_token", "jwt", "id_token", "idToken", "bearer", "authToken"];
    private static readonly string[] TokenContainers = ["data", "result", "response", "payload"];

    public static string? ExtractToken(string body, string? field)
    {
        body = body.Trim();
        if (body.Length == 0)
            return null;
        if (!body.StartsWith('{') && !body.StartsWith('['))
            return body.Trim('"');

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString();
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var names = string.IsNullOrWhiteSpace(field) ? TokenProperties : [field.Trim()];
        foreach (var name in names)
        {
            if (TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        foreach (var container in TokenContainers)
        {
            if (!TryGet(root, container, out var inner) || inner.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var name in names)
            {
                if (TryGet(inner, name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }

        return null;
    }

    private async Task<string> PostAsync(string? employeeNo, int? subType, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(employeeNo, subType, ct);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var scope = employeeNo is null ? "bez filtrů (všechny identifikátory)" : $"pro zaměstnance {employeeNo} (podtyp {subType})";
            throw new HttpRequestException(
                $"Integrační API odpovědělo {(int)response.StatusCode} {scope}: {Truncate(body, 300)}",
                null, response.StatusCode);
        }

        return body;
    }

    private static readonly string[] EmployeeNoProperties = ["employeeNo", "employeeNumber", "personalNumber", "personalNo", "osobniCislo"];
    private static readonly string[] SubTypeProperties = ["idIdentifierSubType", "identifierSubType", "subType", "subTypeId"];
    private static readonly string[] SourceFormatProperties = ["initialFormat", "format"];

    /// <summary>
    /// Rozbor odpovědi na dotaz bez filtrů: záznamy všech osob s osobním číslem a podtypem,
    /// hodnota beze změny (převod pro čtečky dělá pravidlo podtypu). Tentýž identifikátor
    /// téže osoby a podtypu jednou.
    /// </summary>
    public static IReadOnlyList<ApiIdentifier> ParseAll(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && IsFailedEnvelope(root))
            return [];

        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => FindList(root) is { } list ? list.EnumerateArray().ToList() : [root],
            _ => [root],
        };

        var result = new List<ApiIdentifier>();
        var seen = new HashSet<(string, int?, string)>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var value = First(item, ValueProperties)?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            var employeeNo = First(item, EmployeeNoProperties)?.Trim();
            var subType = int.TryParse(First(item, SubTypeProperties), out var parsedSubType) ? parsedSubType : (int?)null;
            if (!seen.Add((employeeNo ?? "", subType, EmployeeIdentifier.Normalize(value))))
                continue;
            result.Add(new ApiIdentifier(value,
                ParseDate(First(item, FromProperties)),
                ParseDate(First(item, ToProperties)),
                ParseActive(item),
                First(item, NoteProperties),
                employeeNo, subType, First(item, SourceFormatProperties)));
        }

        return result;
    }

    private static readonly string[] ListProperties = ["output", "identifiers", "items", "data", "result", "results", "value", "cards", "records"];
    private static readonly string[] ValueProperties = ["initialCode", "identifier", "identifierNo", "identifierNumber", "identifierValue", "cardNumber", "cardNo", "cardId", "number", "code", "value", "serialNumber", "serial", "chipNumber"];
    private static readonly string[] FromProperties = ["validFrom", "dateFrom", "from", "validityFrom", "startDate", "activeFrom"];
    private static readonly string[] ToProperties = ["validTo", "dateTo", "to", "validityTo", "endDate", "expiration", "expires", "activeTo"];
    private static readonly string[] ActiveProperties = ["active", "isActive", "enabled", "valid", "isValid"];
    private static readonly string[] StateProperties = ["state", "status", "stav"];
    private static readonly string[] NoteProperties = ["note", "description", "name", "type", "subTypeName", "identifierSubType"];

    /// <summary>
    /// Rozbor odpovědi — viz popis třídy. Každá hodnota jednou (služba je opakuje), u SPZ bez přípony země.
    /// Chybová obálka (<c>conclusion: false</c>) dá prázdný seznam; chybu vrací <see cref="ServiceError"/>.
    /// </summary>
    public static IReadOnlyList<ApiIdentifier> Parse(string json, IdentifierType type = IdentifierType.Card)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && IsFailedEnvelope(root))
            return [];

        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => FindList(root) is { } list ? list.EnumerateArray().ToList() : [root],
            _ => [root],
        };

        var result = new List<ApiIdentifier>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string raw, DateTime? from, DateTime? to, bool? active, string? note, string? sourceFormat = null)
        {
            var value = type == IdentifierType.LicensePlate ? StripPlateCountry(raw.Trim()) : raw.Trim();
            if (value.Length == 0 || !seen.Add(EmployeeIdentifier.Normalize(value)))
                return;
            result.Add(new ApiIdentifier(value, from, to, active, note, SourceFormat: sourceFormat));
        }

        foreach (var item in items)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String when !string.IsNullOrWhiteSpace(item.GetString()):
                    Add(item.GetString()!, null, null, null, null);
                    break;
                case JsonValueKind.Number:
                    Add(item.GetRawText(), null, null, null, null);
                    break;
                case JsonValueKind.Object:
                    var value = First(item, ValueProperties);
                    if (string.IsNullOrWhiteSpace(value))
                        break;
                    Add(value,
                        ParseDate(First(item, FromProperties)),
                        ParseDate(First(item, ToProperties)),
                        ParseActive(item),
                        First(item, NoteProperties),
                        First(item, SourceFormatProperties));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// SPZ bez přípony země za pomlčkou (<c>1TN7287-CZE</c> → <c>1TN7287</c>, <c>EL285CJ-D</c> → <c>EL285CJ</c>):
    /// kamery čtou jen značku a porovnání u brány jde přes normalizovanou hodnotu.
    /// </summary>
    public static string StripPlateCountry(string plate)
    {
        var dash = plate.LastIndexOf('-');
        if (dash <= 0 || dash == plate.Length - 1)
            return plate;
        var suffix = plate[(dash + 1)..];
        return suffix.Length <= 3 && suffix.All(char.IsLetter) ? plate[..dash].TrimEnd() : plate;
    }

    /// <summary>Chyba z obálky služby (<c>conclusion: false</c> + <c>errorDescription</c>), nebo null, když odpověď chybu nehlásí.</summary>
    public static string? ServiceError(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !IsFailedEnvelope(root))
            return null;

        if (TryGet(root, "errorDescription", out var description) && description.ValueKind == JsonValueKind.Object)
        {
            var message = First(description, ["errorMessage", "message"]);
            var errorType = First(description, ["errorType", "type"]);
            if (message is not null)
                return errorType is null ? message : $"{errorType}: {message}";
        }

        return First(root, ["errorMessage", "message", "resultType"]) ?? "conclusion: false";
    }

    private static bool IsFailedEnvelope(JsonElement root)
        => TryGet(root, "conclusion", out var conclusion) && conclusion.ValueKind == JsonValueKind.False;

    private static JsonElement? FindList(JsonElement root)
    {
        foreach (var name in ListProperties)
        {
            if (TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.Array)
                return element;
        }

        // Jediné pole v objektu, ať se jmenuje jakkoli.
        var arrays = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
        return arrays.Count == 1 ? arrays[0].Value : null;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? First(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value))
                continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static bool? ParseActive(JsonElement item)
    {
        foreach (var name in ActiveProperties)
        {
            if (TryGet(item, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }

        var state = First(item, StateProperties)?.ToLowerInvariant();
        if (state is null)
            return null;
        if (state.Contains("aktiv") || state.Contains("active") || state.Contains("platn") || state.Contains("valid"))
            return !state.Contains("neaktiv") && !state.Contains("inactive") && !state.Contains("neplatn") && !state.Contains("invalid");
        if (state.Contains("blok") || state.Contains("disab") || state.Contains("zru") || state.Contains("expir"))
            return false;
        return null;
    }

    private static DateTime? ParseDate(string? raw)
        => DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : DateTime.TryParse(raw, out var local) ? local : null;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
