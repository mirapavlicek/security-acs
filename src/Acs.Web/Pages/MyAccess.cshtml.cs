using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

/// <summary>
/// „Moje přístupy“ — všechno, čím se přihlášený zaměstnanec prokazuje a kam smí: dveře
/// (WIN-PAK), identifikátory (karty, SPZ…), vjezdy a parkování (vydaná povolení po
/// areálech) a poslední průjezdy nahlášené parkovacím systémem.
/// </summary>
public class MyAccessModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    /// <summary>Kolik posledních vjezdů / výjezdů se zobrazí.</summary>
    public const int RecentEventCount = 10;

    public Employee? Employee { get; private set; }
    public List<AccessRequestItem> ActiveItems { get; private set; } = [];
    public List<AccessRequestItem> PendingItems { get; private set; } = [];

    /// <summary>Identifikátory zaměstnance (karty, SPZ, PIN…) včetně neaktivních — kvůli dohledání.</summary>
    public List<EmployeeIdentifier> Identifiers { get; private set; } = [];

    /// <summary>Vydaná parkovací povolení (položky udělení ve stavu „vydáno“).</summary>
    public List<AccessRequestItem> ParkingItems { get; private set; } = [];

    /// <summary>Parkovací žádosti čekající na schválení / vydání.</summary>
    public List<AccessRequestItem> PendingParkingItems { get; private set; } = [];

    /// <summary>Poslední vjezdy / výjezdy a rozhodnutí u brány z parkovacího systému.</summary>
    public List<IntegrationEvent> RecentGateEvents { get; private set; } = [];

    /// <summary>Areály, do kterých má zaměstnanec právě teď platný vjezd (podle povolení).</summary>
    public List<Site> AccessibleSites { get; private set; } = [];
    public bool AllSitesAccessible { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static string IdentifierTypeName(IdentifierType type) => type switch
    {
        IdentifierType.Card => "Karta",
        IdentifierType.LicensePlate => "SPZ",
        IdentifierType.Pin => "PIN",
        IdentifierType.Tag => "Čip",
        IdentifierType.Biometric => "Biometrie",
        _ => "Jiný",
    };

    public static string GateEventText(IntegrationEvent e) => e.Type switch
    {
        "vehicleIn" => "vjezd",
        "vehicleOut" => "výjezd",
        "denied" => "zamítnuto",
        "passed" => "průjezd",
        "authorizationCheck" => e.Decision == "allow"
            ? (e.Direction == "out" ? "výjezd povolen" : "vjezd povolen")
            : (e.Direction == "out" ? "výjezd zamítnut" : "vjezd zamítnut"),
        _ => e.Type,
    };

    public static string ReasonText(string? reason) => reason switch
    {
        "allowed" => "v pořádku",
        "credentialUnknown" => "SPZ / karta v ACS není",
        "credentialExpired" => "SPZ / karta už neplatí",
        "personEnded" => "zaměstnanec ukončen",
        "noEntitlement" => "bez povolení pro tento areál",
        "entitlementExpired" => "povolení už neplatí",
        "accessPointUnknown" => "neznámý vjezd (chybí párování areálu)",
        null or "" => "",
        _ => reason,
    };

    public async Task OnGetAsync()
    {
        var user = await db.Users.Include(u => u.Employee!).ThenInclude(e => e.Manager)
            .FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        Employee = user?.Employee;
        if (Employee is null)
            return;

        var items = await db.AccessRequestItems
            .Include(i => i.Reader).ThenInclude(r => r!.Room).ThenInclude(room => room!.Floor).ThenInclude(f => f!.Building)
            .Include(i => i.Reader).ThenInclude(r => r!.Room).ThenInclude(room => room!.Corridor)
            .Include(i => i.Reader).ThenInclude(r => r!.Corridor).ThenInclude(c => c!.Floor).ThenInclude(f => f!.Building)
            .Include(i => i.ReaderGroup)
            .Where(i => i.Request!.TargetEmployeeId == Employee.Id && i.Request.Kind == RequestKind.Grant
                        && i.ParkingPermitId == null)
            .ToListAsync();

        ActiveItems = items
            .Where(i => i.Status is RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed)
            .OrderBy(i => i.Reader?.Name)
            .ToList();
        PendingItems = items
            .Where(i => i.Status is RequestStatus.Pending or RequestStatus.Approved)
            .OrderBy(i => i.Reader?.Name)
            .ToList();

        Identifiers = await db.EmployeeIdentifiers
            .Where(i => i.EmployeeId == Employee.Id)
            .OrderBy(i => i.Type).ThenByDescending(i => i.IsActive).ThenBy(i => i.Value)
            .ToListAsync();

        var parking = await db.AccessRequestItems
            .Include(i => i.Request)
            .Include(i => i.Stages)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.PermitType)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Plates)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Sites).ThenInclude(s => s.Site)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.ParkingSpot!).ThenInclude(s => s.Site)
            .Where(i => i.ParkingPermitId != null
                        && i.Request!.TargetEmployeeId == Employee.Id
                        && i.Request.Kind == RequestKind.Grant
                        && (i.Status == RequestStatus.Issued || i.Status == RequestStatus.Pending || i.Status == RequestStatus.Approved))
            .OrderByDescending(i => i.Request!.CreatedAt)
            .ToListAsync();
        ParkingItems = parking.Where(i => i.Status == RequestStatus.Issued).ToList();
        PendingParkingItems = parking.Where(i => i.Status != RequestStatus.Issued).ToList();

        var now = DateTime.UtcNow;
        var currentPermits = ParkingItems.Select(i => i.ParkingPermit!).Where(p => p.IsValidAt(now)).ToList();
        AllSitesAccessible = currentPermits.Any(p => p.AllSites);
        AccessibleSites = AllSitesAccessible
            ? await db.Sites.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync()
            : currentPermits.SelectMany(p => p.Sites).Select(s => s.Site).Where(s => s is not null)
                .DistinctBy(s => s!.Id).OrderBy(s => s!.SortOrder).ThenBy(s => s!.Name).ToList()!;

        RecentGateEvents = await db.IntegrationEvents
            .Include(e => e.Site)
            .Where(e => e.EmployeeId == Employee.Id)
            .OrderByDescending(e => e.OccurredAt)
            .Take(RecentEventCount)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int? readerId, int? groupId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        if (user?.EmployeeId is null)
            return Forbid();

        try
        {
            var request = await workflow.CreateRequestAsync(
                CurrentUserId, user.EmployeeId.Value,
                readerId is null ? [] : [readerId.Value],
                "žádost o odebrání vlastního přístupu", RequestKind.Revoke,
                groupIds: groupId is null ? [] : [groupId.Value]);
            Message = $"Žádost o odebrání podána (#{request.Id}).";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
