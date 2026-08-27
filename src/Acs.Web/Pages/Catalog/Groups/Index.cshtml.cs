using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Groups;

public class IndexModel(AcsDbContext db, ReaderGroupService groups, AuditService audit) : PageModel
{
    public List<ReaderGroup> Groups { get; private set; } = [];
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public Dictionary<int, int> ReaderCounts { get; private set; } = new();

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Groups = await db.ReaderGroups.Include(g => g.ApprovalMatrix).OrderBy(g => g.Name).ToListAsync();
        Matrices = await db.ApprovalMatrices.Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync();
        foreach (var group in Groups)
            ReaderCounts[group.Id] = (await groups.ExpandReaderIdsAsync(group.Id)).Count;
    }

    /// <summary>Nastaví matici zaškrtnutým skupinám najednou.</summary>
    public async Task<IActionResult> OnPostAssignAsync(int[] groupIds, int? matrixId)
    {
        if (groupIds.Length == 0)
        {
            ErrorMessage = "Nevybrali jste žádnou skupinu.";
            return RedirectToPage();
        }

        ApprovalMatrix? matrix = null;
        if (matrixId is not null)
        {
            matrix = await db.ApprovalMatrices.FirstOrDefaultAsync(m => m.Id == matrixId);
            if (matrix is null)
            {
                ErrorMessage = "Vybraná matice neexistuje.";
                return RedirectToPage();
            }

            if (!matrix.IsActive)
            {
                ErrorMessage = $"Matice {matrix.Name} je neaktivní — žádosti by se podle ní neschvalovaly.";
                return RedirectToPage();
            }
        }

        var changed = await db.ReaderGroups.Where(g => groupIds.Contains(g.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.ApprovalMatrixId, matrixId));

        var target = matrix?.Name ?? "bez vlastní matice";
        await audit.LogAsync(User.Identity?.Name, "groups-matrix-bulk-assigned", "ReaderGroup", null,
            $"{changed} skupin → {target}");
        Message = $"Matice „{target}“ nastavena u {changed} skupin.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        var group = new ReaderGroup { Name = name.Trim() };
        db.ReaderGroups.Add(group);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "group-created", "ReaderGroup", group.Id.ToString(), name);
        return RedirectToPage("Edit", new { id = group.Id });
    }
}
