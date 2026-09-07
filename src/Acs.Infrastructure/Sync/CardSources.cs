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
public record CardRecord(
    string? AdAccount,
    string? PersonalNumber,
    string Value,
    IdentifierType Type,
    string? Note = null,
    DateTime? ValidFrom = null,
    DateTime? ValidTo = null,
    string? WinPakCardHolderId = null);

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
    /// <summary>NTLM/Negotiate účtem domény — výchozí je servisní účet, kterým ACS čte AD.</summary>
    public const string Windows = "Windows";
    /// <summary>Pevný token v hlavičce <c>Authorization: Bearer</c>.</summary>
    public const string Bearer = "Bearer";
    /// <summary>Token získaný přihlášením na endpoint služby (uživatel a heslo, tělo podle šablony).</summary>
    public const string Token = "Token";
}

/// <summary>Identifikátor tak, jak ho vrátilo integrační API, po rozboru odpovědi.</summary>
public record ApiIdentifier(string Value, DateTime? ValidFrom, DateTime? ValidTo, bool? Active, string? Note);

/// <summary>
/// Identifikační karty z integrační služby (<c>POST …/api/v0/Identifiers</c> s tělem
/// <c>{ employeeNo, idIdentifierSubType }</c>; podtyp 3 = identifikační karta). Služba se
/// ptá po zaměstnanci — jedno volání na osobní číslo, několik souběžně.
///
/// Podobu odpovědi dokumentace neuvádí; rozbor je proto tolerantní (pole nebo objekt
/// s polem, hodnota pod různými názvy) a v Nastavení je zkouška, která ukáže surovou
/// odpověď i to, co z ní konektor přečetl.
/// </summary>
public sealed class IdentifiersApiCardSource(HttpClient http, IdentifiersApiCardSource.Options options) : ICardSource
{
    public sealed record Options(
        string Url, int SubType, string? ApiKeyHeader, string? ApiKey, string? User, string? Password, string Auth = CardApiAuth.None,
        string? BearerToken = null, string? TokenUrl = null, string? TokenBody = null, string? TokenField = null);

    public const string DefaultTokenBody = "{\"username\":\"{user}\",\"password\":\"{password}\"}";

    private string? _token;
    private DateTime _tokenObtainedUtc;

    /// <summary>Popis posledního získání tokenu pro zkoušku (stav, tělo) — bez tokenu samotného.</summary>
    public string? TokenStepDescription { get; private set; }

    private const int Parallelism = 4;

    public string Description => $"integrační API {options.Url} (podtyp {options.SubType})";

    public static async Task<IdentifiersApiCardSource> CreateAsync(SettingsService settings, IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.CardsApiUrl, ct)
            ?? throw new InvalidOperationException("Není nastavena adresa integračního API pro karty (Nastavení → Karty).");
        var subType = int.TryParse(await settings.GetAsync(SettingKeys.CardsApiSubType, ct), out var parsed) ? parsed : 3;
        var auth = await settings.GetAsync(SettingKeys.CardsApiAuth, ct) ?? CardApiAuth.None;
        var user = await settings.GetAsync(SettingKeys.CardsApiUser, ct);
        var password = await settings.GetAsync(SettingKeys.CardsApiPassword, ct);
        if (auth is CardApiAuth.Windows or CardApiAuth.Token && string.IsNullOrWhiteSpace(user))
        {
            // Stejný servisní účet, kterým ACS čte AD — jedny přihlašovací údaje pro obě služby domény.
            user = await settings.GetAsync(SettingKeys.LdapBindUser, ct);
            password = await settings.GetAsync(SettingKeys.LdapBindPassword, ct);
        }

        var options = new Options(url, subType,
            await settings.GetAsync(SettingKeys.CardsApiKeyHeader, ct),
            await settings.GetAsync(SettingKeys.CardsApiKey, ct),
            user, password, auth,
            await settings.GetAsync(SettingKeys.CardsApiBearerToken, ct),
            await settings.GetAsync(SettingKeys.CardsApiTokenUrl, ct),
            await settings.GetAsync(SettingKeys.CardsApiTokenBody, ct),
            await settings.GetAsync(SettingKeys.CardsApiTokenField, ct));

