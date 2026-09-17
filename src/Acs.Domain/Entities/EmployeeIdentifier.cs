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

/// <summary>
/// Otisk posledního stažení identifikátorů ze zdroje (integrační služba, SQL) —
/// řádek na identifikátor tak, jak přišel, plus číslo převedené pro čtečky a
/// zaměstnanec, ke kterému se spároval. Při každé synchronizaci se přepíše celý.
/// Slouží k tomu, aby se párování dělalo nad naší databází a šlo dohledat, co
/// zdroj vrátil a proč se něco nespárovalo (osobní číslo, které v ACS není).
/// </summary>
public class ImportedIdentifier
{
    public int Id { get; set; }

    /// <summary>Osobní číslo (employeeNo) ze zdroje.</summary>
    public string? EmployeeNo { get; set; }

    /// <summary>AD účet ze zdroje (MSSQL), když osobní číslo chybí.</summary>
    public string? AdAccount { get; set; }

    /// <summary>Podtyp identifikátoru ve zdroji (idIdentifierSubType), je-li znám.</summary>
    public int? SubType { get; set; }

    /// <summary>Hodnota přesně tak, jak ji zdroj vrátil (např. <c>4d-07782</c>).</summary>
    public required string RawValue { get; set; }

    /// <summary>Hodnota po převodu pro čtečky a normalizaci (např. <c>07782</c>).</summary>
    public required string Value { get; set; }

    public IdentifierType Type { get; set; }

    /// <summary>Spárovaný zaměstnanec (null = osobní číslo v ACS není).</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Proč se záznam do identifikátorů nepřenesl (podtyp bez pravidla…); null = přenesen.</summary>
    public string? SkipReason { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
