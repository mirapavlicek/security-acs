using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Admin;

public class AuditModel(AcsDbContext db) : PageModel
{
    public List<AuditLog> Logs { get; private set; } = [];

    public async Task OnGetAsync()
        => Logs = await db.AuditLogs.OrderByDescending(l => l.At).Take(200).ToListAsync();
}
