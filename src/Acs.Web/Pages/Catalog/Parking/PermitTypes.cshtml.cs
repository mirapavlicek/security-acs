using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Parking;

public class PermitTypesModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<ParkingPermitType> Types { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Types = await db.ParkingPermitTypes.Include(t => t.ApprovalMatrix)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, int binding)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Zadejte název druhu povolení.";
            return RedirectToPage();
        }

        var maxOrder = await db.ParkingPermitTypes.MaxAsync(t => (int?)t.SortOrder) ?? 0;
        var type = new ParkingPermitType
        {
            Name = name.Trim(),
            Binding = Enum.IsDefined(typeof(PermitBinding), binding) ? (PermitBinding)binding : PermitBinding.LicensePlate,
            SortOrder = maxOrder + 1,
        };
        db.ParkingPermitTypes.Add(type);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "permit-type-created", "ParkingPermitType", type.Id.ToString(), type.Name);
        return RedirectToPage("PermitTypeEdit", new { id = type.Id });
    }
}
