using Acs.Domain.Entities;

namespace Acs.Infrastructure.Pdf;

/// <summary>Jeden řádek na ceduli: registrační značka (s EU pruhem), nebo název funkce u přenosného povolení.</summary>
public record ParkingSignRow(string Text, bool IsPlate)
{
    public static ParkingSignRow Plate(string value) => new(FormatPlate(value), true);
    public static ParkingSignRow Function(string title) => new(title, false);

    /// <summary>Normalizovanou SPZ („8AN5201“) rozdělí mezerou po třetím znaku („8AN 5201“) jako na skutečné značce.</summary>
    public static string FormatPlate(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Length < 5 || compact.Length > 8)
            return value.Trim().ToUpperInvariant();
        return $"{compact[..3]} {compact[3..]}";
    }
}

/// <summary>Data pro ceduli A4 „Vyhrazené parkování“ na konkrétní parkovací místo.</summary>
public record ParkingSpotSignView(
    string SpotCode,
    string SiteName,
    string? Location,
    IReadOnlyList<ParkingSignRow> Rows)
{
    /// <summary>„Místo A-12 · areál Motol“ (+ umístění, je-li vyplněno).</summary>
    public string SpotText()
    {
        var text = $"Místo {SpotCode} · areál {SiteName}";
        return string.IsNullOrWhiteSpace(Location) ? text : $"{text} · {Location}";
    }

    /// <summary>Cedule pro místo s danými povoleními (jen SPZ, resp. funkce; duplicitní SPZ se sloučí).</summary>
    public static ParkingSpotSignView For(ParkingSpot spot, IEnumerable<ParkingPermit> permits)
    {
        var rows = new List<ParkingSignRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var permit in permits)
        {
            if (permit.Plates.Count > 0)
            {
                foreach (var plate in permit.Plates)
                {
                    if (seen.Add(plate.Value))
                        rows.Add(ParkingSignRow.Plate(plate.Value));
                }
            }
            else if (!string.IsNullOrWhiteSpace(permit.FunctionTitle) && seen.Add("fn:" + permit.FunctionTitle))
            {
                rows.Add(ParkingSignRow.Function(permit.FunctionTitle));
            }
        }

        return new ParkingSpotSignView(spot.Code, spot.Site?.Name ?? "?", spot.Location, rows);
    }

    /// <summary>Ukázka pro náhled (bez konkrétního místa).</summary>
    public static ParkingSpotSignView Sample() => new(
        "A-12", "Motol", null,
        [ParkingSignRow.Plate("8AN5201"), ParkingSignRow.Plate("6AT1765")]);
}
