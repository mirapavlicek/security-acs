using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>
/// Práce se skupinami čteček: rekurzivní expanze členů (vnořené skupiny),
/// sestavení řetězu schvalovacích matic (skupina → nadřazené skupiny)
/// a detekce cyklů při vnořování.
/// </summary>
public class ReaderGroupService(AcsDbContext db)
{
    private sealed record Edge(int GroupId, int? ReaderId, int? ChildGroupId);

    private Task<List<Edge>> LoadEdgesAsync(CancellationToken ct)
        => db.ReaderGroupMembers
            .Select(m => new Edge(m.GroupId, m.ReaderId, m.ChildGroupId))
            .ToListAsync(ct);

    /// <summary>Všechny čtečky ve skupině včetně vnořených skupin (rekurzivně).</summary>
    public async Task<HashSet<int>> ExpandReaderIdsAsync(int groupId, CancellationToken ct = default)
    {
        var edges = await LoadEdgesAsync(ct);
        return ExpandReaderIds([groupId], edges);
    }

    /// <summary>Expanze více skupin najednou (jedno načtení členství).</summary>
    public async Task<HashSet<int>> ExpandReaderIdsAsync(IEnumerable<int> groupIds, CancellationToken ct = default)
    {
        var edges = await LoadEdgesAsync(ct);
        return ExpandReaderIds(groupIds, edges);
    }

    private static HashSet<int> ExpandReaderIds(IEnumerable<int> groupIds, List<Edge> edges)
    {
        var byGroup = edges.GroupBy(e => e.GroupId).ToDictionary(g => g.Key, g => g.ToList());
        var readers = new HashSet<int>();
        var visited = new HashSet<int>();
        var stack = new Stack<int>(groupIds);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current) || !byGroup.TryGetValue(current, out var members))
                continue;
            foreach (var m in members)
            {
                if (m.ReaderId is not null)
                    readers.Add(m.ReaderId.Value);
                else if (m.ChildGroupId is not null)
                    stack.Push(m.ChildGroupId.Value);
            }
        }

        return readers;
    }

    /// <summary>
    /// Řetěz matic pro žádost o skupinu: nejdřív matice samotné skupiny,
    /// pak matice nadřazených skupin podle vzdálenosti (přímý rodič dřív než
    /// prarodič). Duplicitní matice se v řetězu neopakují; skupiny bez matice
    /// se přeskakují.
    /// </summary>
    public async Task<List<int>> GetMatrixChainAsync(int groupId, CancellationToken ct = default)
    {
        var edges = await LoadEdgesAsync(ct);
        // hrany rodičovství: ChildGroupId -> GroupId (rodič)
        var parentsByChild = edges.Where(e => e.ChildGroupId != null)
            .GroupBy(e => e.ChildGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.GroupId).ToList());

        // BFS od skupiny nahoru — pořadí podle vzdálenosti
        var ordered = new List<int> { groupId };
        var visited = new HashSet<int> { groupId };
        var queue = new Queue<int>([groupId]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!parentsByChild.TryGetValue(current, out var parents))
                continue;
            foreach (var p in parents.Where(visited.Add))
            {
                ordered.Add(p);
                queue.Enqueue(p);
            }
        }

        var matrices = await db.ReaderGroups
            .Where(g => ordered.Contains(g.Id) && g.ApprovalMatrixId != null && g.IsActive)
            .Select(g => new { g.Id, MatrixId = g.ApprovalMatrixId!.Value })
            .ToDictionaryAsync(x => x.Id, x => x.MatrixId, ct);

        var chain = new List<int>();
        foreach (var gid in ordered)
        {
            if (matrices.TryGetValue(gid, out var matrixId) && !chain.Contains(matrixId))
                chain.Add(matrixId);
        }

        return chain;
    }

    /// <summary>Vznikl by přidáním vnořené skupiny cyklus? (childGroup už obsahuje group)</summary>
    public async Task<bool> WouldCreateCycleAsync(int groupId, int childGroupId, CancellationToken ct = default)
    {
        if (groupId == childGroupId)
            return true;

        var edges = await LoadEdgesAsync(ct);
        var childrenByGroup = edges.Where(e => e.ChildGroupId != null)
            .GroupBy(e => e.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ChildGroupId!.Value).ToList());

        var stack = new Stack<int>([childGroupId]);
        var visited = new HashSet<int>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == groupId)
                return true;
            if (!visited.Add(current) || !childrenByGroup.TryGetValue(current, out var children))
                continue;
            foreach (var c in children)
                stack.Push(c);
        }

        return false;
    }
}
