using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>Výsledek založení matic podle schvalovací matice FN Motol.</summary>
public record MatrixTemplateResult(ApprovalMatrix Access, ApprovalMatrix Parking, ApprovalMatrix Security,
    int PermitTypesAssigned, IReadOnlyList<string> Notes);

/// <summary>
/// Předpřipravené matice podle dokumentu „Schvalovací matice – EKV, parkování, kamery, EZS“
/// (FN Motol). Zakládá se jedním klikem; existující matice stejného názvu se nepřepisují.
/// </summary>
public class MatrixTemplateService(AcsDbContext db, AuditService audit)
{
    public const string AccessName = "EKV – vstupní oprávnění (FN Motol)";
    public const string ParkingName = "Parkování – vjezd do nemocnice (FN Motol)";
    public const string SecurityName = "Kamery / EZS – zřízení a rozšíření (FN Motol)";

    /// <summary>
    /// Založí (nebo najde) tři matice:
    /// <list type="bullet">
    ///   <item><b>EKV</b> (výchozí, bez platné úrovně = bez stupně): 1. nadřízený – řadový + vedoucí;
    ///     2. nadřízený – přímo pod ředitelem, jen mimo úsek (= ředitel); 3. odpovědná osoba cílového
    ///     úseku – řadový, jen mimo úsek; 4. vedoucí OVBKŘ – řadový + vedoucí, jen mimo úsek.
    ///     Ředitel a náměstek ve vlastním úseku → bez úrovně → realizace.</item>
    ///   <item><b>Parkování</b> (bez platné úrovně = bez stupně): 1. nadřízený – řadový + vedoucí;
    ///     přiřadí se druhům povolení bez matice. Náměstek / ředitel → přímo úsek parkování.</item>
    ///   <item><b>Kamery / EZS</b>: 1. vedoucí OVBKŘ (žádá-li sám, rozhoduje jeho nadřízený).</item>
    /// </list>
    /// <paramref name="ovbkrUserId"/> = účet vedoucího OVBKŘ; bez něj zůstanou úrovně OVBKŘ bez
    /// schvalovatele (rozhodne administrátor) a doplní se v editoru matice.
    /// </summary>
    public async Task<MatrixTemplateResult> CreateFnmAsync(int? ovbkrUserId, string? userName, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (ovbkrUserId is not null && !await db.Users.AnyAsync(u => u.Id == ovbkrUserId && u.IsActive, ct))
        {
            ovbkrUserId = null;
            notes.Add("Vybraný účet vedoucího OVBKŘ neexistuje — úrovně OVBKŘ zůstaly bez schvalovatele.");
        }

        Approver? Ovbkr() => ovbkrUserId is { } uid ? new Approver { Kind = ApproverKind.User, UserId = uid } : null;
        static Approver Manager() => new() { Kind = ApproverKind.LineManager, ManagerDepth = 1 };
        static Approver Owner() => new() { Kind = ApproverKind.AreaOwner };

        var access = await FindOrCreateAsync(AccessName, ct, () => new ApprovalMatrix
        {
            Name = AccessName,
            Description = "Řadový / vedoucí: nadřízený; mimo vlastní úsek navíc odpovědná osoba cílového úseku (jen řadový) a vedoucí OVBKŘ; přímo pod ředitelem mimo svůj úsek: ředitel; ředitel a náměstek ve svém úseku bez schvalování.",
            AutoApproveWhenNoLevels = true,
            Levels =
            [
                new ApprovalLevel
                {
                    Order = 1, Name = "Nadřízený zaměstnance", Mode = ApprovalMode.Any,
                    AppliesToRanks = RankFlags.Staff | RankFlags.Manager, Scope = LevelScope.Always,
                    Approvers = [Manager()],
                },
                new ApprovalLevel
                {
                    Order = 2, Name = "Ředitel (nadřízený náměstka)", Mode = ApprovalMode.Any,
                    AppliesToRanks = RankFlags.Executive, Scope = LevelScope.OutsideOwnUnit,
                    Approvers = [Manager()],
                },
                new ApprovalLevel
                {
                    Order = 3, Name = "Odpovědná osoba cílového úseku", Mode = ApprovalMode.Any,
                    AppliesToRanks = RankFlags.Staff, Scope = LevelScope.OutsideOwnUnit,
                    Approvers = [Owner()],
                },
                new ApprovalLevel
                {
                    Order = 4, Name = "Vedoucí OVBKŘ", Mode = ApprovalMode.Any,
                    AppliesToRanks = RankFlags.Staff | RankFlags.Manager, Scope = LevelScope.OutsideOwnUnit,
                    Approvers = Ovbkr() is { } a ? [a] : [],
                },
            ],
        }, notes, userName);

        var parking = await FindOrCreateAsync(ParkingName, ct, () => new ApprovalMatrix
        {
            Name = ParkingName,
            Description = "Řadový / vedoucí: nadřízený → úsek parkování; náměstek, vedoucí přímo pod ředitelem a ředitel: přímo úsek parkování.",
            AutoApproveWhenNoLevels = true,
            Levels =
            [
                new ApprovalLevel
                {
                    Order = 1, Name = "Nadřízený zaměstnance", Mode = ApprovalMode.Any,
                    AppliesToRanks = RankFlags.Staff | RankFlags.Manager, Scope = LevelScope.Always,
                    Approvers = [Manager()],
                },
            ],
        }, notes, userName);

        var security = await FindOrCreateAsync(SecurityName, ct, () => new ApprovalMatrix
        {
            Name = SecurityName,
            Description = "Zřízení / rozšíření kamer nebo EZS: vedoucí OVBKŘ → realizace ICT. Žádá-li vedoucí OVBKŘ sám, rozhoduje jeho nadřízený (provozně-technický náměstek).",
            Levels =
            [
                new ApprovalLevel
                {
                    Order = 1, Name = "Vedoucí OVBKŘ", Mode = ApprovalMode.Any,
                    Approvers = Ovbkr() is { } a ? [a] : [],
                },
            ],
        }, notes, userName);

        // EKV matice jako výchozí (čtečky a skupiny bez vlastní matice).
        var matrices = await db.ApprovalMatrices.ToListAsync(ct);
        foreach (var matrix in matrices)
            matrix.IsDefault = matrix.Id == access.Id;
        access.IsActive = true;

        // Parkovací matice druhům povolení bez matice.
        var permitTypes = await db.ParkingPermitTypes.Where(t => t.ApprovalMatrixId == null).ToListAsync(ct);
        foreach (var type in permitTypes)
            type.ApprovalMatrixId = parking.Id;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "matrix-template-fnm", "ApprovalMatrix", access.Id.ToString(),
            $"EKV #{access.Id} (výchozí), parkování #{parking.Id} ({permitTypes.Count} druhů), kamery/EZS #{security.Id}", ct);

        if (ovbkrUserId is null)
            notes.Add("Doplňte schvalovatele „vedoucí OVBKŘ“ do úrovně 4 matice EKV a úrovně 1 matice Kamery / EZS.");

        return new MatrixTemplateResult(access, parking, security, permitTypes.Count, notes);
    }

    private async Task<ApprovalMatrix> FindOrCreateAsync(string name, CancellationToken ct,
        Func<ApprovalMatrix> factory, List<string> notes, string? userName)
    {
        var existing = await db.ApprovalMatrices.Include(m => m.Levels)
            .FirstOrDefaultAsync(m => m.Name == name, ct);
        if (existing is not null)
        {
            notes.Add($"Matice „{name}“ už existuje — ponechána beze změny.");
            return existing;
        }

        var matrix = factory();
        db.ApprovalMatrices.Add(matrix);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(userName, "matrix-created", "ApprovalMatrix", matrix.Id.ToString(), matrix.Name, ct);
        return matrix;
    }
}
