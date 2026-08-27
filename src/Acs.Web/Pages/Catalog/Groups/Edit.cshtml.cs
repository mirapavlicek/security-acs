using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Groups;

public class EditModel(AcsDbContext db, ReaderGroupService groups, AuditService audit) : PageModel
{
    public ReaderGroup Group { get; private set; } = null!;
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public List<Reader> AvailableReaders { get; private set; } = [];
    public List<ReaderGroup> AvailableGroups { get; private set; } = [];
    public List<ReaderGroup> ParentGroups { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var group = await db.ReaderGroups
            .Include(g => g.Members).ThenInclude(m => m.Reader)
            .Include(g => g.Members).ThenInclude(m => m.ChildGroup)
            .FirstOrDefaultAsync(g => g.Id == id);
        if (group is null)
            return NotFound();

        Group = group;
        Matrices = await db.ApprovalMatrices.Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync();

        var memberReaderIds = group.Members.Where(m => m.ReaderId != null).Select(m => m.ReaderId!.Value).ToHashSet();
        AvailableReaders = await db.Readers
            .Where(r => r.IsActive && !memberReaderIds.Contains(r.Id))
            .OrderBy(r => r.Name).ToListAsync();

        var memberGroupIds = group.Members.Where(m => m.ChildGroupId != null).Select(m => m.ChildGroupId!.Value).ToHashSet();
        AvailableGroups = await db.ReaderGroups
            .Where(g => g.Id != id && g.IsActive && !memberGroupIds.Contains(g.Id))
            .OrderBy(g => g.Name).ToListAsync();

        var parentIds = await db.ReaderGroupMembers
            .Where(m => m.ChildGroupId == id)
            .Select(m => m.GroupId)
            .ToListAsync();
        ParentGroups = await db.ReaderGroups.Where(g => parentIds.Contains(g.Id)).ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string name, string? description,
        int? approvalMatrixId, string? isActive)
    {
        var group = await db.ReaderGroups.FindAsync(id);
        if (group is null)
            return NotFound();

        group.Name = name.Trim();
        group.Description = description;
        group.ApprovalMatrixId = approvalMatrixId;
        group.IsActive = isActive == "true";
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "group-updated", "ReaderGroup", id.ToString(), name);
        Message = "Skupina uložena.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddReaderAsync(int id, int? readerId)
    {
        if (readerId is null)
            return RedirectToPage(new { id });

        if (!await db.ReaderGroupMembers.AnyAsync(m => m.GroupId == id && m.ReaderId == readerId))
        {
            db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = id, ReaderId = readerId });
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "group-member-added", "ReaderGroup", id.ToString(),
                $"čtečka {readerId}");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddChildGroupAsync(int id, int? childGroupId)
    {
        if (childGroupId is null)
            return RedirectToPage(new { id });

        if (await groups.WouldCreateCycleAsync(id, childGroupId.Value))
        {
            ErrorMessage = "Tuto skupinu nelze vnořit — vznikl by cyklus.";
            return RedirectToPage(new { id });
        }

        if (!await db.ReaderGroupMembers.AnyAsync(m => m.GroupId == id && m.ChildGroupId == childGroupId))
        {
            db.ReaderGroupMembers.Add(new ReaderGroupMember { GroupId = id, ChildGroupId = childGroupId });
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "group-member-added", "ReaderGroup", id.ToString(),
                $"vnořená skupina {childGroupId}");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(int id, int memberId)
    {
        var member = await db.ReaderGroupMembers.FindAsync(memberId);
        if (member is not null && member.GroupId == id)
        {
            db.ReaderGroupMembers.Remove(member);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "group-member-removed", "ReaderGroup", id.ToString());
        }

        return RedirectToPage(new { id });
    }
}
