namespace Acs.Domain.Entities;

/// <summary>
/// Událost nahlášená navázaným systémem přes integrační API (vjezd, výjezd, zamítnutí
/// u závory…) nebo zaznamenaná ACS při online autorizaci. Slouží k dohledání
/// („proč mě brána nepustila“) a k přehledu vjezdů zaměstnance — ne k rozhodování.
/// </summary>
public class IntegrationEvent
{
    public long Id { get; set; }

    /// <summary>Integrace, ze které událost přišla (zatím jen <c>parking</c>).</summary>
    public required string Source { get; set; }

    /// <summary>Id události v odesílajícím systému — podle něj se poznají duplicity. Null u vlastních záznamů ACS.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Druh: <c>vehicleIn</c>, <c>vehicleOut</c>, <c>denied</c>, <c>passed</c>, <c>authorizationCheck</c>, …</summary>
    public required string Type { get; set; }

    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Přístupový bod tak, jak ho poslal systém (id vjezdu / brány).</summary>
    public string? AccessPointId { get; set; }

    /// <summary>Areál, na který se přístupový bod podařilo napárovat.</summary>
    public int? SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Zaměstnanec, ke kterému se identifikátor dohledal.</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public IdentifierType? CredentialType { get; set; }

    /// <summary>Načtená hodnota (normalizovaná SPZ, číslo karty…).</summary>
    public string? CredentialValue { get; set; }

    /// <summary><c>in</c> / <c>out</c> u vjezdů.</summary>
    public string? Direction { get; set; }

    /// <summary><c>allow</c> / <c>deny</c> u autorizací a zamítnutí.</summary>
    public string? Decision { get; set; }

    /// <summary>Důvod rozhodnutí (<c>credentialUnknown</c>, <c>noEntitlement</c>, …).</summary>
    public string? DecisionReason { get; set; }

    /// <summary>Spolehlivost čtení kamerou (0–1), když ji systém posílá.</summary>
    public double? Confidence { get; set; }

    /// <summary>Doplňující údaje jako JSON (klíč → hodnota).</summary>
    public string? Details { get; set; }

    /// <summary>Korelační id odpovědi ACS — vrací se volajícímu, aby šlo spárovat log obou stran.</summary>
    public string? TraceId { get; set; }
}
