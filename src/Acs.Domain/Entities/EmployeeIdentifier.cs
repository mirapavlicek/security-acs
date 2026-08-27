namespace Acs.Domain.Entities;

/// <summary>Druh identifikátoru, kterým se zaměstnanec prokazuje.</summary>
public enum IdentifierType
{
    /// <summary>Přístupová karta (číslo karty).</summary>
    Card = 0,
    /// <summary>SPZ vozidla (vjezd do areálu, parkoviště).</summary>
    LicensePlate = 1,
    /// <summary>PIN kód.</summary>
    Pin = 2,
    /// <summary>Čip / přívěsek.</summary>
    Tag = 3,
    /// <summary>Biometrie (otisk, obličej) — evidenční záznam.</summary>
    Biometric = 4,
    /// <summary>Jiný identifikátor.</summary>
    Other = 9,
}

/// <summary>
/// Identifikátor zaměstnance. Jeden člověk jich může mít libovolný počet
/// (např. tři karty a dvě SPZ); každý má vlastní platnost a lze ho deaktivovat
/// bez smazání kvůli historii.
/// </summary>
public class EmployeeIdentifier
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public IdentifierType Type { get; set; }

    /// <summary>Hodnota — číslo karty, SPZ, PIN… (normalizovaná na velká písmena bez mezer).</summary>
    public required string Value { get; set; }

    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    /// <summary>Importováno ze zdroje (SQL) nebo zadáno ručně.</summary>
    public RecordSource Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Platí identifikátor v daném okamžiku?</summary>
    public bool IsValidAt(DateTime moment)
        => IsActive
           && (ValidFrom is null || ValidFrom <= moment)
           && (ValidTo is null || ValidTo >= moment);

    /// <summary>Sjednocený tvar hodnoty pro porovnávání a hledání.</summary>
    public static string Normalize(string value)
        => value.Replace(" ", "").Replace("-", "").ToUpperInvariant();
}
