using Acs.Domain.Entities;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages;

[Authorize(Policy = "CardAdmin")]
public class CardQueueModel(CardAdminService cardAdmin, SettingsService settings, WinPakClient winPak) : PageModel
{
    public List<AccessRequestItem> Queue { get; private set; } = [];
    public string ConnectorStatus { get; private set; } = "—";
    public bool CanPush { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Queue = await cardAdmin.GetQueueAsync();

        if (await settings.GetAsync(SettingKeys.WinPakBaseUrl) is null)
        {
            ConnectorStatus = "nenakonfigurován — je možné jen ruční potvrzení";
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var info = await winPak.GetInfoAsync(cts.Token);
            if (info is null)
            {
                ConnectorStatus = "nedostupný";
            }
            else if (info.SupportsWrite)
            {
                ConnectorStatus = $"online ({info.ProviderMode}, zápis povolen)";
                CanPush = true;
            }
            else
            {
                ConnectorStatus = $"online ({info.ProviderMode}, jen čtení — použijte ruční potvrzení)";
            }
        }
        catch
        {
            ConnectorStatus = "nedostupný";
        }
    }

    public async Task<IActionResult> OnPostPushAsync(int itemId)
    {
        try
        {
            await cardAdmin.PushAsync(itemId, User.Identity?.Name);
            Message = "Předáno do WIN-PAK.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Předání selhalo: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAsync(int itemId)
    {
        try
        {
            await cardAdmin.ConfirmManualAsync(itemId, User.Identity?.Name);
            Message = "Potvrzeno jako ručně zadané.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
