using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Matrices;

public class EditModel(AcsDbContext db, AuditService audit) : PageModel
{
    public ApprovalMatrix Matrix { get; private set; } = new() { Name = "" };
    public List<AppUser> Users { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var matrix = await LoadMatrixAsync(id);
        if (matrix is null)
            return NotFound();

        Matrix = matrix;
        Users = await db.Users.Where(u => u.IsActive).OrderBy(u => u.UserName).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(int id, string name, string? description, string? isActive)
    {
        var matrix = await db.ApprovalMatrices.FindAsync(id);
        if (matrix is null)
            return NotFound();

        matrix.Name = name.Trim();
        matrix.Description = description;
        matrix.IsActive = isActive == "true";
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "matrix-updated", "ApprovalMatrix", id.ToString(), name);
        Message = "Matice uložena.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddLevelAsync(int id, string? name)
    {
        var maxOrder = await db.ApprovalLevels.Where(l => l.MatrixId == id)
            .MaxAsync(l => (int?)l.Order) ?? 0;
        db.ApprovalLevels.Add(new ApprovalLevel
        {
            MatrixId = id,
            Order = maxOrder + 1,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "matrix-level-added", "ApprovalMatrix", id.ToString());
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUpdateLevelAsync(int id, int levelId, string mode, int? requiredCount)
    {
        var level = await db.ApprovalLevels.FindAsync(levelId);
        if (level is null || level.MatrixId != id)
            return NotFound();

        level.Mode = Enum.TryParse<ApprovalMode>(mode, out var parsed) ? parsed : ApprovalMode.Any;
        level.RequiredCount = level.Mode == ApprovalMode.Quorum ? Math.Max(1, requiredCount ?? 1) : null;
        await db.SaveChangesAsync();
        Message = $"Úroveň {level.Order} uložena.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveLevelAsync(int id, int levelId)
    {
        var level = await db.ApprovalLevels.FindAsync(levelId);
        if (level is null || level.MatrixId != id)
            return NotFound();

        db.ApprovalLevels.Remove(level);
        await db.SaveChangesAsync();

        // Přečíslování úrovní, aby zůstaly souvislé 1..n.
        var remaining = await db.ApprovalLevels.Where(l => l.MatrixId == id)
            .OrderBy(l => l.Order).ToListAsync();
        for (var i = 0; i < remaining.Count; i++)
            remaining[i].Order = i + 1;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "matrix-level-removed", "ApprovalMatrix", id.ToString());
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddApproverAsync(int id, int levelId, int? userId)
    {
        if (userId is null)
        {
            ErrorMessage = "Vyberte uživatele.";
            return RedirectToPage(new { id });
        }

        var level = await db.ApprovalLevels.Include(l => l.Approvers).FirstOrDefaultAsync(l => l.Id == levelId);
        if (level is null || level.MatrixId != id)
            return NotFound();

        if (level.Approvers.All(a => a.UserId != userId))
        {
            db.Approvers.Add(new Approver { LevelId = levelId, UserId = userId });
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "matrix-approver-added", "ApprovalMatrix", id.ToString(),
                $"úroveň {level.Order}, uživatel {userId}");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveApproverAsync(int id, int approverId)
    {
        var approver = await db.Approvers.FindAsync(approverId);
        if (approver is not null)
        {
            db.Approvers.Remove(approver);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "matrix-approver-removed", "ApprovalMatrix", id.ToString());
        }

        return RedirectToPage(new { id });
    }

    private Task<ApprovalMatrix?> LoadMatrixAsync(int id)
        => db.ApprovalMatrices
            .Include(m => m.Levels).ThenInclude(l => l.Approvers).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(m => m.Id == id);
}
