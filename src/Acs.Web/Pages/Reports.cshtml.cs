using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Acs.Web.Pages;

public record AccessReportRow(
    string ReaderName, string Location, string EmployeeName,
    string? Department, string? CardNumber, DateTime? Since);

[Authorize(Policy = "CatalogManager")]
public class ReportsModel(AcsDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "byReader";

    public List<AccessReportRow> Rows { get; private set; } = [];
    public IEnumerable<IGrouping<string, AccessReportRow>> ByReader
        => Rows.GroupBy(r => r.ReaderName).OrderBy(g => g.Key);
    public IEnumerable<IGrouping<string, AccessReportRow>> ByEmployee
        => Rows.GroupBy(r => r.EmployeeName).OrderBy(g => g.Key);

    public async Task OnGetAsync() => Rows = await LoadAsync();

    public async Task<IActionResult> OnGetCsvAsync()
    {
        Rows = await LoadAsync();
        var csv = new StringBuilder("Ctecka;Umisteni;Zamestnanec;Oddeleni;Karta;PristupOd\n");
        foreach (var row in Rows.OrderBy(r => r.ReaderName).ThenBy(r => r.EmployeeName))
        {
            csv.AppendLine(string.Join(';',
                Escape(row.ReaderName), Escape(row.Location), Escape(row.EmployeeName),
                Escape(row.Department), Escape(row.CardNumber),
                row.Since?.ToString("yyyy-MM-dd")));
        }

        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv", $"acs-pristupy-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    private static string Escape(string? value)
        => value is null ? "" : value.Contains(';') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private async Task<List<AccessReportRow>> LoadAsync()
    {
        var items = await db.AccessRequestItems
            .Include(i => i.Reader!).ThenInclude(r => r.Room!).ThenInclude(room => room.Floor!).ThenInclude(f => f.Building)
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Where(i => i.Request!.Kind == RequestKind.Grant
                        && (i.Status == RequestStatus.PushedToWinPak
                            || i.Status == RequestStatus.ManuallyConfirmed))
            .ToListAsync();

        return items.Select(i => new AccessReportRow(
                ReaderName: i.Reader!.Name,
                Location: i.Reader.Room is null
                    ? "—"
                    : $"{i.Reader.Room.Floor?.Building?.Name} / {i.Reader.Room.Floor?.Name} / {i.Reader.Room.Name}",
                EmployeeName: i.Request!.TargetEmployee!.FullName,
                Department: i.Request.TargetEmployee.Department,
                CardNumber: i.Request.TargetEmployee.CardNumber,
                Since: i.PushedAt))
            .ToList();
    }
}
