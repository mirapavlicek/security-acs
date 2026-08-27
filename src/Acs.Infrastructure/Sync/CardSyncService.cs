using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record CardSyncResult(int Updated, int Unmatched)
{
    public override string ToString() => $"aktualizováno {Updated}, nespárováno {Unmatched}";
}

/// <summary>
/// Synchronizace karet z MSSQL: dotaz vrací sloupce
/// <c>AdAccount</c> nebo <c>PersonalNumber</c> (párování na zaměstnance),
/// <c>CardNumber</c> a volitelně <c>WinPakCardHolderId</c>.
/// Zaměstnanci se načítají z AD, karty právě odsud.
/// </summary>
public class CardSyncService(AcsDbContext db, SettingsService settings, AuditService audit)
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

        int updated = 0, unmatched = 0;

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
            var adAccount = Get("AdAccount");
            var personalNumber = Get("PersonalNumber");
            var cardNumber = Get("CardNumber");
            var holderId = Get("WinPakCardHolderId");

            Domain.Entities.Employee? employee = null;
            if (adAccount is not null)
                byAd.TryGetValue(adAccount, out employee);
            if (employee is null && personalNumber is not null)
                byPersonal.TryGetValue(personalNumber, out employee);

            if (employee is null)
            {
                unmatched++;
                continue;
            }

            var changed = false;
            if (cardNumber is not null && employee.CardNumber != cardNumber)
            {
                employee.CardNumber = cardNumber;
                changed = true;
            }

            if (holderId is not null && employee.WinPakCardHolderId != holderId)
            {
                employee.WinPakCardHolderId = holderId;
                changed = true;
            }

            if (changed)
                updated++;
        }

        await db.SaveChangesAsync(ct);
        var result = new CardSyncResult(updated, unmatched);
        await audit.LogAsync(userName, "cards-synced", "Employee", null, result.ToString(), ct);
        return result;
    }
}
