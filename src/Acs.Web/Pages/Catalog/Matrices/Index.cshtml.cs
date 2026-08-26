using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Matrices;

public class IndexModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public Dictionary<int, int> UsageCounts { get; private set; } = new();

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        Matrices = await db.ApprovalMatrices.Include(m => m.Levels).OrderBy(m => m.Name).ToListAsync();
        UsageCounts = await db.Readers
            .Where(r => r.ApprovalMatrixId != null)
            .GroupBy(r => r.ApprovalMatrixId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        var matrix = new ApprovalMatrix { Name = name.Trim() };
        db.ApprovalMatrices.Add(matrix);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "matrix-created", "ApprovalMatrix", matrix.Id.ToString(), name);
        return RedirectToPage("Edit", new { id = matrix.Id });
    }
}
