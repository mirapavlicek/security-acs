using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Sync;

/// <param name="Duplicates">Záznamy ze zdroje, které u téhož člověka opakovaly už načtený identifikátor (přeskočeny).</param>
/// <param name="Fetched">Kolik záznamů zdroj vrátil (uloženo do otisku ImportedIdentifiers).</param>
/// <param name="UnmatchedPersons">Kolik různých osobních čísel ze zdroje v ACS není.</param>
/// <param name="Skipped">Záznamy jen v otisku — podtyp bez pravidla (viz Nastavení → Karty).</param>
public record CardSyncResult(int Added, int Updated, int Deactivated, int Unmatched, int Duplicates = 0,
    int Fetched = 0, int UnmatchedPersons = 0, int Skipped = 0)
{
    public override string ToString()
        => $"staženo {Fetched}, přidáno {Added}, aktualizováno {Updated}, deaktivováno {Deactivated}, nespárováno {Unmatched}"
           + (UnmatchedPersons > 0 ? $" ({UnmatchedPersons} osobních čísel v ACS není)" : "")
           + (Duplicates > 0 ? $", přeskočeno duplicit {Duplicates}" : "")
           + (Skipped > 0 ? $", bez pravidla podtypu {Skipped} (jen v otisku)" : "");
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
    ILogger<CardSyncService>? logger = null, SettingsService? settings = null)
{
    public async Task<CardSyncResult> SyncAsync(string? userName, CancellationToken ct = default)
    {
        var source = await sources.CreateAsync(ct);
        using var sourceLifetime = source as IDisposable;
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

        int added = 0, updated = 0, unmatched = 0, duplicates = 0, skipped = 0;
        var seen = new HashSet<(int, IdentifierType, string)>();
        var unmatchedPersons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedAt = DateTime.UtcNow;
        var snapshot = new List<ImportedIdentifier>();

        await foreach (var record in source.ReadAsync(employees, ct))
        {
            var employee = Match(record.AdAccount, record.PersonalNumber, byAd, byPersonal);
            if (!string.IsNullOrWhiteSpace(record.Value))
            {
                snapshot.Add(new ImportedIdentifier
                {
                    EmployeeNo = record.PersonalNumber,
                    AdAccount = record.AdAccount,
                    SubType = record.SubType,
                    RawValue = Truncate(record.RawValue ?? record.Value, 128),
                    Value = Truncate(EmployeeIdentifier.Normalize(record.Value), 128),
                    Type = record.Type,
                    EmployeeId = employee?.Id,
                    SkipReason = record.SkipReason,
                    FetchedAt = fetchedAt,
                });
            }

            // Jen do otisku (podtyp bez pravidla) — do identifikátorů se nepřenáší.
            if (record.SkipReason is not null)
            {
                skipped++;
                continue;
            }

            if (employee is null)
            {
                unmatched++;
                unmatchedPersons.Add(record.PersonalNumber ?? record.AdAccount ?? "");
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
        await ReplaceSnapshotAsync(snapshot, ct);

        var result = new CardSyncResult(added, updated, deactivated, unmatched, duplicates,
            snapshot.Count, unmatchedPersons.Count(p => p.Length > 0), skipped);
        logger?.LogInformation("Synchronizace karet ({Source}): {Result}", source.Description, result);
        await audit.LogAsync(userName, "cards-synced", "EmployeeIdentifier", null, result.ToString(), ct);
        if (settings is not null)
            await settings.SetAsync(SettingKeys.CardsLastImportStats, $"{fetchedAt:yyyy-MM-dd HH:mm} UTC — {result}", userName, ct);
        return result;
    }

    /// <summary>
    /// Otisk posledního stažení: co zdroj vrátil, jak se číslo převedlo a ke komu se spárovalo.
    /// Přepisuje se celý — je to pohled na zdroj, ne historie. Párování nad databází ACS
    /// tak jde kdykoli zkontrolovat (SQL nad ImportedIdentifiers, osobní čísla bez zaměstnance).
    /// </summary>
    private async Task ReplaceSnapshotAsync(List<ImportedIdentifier> snapshot, CancellationToken ct)
    {
        await db.ImportedIdentifiers.ExecuteDeleteAsync(ct);
        db.ImportedIdentifiers.AddRange(snapshot);
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

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
