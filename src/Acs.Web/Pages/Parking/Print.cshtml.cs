using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Pdf;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Parking;

/// <summary>Kartička za čelní sklo jako PDF (150 × 70 mm) — pro držitele povolení a správce parkování.</summary>
public class PrintModel(AcsDbContext db, ParkingAdminService parkingAdmin) : PageModel
{
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
            return BadRequest("Kartičku lze vygenerovat jen u vydaného povolení.");

        var permit = item.ParkingPermit!;
        if (permit.PermitType?.PrintsWindshieldCard != true)
            return BadRequest("K tomuto druhu povolení se kartička netiskne.");

        var pdf = PermitCardPdf.Render(PermitCardView.For(permit, item.Request!.TargetEmployee));
        var fileName = $"parkovaci-povoleni-{SafeFileName(permit.PermitNumber ?? permit.Id.ToString())}.pdf";
        Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
        return File(pdf, "application/pdf");
    }

    private static string SafeFileName(string value)
        => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
