using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record ReaderRemovalResult(int Deleted, int Deactivated, IReadOnlyList<string> DeactivatedNames)
{
    public override string ToString()
    {
        var text = $"Smazáno čteček: {Deleted}.";
        if (Deactivated > 0)
        {
            text += $" Deaktivováno a skryto: {Deactivated} — visí na nich žádost, skupina nebo závislost,"
                    + $" proto zůstávají v evidenci: {string.Join(", ", DeactivatedNames.Take(10))}"
                    + (DeactivatedNames.Count > 10 ? " …" : "");
        }

        return text;
    }
}

/// <summary>
/// Odstranění čteček z číselníku — typicky odhadů z půdorysů, které nahradil
/// import z dokumentace.
///
/// Správce chce čtečku pryč a nemá řešit, co na ní visí. Řeší to služba: čtečka
/// bez vazeb se smaže; čtečka, na které je žádost, členství ve skupině nebo
/// závislost, se deaktivuje a zmizí ze seznamů, plánů i výběru v žádostech,
/// ale záznam zůstane — u přístupu musí jít zpětně dohledat, ke které čtečce byl.
/// </summary>
public class ReaderCleanupService(AcsDbContext db, AuditService audit)
{
    public async Task<ReaderRemovalResult> RemoveAsync(
        IReadOnlyCollection<int> readerIds, string? userName, CancellationToken ct = default)
    {
        if (readerIds.Count == 0)
            return new ReaderRemovalResult(0, 0, []);

        var readers = await db.Readers
            .Where(r => readerIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);
        var ids = readers.Select(r => r.Id).ToList();

        var referenced = new HashSet<int>();
        referenced.UnionWith(await db.AccessRequestItems
            .Where(i => i.ReaderId != null && ids.Contains(i.ReaderId.Value))
            .Select(i => i.ReaderId!.Value).Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.ReaderGroupMembers
            .Where(m => m.ReaderId != null && ids.Contains(m.ReaderId.Value))
            .Select(m => m.ReaderId!.Value).Distinct().ToListAsync(ct));
        // Závislost drží obě strany hrany — čtečku, která vyžaduje, i tu vyžadovanou.
        referenced.UnionWith(await db.ReaderDependencies
            .Where(d => ids.Contains(d.ReaderId))
            .Select(d => d.ReaderId).Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.ReaderDependencies
            .Where(d => ids.Contains(d.RequiresReaderId))
            .Select(d => d.RequiresReaderId).Distinct().ToListAsync(ct));

        var deletable = ids.Where(id => !referenced.Contains(id)).ToList();
        var deleted = deletable.Count == 0
            ? 0
            : await db.Readers.Where(r => deletable.Contains(r.Id)).ExecuteDeleteAsync(ct);

        var deactivated = referenced.Count == 0
            ? 0
            : await db.Readers.Where(r => referenced.Contains(r.Id) && r.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, false), ct);

        var deactivatedNames = readers.Where(r => referenced.Contains(r.Id))
            .Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var result = new ReaderRemovalResult(deleted, referenced.Count, deactivatedNames);

        if (deleted > 0 || deactivated > 0)
            await audit.LogAsync(userName, "readers-removed", "Reader", null, result.ToString(), ct);

        return result;
    }
}
