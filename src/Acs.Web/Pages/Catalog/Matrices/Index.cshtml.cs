using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog.Matrices;

public class IndexModel(AcsDbContext db, AuditService audit, SettingsService settings, MatrixTemplateService templates) : PageModel
{
    /// <summary>Matice pro žádosti o kamery / EZS (null = výchozí matice, jinak administrátor).</summary>
    public ApprovalMatrix? SecurityMatrix { get; private set; }

    /// <summary>Uživatelé pro výběr vedoucího OVBKŘ v průvodci.</summary>
    public List<AppUser> Users { get; private set; } = [];

    /// <summary>Matice podle dokumentu FN Motol už existují?</summary>
    public bool FnmTemplateExists { get; private set; }

    public List<ApprovalMatrix> Matrices { get; private set; } = [];
    public Dictionary<int, int> UsageCounts { get; private set; } = new();
    public Dictionary<int, int> GroupUsageCounts { get; private set; } = new();

    /// <summary>Čtečky, které zatím nemají matici — schvaluje je výchozí matice, jinak administrátor.</summary>
    public int ReadersWithoutMatrix { get; private set; }

    /// <summary>Výchozí matice pro položky bez vlastní matice (null = rozhoduje administrátor).</summary>
    public ApprovalMatrix? DefaultMatrix { get; private set; }

    /// <summary>Existuje už matice tvořená jen schvalovatelem „nadřízený zaměstnance“?</summary>
    public ApprovalMatrix? ManagerMatrix { get; private set; }

    public int EmployeesWithManager { get; private set; }
    public int EmployeesActive { get; private set; }

    public const string ManagerMatrixName = "Nadřízený zaměstnance";

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        Matrices = await db.ApprovalMatrices.Include(m => m.Levels).OrderBy(m => m.Name).ToListAsync();
        UsageCounts = await db.Readers
            .Where(r => r.ApprovalMatrixId != null)
            .GroupBy(r => r.ApprovalMatrixId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        GroupUsageCounts = await db.ReaderGroups
            .Where(g => g.ApprovalMatrixId != null)
            .GroupBy(g => g.ApprovalMatrixId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        ReadersWithoutMatrix = await db.Readers.CountAsync(r => r.ApprovalMatrixId == null);
        DefaultMatrix = Matrices.FirstOrDefault(m => m.IsDefault);
        ManagerMatrix = await db.ApprovalMatrices
            .Where(m => m.Levels.Count == 1
                        && m.Levels.All(l => l.Approvers.Count == 1
                                             && l.Approvers.All(a => a.Kind == ApproverKind.LineManager && a.ManagerDepth == 1)))
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync();
        EmployeesActive = await db.Employees.CountAsync(e => e.IsActive);
        EmployeesWithManager = await db.Employees.CountAsync(e => e.IsActive && e.ManagerId != null);

        var securityMatrixId = await settings.GetIntAsync(SettingKeys.SecurityMatrixId, 0);
        SecurityMatrix = securityMatrixId > 0 ? Matrices.FirstOrDefault(m => m.Id == securityMatrixId) : null;
        Users = await db.Users.Where(u => u.IsActive).OrderBy(u => u.DisplayName ?? u.UserName).ToListAsync();
        FnmTemplateExists = Matrices.Any(m => m.Name == MatrixTemplateService.AccessName);
    }

    /// <summary>Matice pro žádosti o kamery / EZS.</summary>
    public async Task<IActionResult> OnPostSetSecurityAsync(int? matrixId)
    {
        if (matrixId is not null && !await db.ApprovalMatrices.AnyAsync(m => m.Id == matrixId))
            return NotFound();

        await settings.SetAsync(SettingKeys.SecurityMatrixId, matrixId?.ToString() ?? "", User.Identity?.Name);
        await audit.LogAsync(User.Identity?.Name, "security-matrix-changed", "ApprovalMatrix", matrixId?.ToString(), null);
        Message = matrixId is null
            ? "Žádosti o kamery / EZS schvaluje výchozí matice (bez ní administrátor)."
            : "Matice pro kamery / EZS nastavena.";
        return RedirectToPage();
    }

    /// <summary>Založí matice podle schvalovací matice FN Motol (EKV, parkování, kamery / EZS).</summary>
    public async Task<IActionResult> OnPostCreateFnmAsync(int? ovbkrUserId)
    {
        var result = await templates.CreateFnmAsync(ovbkrUserId, User.Identity?.Name);
        await settings.SetAsync(SettingKeys.SecurityMatrixId, result.Security.Id.ToString(), User.Identity?.Name);
        Message = $"Založeno: „{result.Access.Name}“ (výchozí), „{result.Parking.Name}“ (přiřazena {result.PermitTypesAssigned} druhům povolení), „{result.Security.Name}“ (kamery / EZS)."
                  + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "");
        return RedirectToPage();
    }

