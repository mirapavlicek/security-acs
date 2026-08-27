using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

public record CardSyncResult(int Added, int Updated, int Deactivated, int Unmatched)
{
    public override string ToString()
        => $"přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}, nespárováno {Unmatched}";
}

/// <summary>
/// Synchronizace identifikátorů zaměstnanců z MSSQL — jeden člověk jich může mít
/// libovolný počet (např. tři karty a dvě SPZ), takže dotaz vrací <b>řádek na
/// identifikátor</b>, ne na osobu.
///
/// Očekávané sloupce:
/// <list type="bullet">
///   <item><c>AdAccount</c> nebo <c>PersonalNumber</c> — párování na zaměstnance,</item>
///   <item><c>Value</c> (nebo <c>CardNumber</c>) — hodnota identifikátoru,</item>
///   <item><c>Type</c> — Card / LicensePlate / Pin / Tag / Biometric / Other
///     (nepovinné, výchozí Card; akceptuje i české „karta“, „spz“),</item>
///   <item><c>WinPakCardHolderId</c>, <c>Note</c>, <c>ValidFrom</c>, <c>ValidTo</c> — nepovinné.</item>
/// </list>
/// Identifikátory, které ze zdroje zmizely, se deaktivují (nemažou — kvůli historii).
/// </summary>
public class CardSyncService(
    AcsDbContext db, SettingsService settings, AuditService audit,
    ILogger<CardSyncService>? logger = null)
{
    public async Task<CardSyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var connectionString = await settings.GetAsync(SettingKeys.CardsMssqlConnectionString, ct)
            ?? throw new InvalidOperationException("Není nastaven MSSQL connection string pro karty (Nastavení → Karty).");
        var query = await settings.GetAsync(SettingKeys.CardsMssqlQuery, ct)
            ?? throw new InvalidOperationException("Není nastaven SQL dotaz pro karty (Nastavení → Karty).");

        var byAd = await db.Employees.Where(e => e.AdAccount != null)
            .ToDictionaryAsync(e => e.AdAccount!, StringComparer.OrdinalIgnoreCase, ct);
        var byPersonal = await db.Employees.Where(e => e.PersonalNumber != null)
            .GroupBy(e => e.PersonalNumber!)
            .ToDictionaryAsync(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase, ct);
        var existing = (await db.EmployeeIdentifiers.ToListAsync(ct))
            .ToDictionary(i => (i.EmployeeId, i.Type, i.Value));

        int added = 0, updated = 0, unmatched = 0;
        var seen = new HashSet<(int, IdentifierType, string)>();

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
            var employee = Match(Get("AdAccount"), Get("PersonalNumber"), byAd, byPersonal);
            if (employee is null)
            {
                unmatched++;
                continue;
            }

            if (Get("WinPakCardHolderId") is { Length: > 0 } holder && employee.WinPakCardHolderId != holder)
                employee.WinPakCardHolderId = holder;

            var rawValue = Get("Value") ?? Get("CardNumber") ?? Get("LicensePlate");
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            var type = ParseType(Get("Type"), columns.ContainsKey("LicensePlate") && Get("CardNumber") is null);
            var value = EmployeeIdentifier.Normalize(rawValue);
            var key = (employee.Id, type, value);
            seen.Add(key);

            if (existing.TryGetValue(key, out var identifier))
            {
                var changed = !identifier.IsActive;
                identifier.IsActive = true;
                identifier.Note = Get("Note") ?? identifier.Note;
                if (ParseDate(Get("ValidFrom")) is { } from && identifier.ValidFrom != from)
                {
                    identifier.ValidFrom = from;
                    changed = true;
                }

                if (ParseDate(Get("ValidTo")) is { } to && identifier.ValidTo != to)
                {
                    identifier.ValidTo = to;
                    changed = true;
                }

                if (changed)
                    updated++;
            }
            else
            {
                db.EmployeeIdentifiers.Add(new EmployeeIdentifier
                {
                    EmployeeId = employee.Id,
                    Type = type,
                    Value = value,
                    Note = Get("Note"),
                    ValidFrom = ParseDate(Get("ValidFrom")),
                    ValidTo = ParseDate(Get("ValidTo")),
                    Source = RecordSource.Imported,
                });
                added++;
            }
        }

        await reader.CloseAsync();

        // Co ze zdroje zmizelo, jen deaktivujeme (ruční záznamy se nedotýkáme).
        var deactivated = 0;
        foreach (var (key, identifier) in existing)
        {
            if (identifier.Source == RecordSource.Imported && identifier.IsActive && !seen.Contains(key))
            {
                identifier.IsActive = false;
                deactivated++;
            }
        }

        await db.SaveChangesAsync(ct);
        await SyncPrimaryCardsAsync(ct);

        var result = new CardSyncResult(added, updated, deactivated, unmatched);
        logger?.LogInformation("Synchronizace karet: {Result}", result);
        await audit.LogAsync(userName, "cards-synced", "EmployeeIdentifier", null, result.ToString(), ct);
        return result;
    }

    /// <summary>Do <see cref="Employee.CardNumber"/> promítne první platnou kartu (kvůli zobrazení a hledání).</summary>
    private async Task SyncPrimaryCardsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cards = await db.EmployeeIdentifiers
            .Where(i => i.Type == IdentifierType.Card && i.IsActive)
            .OrderBy(i => i.Id)
            .ToListAsync(ct);
        var primary = cards.Where(c => c.IsValidAt(now))
            .GroupBy(c => c.EmployeeId)
            .ToDictionary(g => g.Key, g => g.First().Value);

        foreach (var employee in await db.Employees.ToListAsync(ct))
        {
            var value = primary.GetValueOrDefault(employee.Id);
            if (value is not null && employee.CardNumber != value)
                employee.CardNumber = value;
        }

        await db.SaveChangesAsync(ct);
    }

    private static Employee? Match(string? adAccount, string? personalNumber,
        Dictionary<string, Employee> byAd, Dictionary<string, Employee> byPersonal)
    {
        if (adAccount is { Length: > 0 } && byAd.TryGetValue(adAccount, out var byAdMatch))
            return byAdMatch;
        if (personalNumber is { Length: > 0 } && byPersonal.TryGetValue(personalNumber, out var byNumber))
            return byNumber;
        return null;
    }

    /// <summary>Rozpozná typ identifikátoru (anglicky i česky), výchozí je karta.</summary>
    public static IdentifierType ParseType(string? raw, bool defaultLicensePlate = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultLicensePlate ? IdentifierType.LicensePlate : IdentifierType.Card;

        return raw.Trim().ToLowerInvariant() switch
        {
            "card" or "karta" or "kartа" => IdentifierType.Card,
            "licenseplate" or "license_plate" or "spz" or "rz" or "vozidlo" => IdentifierType.LicensePlate,
            "pin" or "kod" or "kód" => IdentifierType.Pin,
            "tag" or "cip" or "čip" or "privesek" or "přívěsek" => IdentifierType.Tag,
            "biometric" or "biometrie" or "otisk" => IdentifierType.Biometric,
            _ => Enum.TryParse<IdentifierType>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : IdentifierType.Other,
        };
    }

    private static DateTime? ParseDate(string? raw)
        => DateTime.TryParse(raw, out var value) ? value : null;
}
