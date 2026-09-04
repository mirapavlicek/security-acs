using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

/// <summary>Přehled — dashboard s dlaždicemi podle role a s tím, co na uživatele čeká.</summary>
public class IndexModel(
    AcsDbContext db, SettingsService settings, WinPakClient winPak,
    Acs.Infrastructure.Notifications.AttentionService attention) : PageModel
{
    public Acs.Infrastructure.Notifications.AttentionCounts Attention { get; private set; }
        = new(0, 0, 0);

    // Moje
    public int MyActiveAccesses { get; private set; }
    public int MyActivePermits { get; private set; }
    public bool HasEmployee { get; private set; }

    // Číselníky (CatalogManager)
    public int ReaderCount { get; private set; }
    public int GroupCount { get; private set; }
    public int EmployeeCount { get; private set; }
    public int MatrixCount { get; private set; }
    public int BuildingCount { get; private set; }
    public int SiteCount { get; private set; }
    public int PermitTypeCount { get; private set; }
    public int IssuedPermitCount { get; private set; }

    // Systém (Admin)
    public int UserCount { get; private set; }
    public string WinPakStatus { get; private set; } = "—";

    public bool IsCatalogManager => User.IsInRole("Admin") || User.IsInRole("CatalogManager");
    public bool IsCardAdmin => User.IsInRole("Admin") || User.IsInRole("CardAdmin");
    public bool IsParkingAdmin => User.IsInRole("Admin") || User.IsInRole("ParkingAdmin");
    public bool IsAdmin => User.IsInRole("Admin");

    public async Task OnGetAsync()
    {
        Attention = await attention.GetAsync(User);

        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var employeeId = await db.Users.Where(u => u.Id == userId).Select(u => u.EmployeeId).FirstOrDefaultAsync();
            HasEmployee = employeeId is not null;
            if (employeeId is not null)
            {
                MyActiveAccesses = await db.AccessRequestItems.CountAsync(i =>
                    i.Request!.TargetEmployeeId == employeeId && i.Request.Kind == RequestKind.Grant
                    && i.ParkingPermitId == null
                    && (i.Status == RequestStatus.PushedToWinPak || i.Status == RequestStatus.ManuallyConfirmed));
                MyActivePermits = await db.AccessRequestItems.CountAsync(i =>
                    i.Request!.TargetEmployeeId == employeeId && i.Request.Kind == RequestKind.Grant
                    && i.ParkingPermitId != null && i.Status == RequestStatus.Issued);
            }
        }

        if (IsCatalogManager)
        {
            ReaderCount = await db.Readers.CountAsync(r => r.IsActive);
            GroupCount = await db.ReaderGroups.CountAsync(g => g.IsActive);
            EmployeeCount = await db.Employees.CountAsync(e => e.IsActive);
            MatrixCount = await db.ApprovalMatrices.CountAsync(m => m.IsActive);
            BuildingCount = await db.Buildings.CountAsync();
            SiteCount = await db.Sites.CountAsync(s => s.IsActive);
            PermitTypeCount = await db.ParkingPermitTypes.CountAsync(t => t.IsActive);
            IssuedPermitCount = await db.AccessRequestItems.CountAsync(i =>
                i.ParkingPermitId != null && i.Status == RequestStatus.Issued);
        }

        if (!IsAdmin)
            return;

        UserCount = await db.Users.CountAsync(u => u.IsActive);

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
