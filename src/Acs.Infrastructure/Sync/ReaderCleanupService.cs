using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Sync;

public record ReaderCleanupResult(int Deleted, int SkippedActive, int SkippedReferenced, IReadOnlyList<string> Referenced)
{
    public override string ToString()
    {
        var text = $"Smazáno čteček: {Deleted}.";
        if (SkippedActive > 0)
            text += $" Přeskočeno aktivních: {SkippedActive} (mažou se jen neaktivní).";
        if (SkippedReferenced > 0)
        {
            text += $" Přeskočeno s vazbami: {SkippedReferenced} — visí na nich žádost, skupina nebo závislost,"
                    + $" zůstávají neaktivní: {string.Join(", ", Referenced.Take(10))}"
                    + (Referenced.Count > 10 ? " …" : "");
        }

        return text;
    }
}

/// <summary>
/// Odstranění čteček, které v číselníku jen překážejí — typicky odhady z výkresů,
/// které import z dokumentace deaktivoval, protože neměly protějšek.
///
/// Maže se jen to, co nemá historii: neaktivní čtečka, na kterou nevisí žádná
/// žádost, skupina ani závislost. Cokoli s vazbou zůstává neaktivní, aby se
/// nerozbila dohledatelnost — u přístupu musí jít zpětně zjistit, ke které
/// čtečce byl.
/// </summary>
public class ReaderCleanupService(AcsDbContext db, AuditService audit)
{
    public async Task<ReaderCleanupResult> DeleteAsync(
        IReadOnlyCollection<int> readerIds, string? userName, CancellationToken ct = default)
    {
        if (readerIds.Count == 0)
            return new ReaderCleanupResult(0, 0, 0, []);

        var readers = await db.Readers
            .Where(r => readerIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.IsActive })
            .ToListAsync(ct);

        var candidateIds = readers.Where(r => !r.IsActive).Select(r => r.Id).ToList();
        var skippedActive = readers.Count - candidateIds.Count;

        var referenced = new HashSet<int>();
        referenced.UnionWith(await db.AccessRequestItems
            .Where(i => i.ReaderId != null && candidateIds.Contains(i.ReaderId.Value))
            .Select(i => i.ReaderId!.Value).Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.ReaderGroupMembers
            .Where(m => m.ReaderId != null && candidateIds.Contains(m.ReaderId.Value))
            .Select(m => m.ReaderId!.Value).Distinct().ToListAsync(ct));
        // Závislost drží obě strany hrany — čtečku, která vyžaduje, i tu vyžadovanou.
        referenced.UnionWith(await db.ReaderDependencies
            .Where(d => candidateIds.Contains(d.ReaderId))
            .Select(d => d.ReaderId).Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.ReaderDependencies
            .Where(d => candidateIds.Contains(d.RequiresReaderId))
            .Select(d => d.RequiresReaderId).Distinct().ToListAsync(ct));
        referenced.IntersectWith(candidateIds);

        var deletable = candidateIds.Where(id => !referenced.Contains(id)).ToList();
        var deleted = deletable.Count == 0
            ? 0
            : await db.Readers.Where(r => deletable.Contains(r.Id)).ExecuteDeleteAsync(ct);

        var referencedNames = readers.Where(r => referenced.Contains(r.Id))
            .Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var result = new ReaderCleanupResult(deleted, skippedActive, referenced.Count, referencedNames);

        if (deleted > 0)
            await audit.LogAsync(userName, "readers-deleted", "Reader", null, result.ToString(), ct);

        return result;
    }
}
