using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Security;

/// <summary>Fronta realizace ICT: schválené žádosti o kamery / EZS.</summary>
[Authorize(Policy = "IctAdmin")]
public class QueueModel(SecurityAdminService security) : PageModel
{
    public List<AccessRequestItem> Queue { get; private set; } = [];
    public List<AccessRequestItem> Implemented { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        Queue = await security.GetQueueAsync();
        Implemented = await security.GetImplementedAsync(Search);
    }

    public async Task<IActionResult> OnPostImplementAsync(int itemId, string? note)
    {
        try
        {
            await security.ImplementAsync(itemId, CurrentUserId, note, User.Identity?.Name);
            Message = "Požadavek označen jako realizovaný.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { Search });
    }

    public async Task<IActionResult> OnPostRejectAsync(int itemId, string? reason)
    {
        try
        {
            await security.RejectAsync(itemId, CurrentUserId, reason ?? "", User.Identity?.Name);
            Message = "Požadavek zamítnut jako nerealizovatelný.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage(new { Search });
    }
}
