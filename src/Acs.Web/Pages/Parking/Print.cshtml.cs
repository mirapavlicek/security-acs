using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Acs.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Parking;

/// <summary>Tisková stránka kartičky za čelní sklo (bez rámu aplikace).</summary>
public class PrintModel(AcsDbContext db, ParkingAdminService parkingAdmin) : PageModel
{
    public PermitCardView Card { get; private set; } = null!;
    public string HolderName { get; private set; } = "";

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await parkingAdmin.GetGrantItemAsync(id);
        if (item is null)
            return NotFound();

        var isAdmin = User.IsInRole("Admin") || User.IsInRole("ParkingAdmin");
        var myEmployeeId = await db.Users.Where(u => u.Id == CurrentUserId)
            .Select(u => u.EmployeeId).FirstOrDefaultAsync();
        var isHolder = myEmployeeId is not null && item.Request!.TargetEmployeeId == myEmployeeId;
        if (!isAdmin && !isHolder)
            return Forbid();

        if (item.Status != RequestStatus.Issued)
            return BadRequest("Kartičku lze tisknout jen u vydaného povolení.");

        var permit = item.ParkingPermit!;
        if (permit.PermitType?.PrintsWindshieldCard != true)
            return BadRequest("K tomuto druhu povolení se kartička netiskne.");

        HolderName = item.Request!.TargetEmployee!.FullName;
        Card = PermitCardView.For(permit, item.Request.TargetEmployee);
        return Page();
    }
}
