using System.Security.Claims;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public class DeputiesModel(AcsDbContext db, AuditService audit) : PageModel
{
    public List<Deputy> Deputies { get; private set; } = [];
    public List<AppUser> Users { get; private set; } = [];

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        var query = db.Deputies
            .Include(d => d.PrincipalUser)
            .Include(d => d.DeputyUser)
            .AsQueryable();

        if (!User.IsInRole("Admin"))
        {
            var userId = CurrentUserId;
            query = query.Where(d => d.PrincipalUserId == userId || d.DeputyUserId == userId);
        }

        Deputies = await query.OrderByDescending(d => d.ValidTo).ToListAsync();
        Users = await db.Users.Where(u => u.IsActive).OrderBy(u => u.UserName).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        int? principalUserId, int deputyUserId, DateTime validFrom, DateTime validTo, string? note)
    {
        // Ne-admin smí založit zástup jen sám za sebe.
        var principal = User.IsInRole("Admin") && principalUserId is not null
            ? principalUserId.Value
            : CurrentUserId;

        if (principal == deputyUserId)
        {
            ErrorMessage = "Zástup nemůže být totožný se zastupovaným.";
            return RedirectToPage();
        }

        if (validTo < validFrom)
        {
            ErrorMessage = "Datum „do“ musí být po datu „od“.";
            return RedirectToPage();
        }

        db.Deputies.Add(new Deputy
        {
            PrincipalUserId = principal,
            DeputyUserId = deputyUserId,
            ValidFrom = validFrom.Date,
            ValidTo = validTo.Date.AddDays(1).AddSeconds(-1),
            Note = note,
        });
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "deputy-created", "Deputy", null,
            $"{principal} zastupuje {deputyUserId} ({validFrom:d}–{validTo:d})");
        Message = "Zástup vytvořen.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int deputyId)
    {
        var deputy = await db.Deputies.FindAsync(deputyId);
        if (deputy is null)
            return NotFound();

        if (!User.IsInRole("Admin") && deputy.PrincipalUserId != CurrentUserId)
            return Forbid();

        db.Deputies.Remove(deputy);
        await db.SaveChangesAsync();
        await audit.LogAsync(User.Identity?.Name, "deputy-deleted", "Deputy", deputyId.ToString());
        Message = "Zástup zrušen.";
        return RedirectToPage();
    }
}
