using Acs.Domain.Entities;

namespace Acs.Infrastructure.Pdf;

/// <summary>Data pro vykreslení kartičky parkovacího povolení za čelní sklo (partial `_PermitCard`).</summary>
public record PermitCardView(
    string Title,
    string TypeName,
    string ScopeText,
    string? Plates,
    string? FunctionTitle,
    string? PermitNumber,
    DateTime? ValidTo,
    string? HolderName)
{
    public const string DefaultTitle = "POVOLENÍ K PARKOVÁNÍ";

    /// <summary>Ukázka pro náhled v číselníku druhů (bez konkrétního povolení).</summary>
    public static PermitCardView Sample(ParkingPermitType type) => new(
        Title: string.IsNullOrWhiteSpace(type.CardTitle) ? DefaultTitle : type.CardTitle,
        TypeName: type.Name,
        ScopeText: string.IsNullOrWhiteSpace(type.CardScopeText) ? "Platí pro areály: …" : type.CardScopeText,
        Plates: type.Binding == PermitBinding.LicensePlate ? "1AB 2345" : null,
        FunctionTitle: null,
        PermitNumber: "P-0000-0000",
        ValidTo: null,
        HolderName: null);

    /// <summary>Kartička konkrétního povolení (vyžaduje načtený druh, areály a SPZ).</summary>
    public static PermitCardView For(ParkingPermit permit, Employee? holder)
    {
        var type = permit.PermitType!;
        var scope = !string.IsNullOrWhiteSpace(type.CardScopeText)
            ? type.CardScopeText
            : permit.AllSites
                ? "Platí pro všechna pracoviště FNMH (všechny areály)"
                : $"Platí pro areál: {string.Join(", ", permit.Sites.Select(s => s.Site?.Name ?? "?"))}";

        return new PermitCardView(
            Title: string.IsNullOrWhiteSpace(type.CardTitle) ? DefaultTitle : type.CardTitle,
            TypeName: type.Name,
            ScopeText: scope,
            Plates: permit.Plates.Count > 0 ? string.Join("  ·  ", permit.Plates.Select(p => p.Value)) : null,
            FunctionTitle: type.Binding == PermitBinding.Function ? permit.FunctionTitle : null,
            PermitNumber: permit.PermitNumber,
            ValidTo: permit.ValidTo,
            HolderName: type.Binding == PermitBinding.Function ? holder?.FullName : null);
    }
}
