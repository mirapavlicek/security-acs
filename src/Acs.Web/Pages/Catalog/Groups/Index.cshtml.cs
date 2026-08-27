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
    public Dictionary<int, int> ReaderCounts { get; private set; } = new();

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        Groups = await db.ReaderGroups.Include(g => g.ApprovalMatrix).OrderBy(g => g.Name).ToListAsync();
        foreach (var group in Groups)
            ReaderCounts[group.Id] = (await groups.ExpandReaderIdsAsync(group.Id)).Count;
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
