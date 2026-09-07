using Acs.Infrastructure.Data;
using Acs.Infrastructure.Pdf;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Parking;

/// <summary>
/// Cedule A4 „Vyhrazené parkování“ na parkovací místo jako PDF — pro správce parkování a správce
/// číselníků. Jedno místo (<c>/Parking/SpotSign/{id}</c>) nebo všechna aktivní místa areálu
/// (<c>/Parking/SpotSign?siteId=</c>), jedno místo = jedna stránka.
/// </summary>
[Authorize(Policy = "ParkingSignPrinter")]
public class SpotSignModel(AcsDbContext db, ParkingAdminService parkingAdmin) : PageModel
{
    public async Task<IActionResult> OnGetAsync(int? id, int? siteId)
    {
        if (id is { } spotId)
        {
            var sign = await parkingAdmin.GetSpotSignAsync(spotId);
            if (sign is null)
                return NotFound();

            var pdf = ParkingSpotSignPdf.Render(sign);
            Response.Headers.ContentDisposition =
                $"inline; filename=\"vyhrazene-parkovani-{SafeFileName(sign.SiteName)}-{SafeFileName(sign.SpotCode)}.pdf\"";
            return File(pdf, "application/pdf");
        }

        if (siteId is { } site)
        {
            var siteName = await db.Sites.Where(s => s.Id == site).Select(s => s.Name).FirstOrDefaultAsync();
            if (siteName is null)
                return NotFound();

            var signs = await parkingAdmin.GetSiteSignsAsync(site);
            if (signs.Count == 0)
                return BadRequest("Areál nemá žádné aktivní parkovací místo.");

            var pdf = ParkingSpotSignPdf.Render(signs);
            Response.Headers.ContentDisposition =
                $"inline; filename=\"vyhrazene-parkovani-{SafeFileName(siteName)}.pdf\"";
            return File(pdf, "application/pdf");
        }

        return BadRequest("Zadejte parkovací místo (id) nebo areál (siteId).");
    }

    private static string SafeFileName(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var ascii = new string(normalized
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        return string.Concat(ascii.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')).Trim('-');
    }
}
