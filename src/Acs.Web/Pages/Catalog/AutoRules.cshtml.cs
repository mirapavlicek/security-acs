using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog;

public class AutoRulesModel(AcsDbContext db, AutoAssignmentService autoAssign, AuditService audit) : PageModel
{
    public List<AutoAssignmentRule> Rules { get; private set; } = [];
    public List<ReaderGroup> Groups { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        Rules = await db.AutoAssignmentRules.Include(r => r.ReaderGroup)
            .OrderBy(r => r.Department).ToListAsync();
        Groups = await db.ReaderGroups.Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string department, int readerGroupId)
    {
        db.AutoAssignmentRules.Add(new AutoAssignmentRule
        {
            Department = department.Trim(),
            ReaderGroupId = readerGroupId,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "auto-rule-created", "AutoAssignmentRule", null,
            $"{department} → skupina {readerGroupId}");
        Message = "Pravidlo vytvořeno.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int ruleId)
    {
        var rule = await db.AutoAssignmentRules.FindAsync(ruleId);
        if (rule is null)
            return NotFound();

        rule.IsActive = !rule.IsActive;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "auto-rule-toggled", "AutoAssignmentRule", ruleId.ToString());
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int ruleId)
    {
        var rule = await db.AutoAssignmentRules.FindAsync(ruleId);
        if (rule is not null)
        {
            db.AutoAssignmentRules.Remove(rule);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "auto-rule-deleted", "AutoAssignmentRule", ruleId.ToString());
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        var result = await autoAssign.RunAsync(User.Identity?.Name);
        Message = $"Vyhodnoceno: {result}.";
        return RedirectToPage();
    }
}
