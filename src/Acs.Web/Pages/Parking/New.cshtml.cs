using System.Globalization;
using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Parking;

public class NewModel(AcsDbContext db, RequestWorkflowService workflow) : PageModel
{
    public List<Employee> Employees { get; private set; } = [];
    public List<ParkingPermitType> Types { get; private set; } = [];
    public List<Site> Sites { get; private set; } = [];
    public int? MyEmployeeId { get; private set; }
    public string? MyEmployeeName { get; private set; }
    public bool CanActForOthers { get; private set; }
    public int MaxPlateInputs => Math.Clamp(Types.Count == 0 ? 1 : Types.Max(t => t.MaxPlates), 1, 10);

    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool ComputeCanActForOthers()
        => User.IsInRole("Admin") || User.IsInRole("CardAdmin") || User.IsInRole("CatalogManager") || User.IsInRole("ParkingAdmin");

    public async Task OnGetAsync()
    {
        CanActForOthers = ComputeCanActForOthers();

        var user = await db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        MyEmployeeId = user?.EmployeeId;
        MyEmployeeName = user?.Employee?.FullName;

        Employees = CanActForOthers
            ? await db.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync()
            : [];

        Types = await db.ParkingPermitTypes.Where(t => t.IsActive)
            .Include(t => t.ApprovalMatrix)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
        Sites = await db.Sites.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync(int? targetEmployeeId, int? permitTypeId, string? allSites,
        int[] siteIds, string[] plates, string? functionTitle, string? validFrom, string? validTo, string? justification)
    {
        if (targetEmployeeId is null || permitTypeId is null)
        {
            ErrorMessage = "Vyberte zaměstnance a druh povolení.";
            return RedirectToPage();
        }

        var input = new ParkingRequestInput(
            PermitTypeId: permitTypeId.Value,
            AllSites: allSites == "true",
            SiteIds: siteIds,
            Plates: plates,
            FunctionTitle: functionTitle,
            ValidFrom: ParseDate(validFrom),
            ValidTo: ParseDate(validTo),
            Justification: string.IsNullOrWhiteSpace(justification) ? null : justification.Trim());

        try
        {
            var request = await workflow.CreateParkingRequestAsync(
                CurrentUserId, targetEmployeeId.Value, input, requesterCanActForOthers: ComputeCanActForOthers());
            return RedirectToPage("/Requests/Detail", new { id = request.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }
    }

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
