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

    /// <summary>
    /// Výchozí matice: použije se pro žádosti o čtečky, skupiny a parkovací povolení,
    /// které vlastní matici nemají (typicky „schvaluje nadřízený zaměstnance“).
    /// Bez výchozí matice takové položky rozhoduje administrátor. Nejvýše jedna.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Když pro položku žádosti neplatí žádná úroveň matice (podmínky kategorie / úseku
    /// všechny úrovně vyloučí — např. ředitel nebo náměstek ve vlastním úseku), položka se
    /// schválí bez schvalovacího stupně a jde rovnou do fronty realizace. Výchozí false:
    /// položku bez platné úrovně pak rozhoduje administrátor.
    /// </summary>
    public bool AutoApproveWhenNoLevels { get; set; }

    /// <summary>
    /// Když prostor žádosti nemá určený úsek ani odpovědnou osobu (nelze rozlišit vlastní /
    /// cizí úsek), berou se úrovně „jen mimo vlastní úsek“ jako platné (přísnější). Výchozí
    /// false: neurčený prostor se bere jako vlastní úsek (chová se jako dosud — jen nadřízený).
    /// </summary>
    public bool TreatUnknownUnitAsOutside { get; set; }

    /// <summary>Úrovně seřazené podle <see cref="ApprovalLevel.Order"/> — hloubka není omezena.</summary>
    public List<ApprovalLevel> Levels { get; set; } = [];
}

/// <summary>Podmínka úrovně podle vztahu zaměstnance a prostoru žádosti.</summary>
public enum LevelScope
{
    /// <summary>Úroveň platí vždy.</summary>
    Always = 0,
    /// <summary>Jen když je prostor ve vlastním úseku cílového zaměstnance.</summary>
    InsideOwnUnit = 1,
    /// <summary>Jen když je prostor mimo vlastní úsek cílového zaměstnance.</summary>
    OutsideOwnUnit = 2,
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

    /// <summary>
    /// Pro které kategorie zaměstnance úroveň platí (<see cref="RankFlags.None"/> = pro všechny).
    /// Úroveň, která pro cílového zaměstnance neplatí, se při průchodu přeskočí.
    /// </summary>
    public RankFlags AppliesToRanks { get; set; } = RankFlags.None;

    /// <summary>Podmínka podle vztahu zaměstnance a prostoru (vlastní / cizí úsek).</summary>
    public LevelScope Scope { get; set; } = LevelScope.Always;

    /// <summary>Má úroveň nějakou podmínku (kategorie nebo úsek)?</summary>
    public bool IsConditional => (AppliesToRanks != RankFlags.None && AppliesToRanks != RankFlags.All)
                                 || Scope != LevelScope.Always;

    public List<Approver> Approvers { get; set; } = [];
}

/// <summary>Druh schvalovatele v úrovni matice.</summary>
public enum ApproverKind
{
    /// <summary>Konkrétní uživatel ACS (<see cref="Approver.UserId"/>).</summary>
    User = 0,
    /// <summary>AD skupina (<see cref="Approver.AdGroup"/>) — zatím se ve workflow nevyhodnocuje.</summary>
    AdGroup = 1,
    /// <summary>
    /// Nadřízený cílového zaměstnance žádosti podle <see cref="Employee.ManagerId"/>
    /// (<see cref="Approver.ManagerDepth"/> 1 = přímý nadřízený, 2 = nadřízený nadřízeného).
    /// Vyhodnocuje se dynamicky u každé žádosti.
    /// </summary>
    LineManager = 2,
    /// <summary>
    /// Odpovědná osoba cílového úseku — odpovědná osoba prostoru žádosti (čtečky / místnosti /
    /// patra / budovy / skupiny), nebo vedoucí úseku, kterému prostor patří.
    /// Vyhodnocuje se dynamicky u každé žádosti.
    /// </summary>
    AreaOwner = 3,
}

/// <summary>Schvalovatel v úrovni — konkrétní uživatel, AD skupina, nebo nadřízený zaměstnance.</summary>
public class Approver
{
    public int Id { get; set; }
    public int LevelId { get; set; }
    public ApprovalLevel? Level { get; set; }

    public ApproverKind Kind { get; set; } = ApproverKind.User;

    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Alternativně AD skupina (DN nebo sAMAccountName).</summary>
    public string? AdGroup { get; set; }

    /// <summary>Pro <see cref="ApproverKind.LineManager"/>: 1 = přímý nadřízený, 2 = jeho nadřízený.</summary>
    public int ManagerDepth { get; set; } = 1;
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
