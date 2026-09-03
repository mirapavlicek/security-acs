using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Parking;

public class PermitTypeEditModel(AcsDbContext db, AuditService audit) : PageModel
{
    public ParkingPermitType Type { get; private set; } = null!;
    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public int PermitCount { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var type = await db.ParkingPermitTypes.FindAsync(id);
        if (type is null)
            return NotFound();

        Type = type;
        Matrices = await db.ApprovalMatrices.Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync();
        PermitCount = await db.ParkingPermits.CountAsync(p => p.PermitTypeId == id);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string name, string? code, string? description,
        int binding, int maxPlates, int? defaultValidityMonths, int? approvalMatrixId, int sortOrder,
        string? allSitesByDefault, string? isActive, string? printsWindshieldCard,
        string? cardTitle, string? cardScopeText)
    {
        var type = await db.ParkingPermitTypes.FindAsync(id);
        if (type is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Název je povinný.";
            return RedirectToPage(new { id });
        }

        type.Name = name.Trim();
        type.Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        type.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        type.Binding = Enum.IsDefined(typeof(PermitBinding), binding) ? (PermitBinding)binding : type.Binding;
        type.MaxPlates = Math.Clamp(maxPlates, 1, 10);
        type.DefaultValidityMonths = defaultValidityMonths is > 0 ? defaultValidityMonths : null;
        type.ApprovalMatrixId = approvalMatrixId;
        type.SortOrder = sortOrder;
        type.AllSitesByDefault = allSitesByDefault == "true";
        type.IsActive = isActive == "true";
        type.PrintsWindshieldCard = printsWindshieldCard == "true";
        type.CardTitle = string.IsNullOrWhiteSpace(cardTitle) ? null : cardTitle.Trim();
        type.CardScopeText = string.IsNullOrWhiteSpace(cardScopeText) ? null : cardScopeText.Trim();
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "permit-type-updated", "ParkingPermitType", id.ToString(), type.Name);
        Message = "Druh povolení uložen.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var type = await db.ParkingPermitTypes.FindAsync(id);
        if (type is null)
            return NotFound();

        if (await db.ParkingPermits.AnyAsync(p => p.PermitTypeId == id))
        {
            ErrorMessage = "Druh se používá v povoleních — nelze smazat.";
            return RedirectToPage(new { id });
        }

        db.ParkingPermitTypes.Remove(type);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "permit-type-deleted", "ParkingPermitType", id.ToString(), type.Name);
        return RedirectToPage("PermitTypes");
    }
}
