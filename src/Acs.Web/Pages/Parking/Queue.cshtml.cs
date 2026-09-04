using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Pdf;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Parking;

[Authorize(Policy = "ParkingAdmin")]
public class QueueModel(ParkingAdminService parkingAdmin) : PageModel
{
    public List<AccessRequestItem> Queue { get; private set; } = [];
    public List<AccessRequestItem> Issued { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        Queue = await parkingAdmin.GetQueueAsync();
        Issued = await parkingAdmin.GetIssuedAsync(Search);
    }

    /// <summary>Kartičky všech vydaných povolení (dle filtru) v jednom PDF — jedna kartička na stránku.</summary>
    public async Task<IActionResult> OnGetCardsAsync()
    {
        var issued = await parkingAdmin.GetIssuedAsync(Search);
        var cards = issued
            .Where(i => i.ParkingPermit!.PermitType?.PrintsWindshieldCard == true)
            .Select(i => PermitCardView.For(i.ParkingPermit!, i.Request!.TargetEmployee))
            .ToList();
        if (cards.Count == 0)
        {
            ErrorMessage = "Žádné vydané povolení s kartičkou k vygenerování.";
            return RedirectToPage(new { Search });
        }

        var pdf = PermitCardPdf.Render(cards);
        Response.Headers.ContentDisposition = $"inline; filename=\"parkovaci-karticky-{DateTime.UtcNow:yyyyMMdd}.pdf\"";
        return File(pdf, "application/pdf");
    }

    public async Task<IActionResult> OnPostIssueAsync(int itemId, string? permitNumber)
    {
        try
        {
            await parkingAdmin.IssueAsync(itemId, CurrentUserId, permitNumber, User.Identity?.Name);
            Message = "Povolení vydáno — můžete vytisknout kartičku.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { Search });
    }

    public async Task<IActionResult> OnPostConfirmRevokeAsync(int itemId, string? reason)
    {
        try
        {
            await parkingAdmin.ConfirmRevokeAsync(itemId, reason, User.Identity?.Name);
            Message = "Povolení odebráno.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { Search });
    }

    public async Task<IActionResult> OnPostRevokeAsync(int permitId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = "U odebrání uveďte důvod.";
            return RedirectToPage(new { Search });
        }

        try
        {
            await parkingAdmin.RevokeAsync(permitId, CurrentUserId, reason.Trim(), User.Identity?.Name);
            Message = "Povolení odebráno.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { Search });
    }
}
