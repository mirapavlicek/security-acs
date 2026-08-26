using System.Globalization;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages.Catalog;

public class FloorPlanModel(AcsDbContext db, AuditService audit) : PageModel
{
    public Floor Floor { get; private set; } = null!;
    public List<Reader> Readers { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var floor = await db.Floors.Include(f => f.Building).FirstOrDefaultAsync(f => f.Id == id);
        if (floor?.SchemaImage is null)
            return RedirectToPage("Places");

        Floor = floor;
        Readers = await LoadReadersAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, int readerId, string x, string y)
    {
        var reader = await db.Readers.Include(r => r.Room).FirstOrDefaultAsync(r => r.Id == readerId);
        if (reader is null || reader.Room?.FloorId != id)
            return NotFound();

        if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
            && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
        {
            reader.SchemaX = Math.Clamp(px, 0, 100);
            reader.SchemaY = Math.Clamp(py, 0, 100);
            await db.SaveChangesAsync();
            await audit.LogAsync(User.Identity?.Name, "reader-position-set", "Reader", readerId.ToString(),
                $"{reader.SchemaX:F1} %, {reader.SchemaY:F1} %");
            Message = $"Pozice čtečky {reader.Name} uložena.";
        }

        return RedirectToPage(new { id });
    }

    private Task<List<Reader>> LoadReadersAsync(int floorId)
        => db.Readers
            .Where(r => r.Room != null && r.Room.FloorId == floorId)
            .OrderBy(r => r.Name)
            .ToListAsync();
}