        // Interní služba často běží s certifikátem vlastní CA, kterou nody neznají.
        var ignoreTls = await settings.GetAsync(SettingKeys.CardsApiIgnoreTls, ct) == "true";
        if (auth == CardApiAuth.Windows || ignoreTls)
        {
            // Windows (NTLM/Negotiate) vyžaduje vlastní handler s přihlašovacími údaji — na Linuxu
            // ho .NET vyřídí spravovaným NTLM, případně přes GSSAPI (balík gssntlmssp).
            var handler = new HttpClientHandler { PreAuthenticate = true };
            if (ignoreTls)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            if (auth == CardApiAuth.Windows)
            {
                if (string.IsNullOrWhiteSpace(user))
                    throw new InvalidOperationException("Přihlášení Windows účtem: není zadaný účet ani servisní účet pro AD (Nastavení → Active Directory).");
                handler.Credentials = WindowsCredential(user, password ?? "", await settings.GetAsync(SettingKeys.LdapDomain, ct));
            }

            return new IdentifiersApiCardSource(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) }, options);
        }

        return new IdentifiersApiCardSource(httpClientFactory.CreateClient(CardSourceFactory.HttpClientName), options);
    }

    /// <summary>
    /// Účet pro NTLM/Negotiate z toho, jak je zapsaný v nastavení: <c>DOMÉNA\uživatel</c>,
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
        var numbers = employees
            .Where(e => !string.IsNullOrWhiteSpace(e.PersonalNumber))
            .Select(e => e.PersonalNumber!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var batch in numbers.Chunk(Parallelism))
        {
            var results = await Task.WhenAll(batch.Select(async number => (number, identifiers: await QueryAsync(number, ct))));
            foreach (var (number, identifiers) in results)
            {
                foreach (var identifier in identifiers)
                {
                    if (identifier.Active == false)
                        continue;
                    yield return new CardRecord(null, number, identifier.Value, IdentifierType.Card,
                        identifier.Note, identifier.ValidFrom, identifier.ValidTo);
                }
            }
        }
    }

    /// <summary>Výsledek zkoušky z Nastavení — všechno, co je třeba k rozlišení „špatná adresa“, „jiný formát čísla“ a „jiné schéma odpovědi“.</summary>
    public sealed record ProbeResult(
        string Url,
        string RequestBody,
        int StatusCode,
        string? ReasonPhrase,
        string? ContentType,
        string Body,
        IReadOnlyList<ApiIdentifier> Parsed)
    {
        public bool Success => StatusCode is >= 200 and < 300;
    }

    /// <summary>Zkouška z Nastavení: odeslaný požadavek, odpověď se stavem a hlavičkami a co z ní konektor přečte.</summary>
    public async Task<ProbeResult> ProbeAsync(string employeeNo, CancellationToken ct = default)
    {
        var requestBody = JsonSerializer.Serialize(new { employeeNo, idIdentifierSubType = options.SubType });
        using var request = await BuildRequestAsync(employeeNo, ct);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = response.IsSuccessStatusCode ? SafeParse(body) : [];
        return new ProbeResult(options.Url, requestBody, (int)response.StatusCode, response.ReasonPhrase,
            response.Content.Headers.ContentType?.ToString(), body, parsed);
    }

    private static IReadOnlyList<ApiIdentifier> SafeParse(string body)
    {
        try
        {
            return Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<ApiIdentifier>> QueryAsync(string employeeNo, CancellationToken ct)
    {
        try
        {
            return Parse(await PostAsync(employeeNo, ct));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Zaměstnanec bez identifikátoru — služba odpoví 404; není to chyba synchronizace.
            return [];
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(string employeeNo, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, options.Url)
        {
            Content = JsonContent.Create(new { employeeNo, idIdentifierSubType = options.SubType }),
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
        // Windows (NTLM/Negotiate) vyřizuje handler klienta podle Credentials.
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

    private async Task<string> PostAsync(string employeeNo, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(employeeNo, ct);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Integrační API odpovědělo {(int)response.StatusCode} pro zaměstnance {employeeNo}: {Truncate(body, 300)}",
                null, response.StatusCode);
        }

        return body;
    }

    private static readonly string[] ListProperties = ["identifiers", "items", "data", "result", "results", "value", "cards", "records"];
    private static readonly string[] ValueProperties = ["identifier", "identifierNo", "identifierNumber", "identifierValue", "cardNumber", "cardNo", "cardId", "number", "code", "value", "serialNumber", "serial", "chipNumber"];
    private static readonly string[] FromProperties = ["validFrom", "dateFrom", "from", "validityFrom", "startDate", "activeFrom"];
    private static readonly string[] ToProperties = ["validTo", "dateTo", "to", "validityTo", "endDate", "expiration", "expires", "activeTo"];
    private static readonly string[] ActiveProperties = ["active", "isActive", "enabled", "valid", "isValid"];
    private static readonly string[] StateProperties = ["state", "status", "stav"];
    private static readonly string[] NoteProperties = ["note", "description", "name", "type", "subTypeName", "identifierSubType"];

    /// <summary>Rozbor odpovědi bez znalosti přesného schématu — viz popis třídy.</summary>
    public static IReadOnlyList<ApiIdentifier> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => FindList(root) is { } list ? list.EnumerateArray().ToList() : [root],
            _ => [root],
        };

        var result = new List<ApiIdentifier>();
        foreach (var item in items)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String when !string.IsNullOrWhiteSpace(item.GetString()):
                    result.Add(new ApiIdentifier(item.GetString()!.Trim(), null, null, null, null));
                    break;
                case JsonValueKind.Number:
                    result.Add(new ApiIdentifier(item.GetRawText(), null, null, null, null));
                    break;
                case JsonValueKind.Object:
                    var value = First(item, ValueProperties);
                    if (string.IsNullOrWhiteSpace(value))
                        break;
                    result.Add(new ApiIdentifier(value.Trim(),
                        ParseDate(First(item, FromProperties)),
                        ParseDate(First(item, ToProperties)),
                        ParseActive(item),
                        First(item, NoteProperties)));
                    break;
            }
        }

        return result;
    }

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
