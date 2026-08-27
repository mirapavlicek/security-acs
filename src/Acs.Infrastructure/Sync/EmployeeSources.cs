using System.Net.Http.Json;
using Acs.Infrastructure.Settings;
using Microsoft.Data.SqlClient;

namespace Acs.Infrastructure.Sync;

/// <summary>Záznam zaměstnance z externího zdroje (MSSQL nebo API).</summary>
public record EmployeeRecord(
    string ExternalId,
    string? PersonalNumber,
    string FirstName,
    string LastName,
    string? Email,
    string? Department,
    string? AdAccount,
    string? CardNumber);

/// <summary>Zdroj zaměstnanců — zadání nechává MSSQL/API otevřené, proto adaptér.</summary>
public interface IEmployeeSource
{
    Task<IReadOnlyList<EmployeeRecord>> FetchAsync(CancellationToken ct = default);
}

/// <summary>
/// Načtení zaměstnanců z MSSQL. Dotaz je konfigurovatelný v GUI a musí vracet
/// sloupce: ExternalId, PersonalNumber, FirstName, LastName, Email, Department,
/// AdAccount, CardNumber (chybějící sloupce se přeskočí).
/// </summary>
public class MssqlEmployeeSource(SettingsService settings) : IEmployeeSource
{
    public async Task<IReadOnlyList<EmployeeRecord>> FetchAsync(CancellationToken ct = default)
    {
        var connectionString = await settings.GetAsync(SettingKeys.EmployeeMssqlConnectionString, ct)
            ?? throw new InvalidOperationException("Není nastaven MSSQL connection string (Nastavení → Zdroj zaměstnanců).");
        var query = await settings.GetAsync(SettingKeys.EmployeeMssqlQuery, ct)
            ?? throw new InvalidOperationException("Není nastaven MSSQL dotaz (Nastavení → Zdroj zaměstnanců).");

        var result = new List<EmployeeRecord>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var columns = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(i => reader.GetName(i), i => i, StringComparer.OrdinalIgnoreCase);
        string? Get(string name)
            => columns.TryGetValue(name, out var i) && !reader.IsDBNull(i) ? reader.GetValue(i).ToString() : null;

        while (await reader.ReadAsync(ct))
        {
            var externalId = Get("ExternalId") ?? Get("PersonalNumber");
            if (externalId is null)
                continue;
            result.Add(new EmployeeRecord(
                externalId, Get("PersonalNumber"),
                Get("FirstName") ?? "", Get("LastName") ?? "",
                Get("Email"), Get("Department"), Get("AdAccount"), Get("CardNumber")));
        }

        return result;
    }
}

/// <summary>Načtení zaměstnanců z REST API (GET vrací JSON pole objektů EmployeeRecord).</summary>
public class ApiEmployeeSource(HttpClient httpClient, SettingsService settings) : IEmployeeSource
{
    public async Task<IReadOnlyList<EmployeeRecord>> FetchAsync(CancellationToken ct = default)
    {
        var url = await settings.GetAsync(SettingKeys.EmployeeApiUrl, ct)
            ?? throw new InvalidOperationException("Není nastavena API URL (Nastavení → Zdroj zaměstnanců).");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (await settings.GetAsync(SettingKeys.EmployeeApiKey, ct) is { } apiKey)
            request.Headers.Add("X-Api-Key", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<EmployeeRecord>>(ct) ?? [];
    }
}

/// <summary>Vybere aktivní zdroj podle nastavení (None → null).</summary>
public class EmployeeSourceFactory(
    SettingsService settings, IHttpClientFactory httpClientFactory, Auth.DcLocator dcLocator)
{
    public virtual async Task<IEmployeeSource?> CreateAsync(CancellationToken ct = default)
        => (await settings.GetAsync(SettingKeys.EmployeeSourceMode, ct))?.ToLowerInvariant() switch
        {
            "ad" => new LdapEmployeeSource(settings, dcLocator),
            "mssql" => new MssqlEmployeeSource(settings),
            "api" => new ApiEmployeeSource(httpClientFactory.CreateClient(nameof(ApiEmployeeSource)), settings),
            _ => null,
        };
}
