namespace Acs.Domain.Entities;

public enum RequestStatus
{
    /// <summary>Rozpracováno žadatelem.</summary>
    Draft = 0,
    /// <summary>Čeká na schválení (aktuální úroveň viz <see cref="AccessRequestItem.CurrentLevelOrder"/>).</summary>
    Pending = 1,
    /// <summary>Schváleno všemi úrovněmi — čeká ve frontě správce karet.</summary>
    Approved = 2,
    /// <summary>Zapsáno do WIN-PAK přes API.</summary>
    PushedToWinPak = 3,
    /// <summary>Správce karet potvrdil ruční zadání do WIN-PAK.</summary>
    ManuallyConfirmed = 4,
    /// <summary>Zamítnuto.</summary>
    Rejected = 5,
    /// <summary>Přístup odebrán (revokace).</summary>
    Revoked = 6,
    /// <summary>Zrušeno žadatelem.</summary>
    Cancelled = 7,
}

public enum RequestKind
{
    Grant = 0,
    Revoke = 1,
}

/// <summary>Žádost o přístup(y) pro zaměstnance.</summary>
public class AccessRequest
{
    public int Id { get; set; }
    public RequestKind Kind { get; set; } = RequestKind.Grant;

    public int RequesterUserId { get; set; }
    public AppUser? RequesterUser { get; set; }

    /// <summary>Zaměstnanec, kterému se přístup uděluje.</summary>
    public int TargetEmployeeId { get; set; }
    public Employee? TargetEmployee { get; set; }

    public string? Justification { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }

    public List<AccessRequestItem> Items { get; set; } = [];
}

/// <summary>
/// Položka žádosti — buď jedna čtečka, nebo celá skupina čteček (právě jedno).
/// Položka prochází řetězem matic (fáze <see cref="Stages"/>): u skupiny je to
/// matice skupiny a poté matice všech nadřazených skupin. Položky doplněné
/// automaticky z řetězce závislostí mají <see cref="AutoAdded"/>.
/// </summary>
public class AccessRequestItem
{
    public int Id { get; set; }
    public int RequestId { get; set; }
    public AccessRequest? Request { get; set; }

    /// <summary>Čtečka (null, pokud jde o skupinu).</summary>
    public int? ReaderId { get; set; }
    public Reader? Reader { get; set; }

    /// <summary>Skupina čteček (null, pokud jde o jednotlivou čtečku).</summary>
    public int? ReaderGroupId { get; set; }
    public ReaderGroup? ReaderGroup { get; set; }

    public bool AutoAdded { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    /// <summary>Pořadí úrovně aktuální matice, na které položka právě čeká.</summary>
    public int CurrentLevelOrder { get; set; } = 1;

    /// <summary>Aktuální matice (zafixovaná v okamžiku podání; u řetězu se posouvá po fázích).</summary>
    public int? MatrixId { get; set; }

    /// <summary>Pořadí aktuální fáze řetězu matic (1, 2, …).</summary>
    public int CurrentStageOrder { get; set; } = 1;

    public DateTime? DecidedAt { get; set; }
    public DateTime? PushedAt { get; set; }
    public string? PushResult { get; set; }

    public List<ApprovalDecision> Decisions { get; set; } = [];

    /// <summary>Řetěz matic, kterými položka prochází (prázdné = jen <see cref="MatrixId"/>).</summary>
    public List<AccessRequestItemStage> Stages { get; set; } = [];
}

/// <summary>Rozhodnutí schvalovatele (audit).</summary>
public class ApprovalDecision
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public AccessRequestItem? Item { get; set; }

    public int LevelOrder { get; set; }

    /// <summary>Matice, v jejíž úrovni bylo rozhodnuto (kvůli řetězení matic).</summary>
    public int? MatrixId { get; set; }

    public int ApproverUserId { get; set; }
    public AppUser? ApproverUser { get; set; }

    /// <summary>Pokud rozhodoval zástup, uživatel, za kterého jednal.</summary>
    public int? OnBehalfOfUserId { get; set; }

    public bool Approved { get; set; }
    public string? Comment { get; set; }
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}
