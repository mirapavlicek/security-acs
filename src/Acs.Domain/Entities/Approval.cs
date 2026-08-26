namespace Acs.Domain.Entities;

/// <summary>Režim schválení jedné úrovně matice.</summary>
public enum ApprovalMode
{
    /// <summary>Stačí schválení kteréhokoli schvalovatele úrovně.</summary>
    Any = 0,
    /// <summary>Musí schválit všichni schvalovatelé úrovně.</summary>
    All = 1,
    /// <summary>Musí schválit alespoň <see cref="ApprovalLevel.RequiredCount"/> schvalovatelů.</summary>
    Quorum = 2,
}

/// <summary>
/// Schvalovací matice — znovupoužitelná definice schvalovacího procesu.
/// Čtečky na ni odkazují přes <see cref="Reader.ApprovalMatrixId"/>;
/// jedna matice může sloužit libovolnému počtu čteček.
/// </summary>
public class ApprovalMatrix
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Úrovně seřazené podle <see cref="ApprovalLevel.Order"/> — hloubka není omezena.</summary>
    public List<ApprovalLevel> Levels { get; set; } = [];
}

/// <summary>Jedna úroveň schvalovací matice (žádost jimi prochází postupně).</summary>
public class ApprovalLevel
{
    public int Id { get; set; }
    public int MatrixId { get; set; }
    public ApprovalMatrix? Matrix { get; set; }

    /// <summary>Pořadí úrovně v matici (1, 2, 3…).</summary>
    public int Order { get; set; }

    public string? Name { get; set; }
    public ApprovalMode Mode { get; set; } = ApprovalMode.Any;

    /// <summary>Počet nutných schválení pro režim <see cref="ApprovalMode.Quorum"/>.</summary>
    public int? RequiredCount { get; set; }

    public List<Approver> Approvers { get; set; } = [];
}

/// <summary>Schvalovatel v úrovni — konkrétní uživatel, nebo AD skupina.</summary>
public class Approver
{
    public int Id { get; set; }
    public int LevelId { get; set; }
    public ApprovalLevel? Level { get; set; }

    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Alternativně AD skupina (DN nebo sAMAccountName).</summary>
    public string? AdGroup { get; set; }
}

/// <summary>Zástup: <see cref="DeputyUserId"/> smí schvalovat za <see cref="PrincipalUserId"/> v daném období.</summary>
public class Deputy
{
    public int Id { get; set; }
    public int PrincipalUserId { get; set; }
    public AppUser? PrincipalUser { get; set; }
    public int DeputyUserId { get; set; }
    public AppUser? DeputyUser { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public string? Note { get; set; }

    public bool IsValidAt(DateTime moment) => moment >= ValidFrom && moment <= ValidTo;
}
