using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

/// <param name="Duplicates">Záznamy ze zdroje, které u téhož člověka opakovaly už načtený identifikátor (přeskočeny).</param>
public record CardSyncResult(int Added, int Updated, int Deactivated, int Unmatched, int Duplicates = 0)
{
    public override string ToString()
        => $"přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}, nespárováno {Unmatched}"
           + (Duplicates > 0 ? $", přeskočeno duplicit {Duplicates}" : "");
}

/// <summary>
/// Synchronizace identifikátorů zaměstnanců (karty, SPZ…) ze zvoleného zdroje
/// (<see cref="ICardSource"/>: MSSQL dotaz, nebo integrační API po zaměstnancích).
/// Jeden člověk jich může mít libovolný počet (víc karet, víc SPZ) a všechny se
/// evidují; zdroj vrací <b>záznam na identifikátor</b>. Stejný identifikátor u
/// téhož člověka se bere jen jednou — první záznam platí, další se přeskočí.
/// Identifikátory, které ze zdroje zmizely, se deaktivují (nemažou — kvůli historii).
/// </summary>
public class CardSyncService(
    AcsDbContext db, CardSourceFactory sources, AuditService audit,
    ILogger<CardSyncService>? logger = null)
{
    public async Task<CardSyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var source = await sources.CreateAsync(ct);
        var employees = await db.Employees.Where(e => e.IsActive).ToListAsync(ct);
        var byAd = employees.Where(e => e.AdAccount != null)
            .GroupBy(e => e.AdAccount!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byPersonal = employees.Where(e => e.PersonalNumber != null)
            .GroupBy(e => e.PersonalNumber!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Stejný identifikátor u téhož člověka smí být v databázi jen jednou; kdyby tam
        // z dřívějška duplicity byly, platí nejstarší záznam a ostatní se deaktivují.
        var existing = new Dictionary<(int, IdentifierType, string), EmployeeIdentifier>();
        var deactivated = 0;
        foreach (var identifier in await db.EmployeeIdentifiers.OrderBy(i => i.Id).ToListAsync(ct))
        {
            if (existing.TryAdd((identifier.EmployeeId, identifier.Type, identifier.Value), identifier))
                continue;
            if (identifier.IsActive)
            {
                identifier.IsActive = false;
                deactivated++;
            }
        }

        int added = 0, updated = 0, unmatched = 0, duplicates = 0;
        var seen = new HashSet<(int, IdentifierType, string)>();

        await foreach (var record in source.ReadAsync(employees, ct))
        {
            var employee = Match(record.AdAccount, record.PersonalNumber, byAd, byPersonal);
            if (employee is null)
            {
                unmatched++;
                continue;
            }

            if (record.WinPakCardHolderId is { Length: > 0 } holder && employee.WinPakCardHolderId != holder)
                employee.WinPakCardHolderId = holder;

            if (string.IsNullOrWhiteSpace(record.Value))
                continue;

            var type = record.Type;
            var value = EmployeeIdentifier.Normalize(record.Value);
            var key = (employee.Id, type, value);
            if (!seen.Add(key))
            {
                // Zdroj vrátil tentýž identifikátor u téhož člověka znovu — první záznam platí.
                duplicates++;
                continue;
            }

            if (existing.TryGetValue(key, out var identifier))
            {
                var changed = !identifier.IsActive;
                identifier.IsActive = true;
                identifier.Note = record.Note ?? identifier.Note;
                if (record.ValidFrom is { } from && identifier.ValidFrom != from)
                {
                    identifier.ValidFrom = from;
                    changed = true;
                }

                if (record.ValidTo is { } to && identifier.ValidTo != to)
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
                    Note = record.Note,
                    ValidFrom = record.ValidFrom,
                    ValidTo = record.ValidTo,
                    Source = RecordSource.Imported,
                });
                added++;
            }
        }


        // Co ze zdroje zmizelo, jen deaktivujeme (ruční záznamy se nedotýkáme).
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

        var result = new CardSyncResult(added, updated, deactivated, unmatched, duplicates);
        logger?.LogInformation("Synchronizace karet ({Source}): {Result}", source.Description, result);
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
}
