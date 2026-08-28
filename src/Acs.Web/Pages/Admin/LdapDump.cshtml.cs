using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Acs.Web.Pages.Admin;

/// <summary>
/// Výpis toho, co Active Directory o účtu vrací. Slouží k dohledání, odkud brát
/// osobní číslo — každé AD ho má v jiném atributu.
/// </summary>
public class LdapDumpModel(LdapDiagnosticsService diagnostics, AuditService audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }

    public LdapDumpResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
            return;

        try
        {
            Result = await diagnostics.DumpAsync(Query);
            // Výpis obsahuje osobní údaje, proto se dotaz zaznamenává.
            await audit.LogAsync(User.Identity?.Name, "ldap-dump", "Employee", null,
                $"dotaz „{Query.Trim()}“ — nalezeno účtů {Result.Entries.Count}");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
