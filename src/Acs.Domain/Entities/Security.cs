namespace Acs.Domain.Entities;

/// <summary>Druh bezpečnostní technologie, o kterou se žádá.</summary>
public enum SecurityRequestKind
{
    /// <summary>Kamerový systém (CCTV).</summary>
    Camera = 0,
    /// <summary>Elektronická zabezpečovací signalizace (EZS).</summary>
    Alarm = 1,
    /// <summary>Kamery i EZS.</summary>
    Both = 2,
}

/// <summary>
/// Žádost o zřízení / rozšíření kamerového systému nebo EZS. Nejde o přístup, ale o
/// službu: schvaluje ji vedoucí OVBKŘ (matice nastavená pro kamery / EZS), realizuje
/// ICT útvar ve vlastní frontě. Stav nese položka žádosti <see cref="AccessRequestItem"/>,
/// která na tuto žádost odkazuje — sdílí tak rozhodnutí, zástupy, notifikace i audit.
/// </summary>
public class SecurityRequest
{
    public int Id { get; set; }

    public SecurityRequestKind Kind { get; set; } = SecurityRequestKind.Camera;

    /// <summary>Stručný název požadavku (např. „Kamera u vjezdu do skladu léčiv“).</summary>
    public required string Title { get; set; }

    /// <summary>Popis: co, proč, kolik kamer / čidel, požadovaný termín…</summary>
    public string? Description { get; set; }

    /// <summary>Umístění — budova / patro / místnost z číselníku (volitelné) a doplňující text.</summary>
    public int? BuildingId { get; set; }
    public Building? Building { get; set; }
    public int? FloorId { get; set; }
    public Floor? Floor { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public string? LocationText { get; set; }

    /// <summary>Úsek žadatele v okamžiku podání (kvůli přehledu, čí požadavek to je).</summary>
    public int? OrgUnitId { get; set; }
    public OrgUnit? OrgUnit { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Realizace ICT útvarem.</summary>
    public DateTime? ImplementedAt { get; set; }
    public int? ImplementedByUserId { get; set; }
    public AppUser? ImplementedByUser { get; set; }
    public string? ImplementationNote { get; set; }

    public string KindLabel => Kind switch
    {
        SecurityRequestKind.Camera => "kamerový systém",
        SecurityRequestKind.Alarm => "EZS",
        _ => "kamerový systém a EZS",
    };

    /// <summary>Text umístění pro zobrazení (vyžaduje načtené navigace).</summary>
    public string LocationPath()
    {
        List<string?> parts = [Building?.Name, Floor?.Name, Room?.Name, LocationText];
        var path = string.Join(" / ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return path.Length > 0 ? path : "—";
    }
}
