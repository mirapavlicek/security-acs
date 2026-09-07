namespace Acs.Domain.Entities;

/// <summary>
/// Areál nemocnice (Motol, Homolka…). Parkovací povolení platí pro jeden,
/// více nebo všechny areály; areál může mít vlastní schvalovací matici, která
/// se u žádosti zařadí do řetězu fází za matici druhu povolení.
/// </summary>
public class Site
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Krátký kód pro tisk a hledání (např. „MOT“, „HOM“).</summary>
    public string? Code { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Identifikátory vjezdů / parkoviště areálu v parkovacím systému (GreenCenter apod.),
    /// oddělené čárkou. Parkovací systém je posílá jako <c>accessPointId</c> v online
    /// autorizaci a ACS podle nich pozná, o který areál jde.
    /// </summary>
    public string? GateExternalIds { get; set; }

    /// <summary>Rozparsované <see cref="GateExternalIds"/> (bez prázdných, oříznuté).</summary>
    public IReadOnlyList<string> GateExternalIdList()
        => string.IsNullOrWhiteSpace(GateExternalIds)
            ? []
            : GateExternalIds.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Identifikátor areálu v integračním API (<c>site:MOT</c>, bez kódu <c>site:#12</c>).</summary>
    public string IntegrationId()
        => string.IsNullOrWhiteSpace(Code) ? $"site:#{Id}" : $"site:{Code}";

    /// <summary>Volitelná matice areálu (null = areál do schvalování nevstupuje).</summary>
    public int? ApprovalMatrixId { get; set; }
    public ApprovalMatrix? ApprovalMatrix { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// Konkrétní parkovací místo v areálu (stání, na které se vyhrazuje povolení). Na místo lze
/// přiřadit více vydaných povolení; cedule A4 „Vyhrazené parkování“ pak nese SPZ všech
/// platných povolení na místě.
/// </summary>
public class ParkingSpot
{
    public int Id { get; set; }

    public int SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Označení místa (např. „A-12“) — unikátní v rámci areálu.</summary>
    public required string Code { get; set; }

    /// <summary>Kde místo je (např. „před pavilonem 5, u vjezdu“).</summary>
    public string? Location { get; set; }
    public string? Note { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>„A-12 · Motol“ (vyžaduje načtený areál).</summary>
    public string DisplayName()
        => Site is null ? Code : $"{Code} · {Site.Name}";
}

/// <summary>Na co je parkovací povolení vázáno.</summary>
public enum PermitBinding
{
    /// <summary>Na registrační značku vozidla (jednu nebo více).</summary>
    LicensePlate = 0,
    /// <summary>Na funkci (např. „Vedení nemocnice“) — přenosné mezi vozidly, bez SPZ.</summary>
    Function = 1,
}

/// <summary>
/// Druh parkovacího povolení (např. „Vedení nemocnice“, „Zaměstnanec“, „Dodavatel“).
/// Každý druh má vlastní schvalovací proces (matici) a vlastní podobu kartičky za sklo.
/// </summary>
public class ParkingPermitType
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }

    public PermitBinding Binding { get; set; } = PermitBinding.LicensePlate;

    /// <summary>Schvalovací matice druhu (null = bez matice → rozhoduje administrátor).</summary>
    public int? ApprovalMatrixId { get; set; }
    public ApprovalMatrix? ApprovalMatrix { get; set; }

    /// <summary>Kolik SPZ smí jedno povolení nést (jen u vazby na SPZ).</summary>
    public int MaxPlates { get; set; } = 1;

    /// <summary>Výchozí délka platnosti v měsících (null = bez omezení / zadá žadatel).</summary>
    public int? DefaultValidityMonths { get; set; }

    /// <summary>Předvyplnit „všechny areály“.</summary>
    public bool AllSitesByDefault { get; set; }

    /// <summary>Tiskne se k povolení kartička za čelní sklo?</summary>
    public bool PrintsWindshieldCard { get; set; } = true;

    /// <summary>Nadpis kartičky (výchozí „POVOLENÍ K PARKOVÁNÍ“).</summary>
    public string? CardTitle { get; set; }

    /// <summary>Text rozsahu na kartičce (null = vygeneruje se ze zvolených areálů).</summary>
    public string? CardScopeText { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// Konkrétní parkovací povolení přidělené zaměstnanci. Stav (čeká / schváleno /
/// vydáno / zamítnuto / odebráno) nese položka žádosti <see cref="AccessRequestItem"/>,
/// která na povolení odkazuje; tady jsou jen parametry a údaje o vydání.
/// </summary>
public class ParkingPermit
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public int PermitTypeId { get; set; }
    public ParkingPermitType? PermitType { get; set; }

    /// <summary>Funkce, na kterou je povolení vázáno (u <see cref="PermitBinding.Function"/>).</summary>
    public string? FunctionTitle { get; set; }

    /// <summary>Platí pro všechny areály (pak je <see cref="Sites"/> prázdné).</summary>
    public bool AllSites { get; set; }
    public List<ParkingPermitSite> Sites { get; set; } = [];

    /// <summary>Registrační značky (u vazby na SPZ).</summary>
    public List<ParkingPermitPlate> Plates { get; set; } = [];

    /// <summary>Vyhrazené parkovací místo (null = povolení bez konkrétního místa).</summary>
    public int? ParkingSpotId { get; set; }
    public ParkingSpot? ParkingSpot { get; set; }

    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    /// <summary>Číslo povolení — přidělí správce parkování při vydání.</summary>
    public string? PermitNumber { get; set; }
    public DateTime? IssuedAt { get; set; }
    public int? IssuedByUserId { get; set; }
    public AppUser? IssuedByUser { get; set; }

    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Kdy se stav povolení naposledy úspěšně předal do parkovacího systému (vzor A).</summary>
    public DateTime? ParkingSystemSyncedAt { get; set; }

    /// <summary>Chyba posledního předání do parkovacího systému (null = bez chyby).</summary>
    public string? ParkingSystemSyncError { get; set; }

    /// <summary>Platí povolení v daném okamžiku (podle dat, nezávisle na stavu položky)?</summary>
    public bool IsValidAt(DateTime moment)
        => RevokedAt is null
           && ValidFrom <= moment
           && (ValidTo is null || ValidTo >= moment);

    /// <summary>Platí povolení pro daný areál?</summary>
    public bool CoversSite(int siteId)
        => AllSites || Sites.Any(s => s.SiteId == siteId);

    /// <summary>Text rozsahu areálů pro zobrazení a tisk (vyžaduje načtené navigace).</summary>
    public string SitesText()
        => AllSites
            ? "všechny areály"
            : Sites.Count == 0
                ? "—"
                : string.Join(", ", Sites.Select(s => s.Site?.Name ?? s.SiteId.ToString()));

    /// <summary>SPZ oddělené čárkou (nebo funkce u vazby na funkci).</summary>
    public string SubjectText()
        => Plates.Count > 0
            ? string.Join(", ", Plates.Select(p => p.Value))
            : FunctionTitle ?? "—";
}

/// <summary>Areál, pro který povolení platí (M:N).</summary>
public class ParkingPermitSite
{
    public int Id { get; set; }
    public int PermitId { get; set; }
    public ParkingPermit? Permit { get; set; }
    public int SiteId { get; set; }
    public Site? Site { get; set; }
}

/// <summary>Registrační značka na povolení (normalizovaná — velká písmena bez mezer a pomlček).</summary>
public class ParkingPermitPlate
{
    public int Id { get; set; }
    public int PermitId { get; set; }
    public ParkingPermit? Permit { get; set; }
    public required string Value { get; set; }
    public string? Note { get; set; }

    /// <summary>Identifikátor zaměstnance založený při vydání povolení (kvůli odebrání).</summary>
    public int? EmployeeIdentifierId { get; set; }
    public EmployeeIdentifier? EmployeeIdentifier { get; set; }
}