    /// <summary>Nastaví (nebo zruší, <paramref name="matrixId"/> = null) výchozí matici — nejvýše jedna.</summary>
    public async Task<IActionResult> OnPostSetDefaultAsync(int? matrixId)
    {
        var matrices = await db.ApprovalMatrices.Include(m => m.Levels).ToListAsync();
        var target = matrixId is null ? null : matrices.FirstOrDefault(m => m.Id == matrixId);
        if (matrixId is not null && target is null)
            return NotFound();
        if (target is { Levels.Count: 0 })
        {
            Message = $"Matice „{target.Name}“ nemá žádnou úroveň — nejdřív ji doplňte.";
            return RedirectToPage();
        }

        foreach (var matrix in matrices)
            matrix.IsDefault = target is not null && matrix.Id == target.Id;
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "matrix-default-changed", "ApprovalMatrix",
            target?.Id.ToString(), target?.Name ?? "zrušena");
        Message = target is null
            ? "Výchozí matice zrušena — položky bez matice rozhoduje administrátor."
            : $"Výchozí matice: „{target.Name}“ — platí pro čtečky, skupiny i parkovací povolení bez vlastní matice.";
        return RedirectToPage();
    }

    /// <summary>
    /// Jedním klikem: matice „Nadřízený zaměstnance“ (jedna úroveň, schvalovatel = přímý
    /// nadřízený) nastavená jako výchozí. Tím SPZ i karty schvalují nadřízení bez toho,
    /// aby se matice musela přiřazovat ke každé čtečce a druhu povolení zvlášť.
    /// </summary>
    public async Task<IActionResult> OnPostCreateManagerDefaultAsync()
    {
        var existing = await db.ApprovalMatrices
            .Include(m => m.Levels)
            .Where(m => m.Levels.Count == 1
                        && m.Levels.All(l => l.Approvers.Count == 1
                                             && l.Approvers.All(a => a.Kind == ApproverKind.LineManager && a.ManagerDepth == 1)))
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync();

        if (existing is null)
        {
            existing = new ApprovalMatrix
            {
                Name = ManagerMatrixName,
                Description = "Schvaluje přímý nadřízený cílového zaměstnance (z AD).",
                Levels =
                [
                    new ApprovalLevel
                    {
                        Order = 1, Mode = ApprovalMode.Any, Name = "Nadřízený",
                        Approvers = [new Approver { Kind = ApproverKind.LineManager, ManagerDepth = 1 }],
                    },
                ],
            };
            db.ApprovalMatrices.Add(existing);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "matrix-created", "ApprovalMatrix", existing.Id.ToString(), existing.Name);
        }

        existing.IsActive = true;
        return await OnPostSetDefaultAsync(existing.Id);
    }

    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        var matrix = new ApprovalMatrix { Name = name.Trim() };
        db.ApprovalMatrices.Add(matrix);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "matrix-created", "ApprovalMatrix", matrix.Id.ToString(), name);
        return RedirectToPage("Edit", new { id = matrix.Id });
    }
}
