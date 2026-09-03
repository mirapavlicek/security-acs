using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public class IndexModel(
    AcsDbContext db, SettingsService settings, WinPakClient winPak,
    Acs.Infrastructure.Notifications.AttentionService attention) : PageModel
{
    public Acs.Infrastructure.Notifications.AttentionCounts Attention { get; private set; }
        = new(0, 0, 0);

    public int UserCount { get; private set; }
    public int EmployeeCount { get; private set; }
    public int ReaderCount { get; private set; }
    public string WinPakStatus { get; private set; } = "—";

    public async Task OnGetAsync()
    {
        Attention = await attention.GetAsync(User);

        if (!User.IsInRole("Admin"))
            return;

        UserCount = await db.Users.CountAsync();
        EmployeeCount = await db.Employees.CountAsync();
        ReaderCount = await db.Readers.CountAsync(r => r.IsActive);

        if (await settings.GetAsync(SettingKeys.WinPakBaseUrl) is null)
        {
            WinPakStatus = "nenakonfigurován";
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var info = await winPak.GetInfoAsync(cts.Token);
            WinPakStatus = info is null
                ? "nedostupný"
                : $"online ({info.ProviderMode}{(info.SupportsWrite ? ", zápis" : ", jen čtení")})";
        }
        catch
        {
            WinPakStatus = "nedostupný";
        }
    }
}
