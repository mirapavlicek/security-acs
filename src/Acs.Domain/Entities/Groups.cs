namespace Acs.Domain.Entities;

/// <summary>
/// Skupina čteček. Může obsahovat čtečky i další (vnořené) skupiny.
/// O skupinu lze žádat jako o celek; má vlastní schvalovací matici.
/// Při žádosti o skupinu se schvaluje řetěz matic: matice skupiny a poté
/// matice všech nadřazených skupin (např. Chirurgie → Bezpečnost).
/// </summary>
public class ReaderGroup
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Schvalovací matice skupiny (null = bez vlastní matice).</summary>
    public int? ApprovalMatrixId { get; set; }
    public ApprovalMatrix? ApprovalMatrix { get; set; }

    public bool IsActive { get; set; } = true;

    public List<ReaderGroupMember> Members { get; set; } = [];
}

/// <summary>Člen skupiny — buď čtečka, nebo vnořená skupina (právě jedno).</summary>
public class ReaderGroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public ReaderGroup? Group { get; set; }

    public int? ReaderId { get; set; }
    public Reader? Reader { get; set; }

    public int? ChildGroupId { get; set; }
    public ReaderGroup? ChildGroup { get; set; }
}

/// <summary>
/// Pravidlo automatického zařazení: zaměstnanec s daným oddělením dostane
/// automaticky předschválený přístup ke skupině čteček (jde rovnou do fronty
/// správce karet — např. nástup na chirurgii → základní přístup Chirurgie).
/// </summary>
public class AutoAssignmentRule
{
    public int Id { get; set; }

    /// <summary>Oddělení zaměstnance (přesná shoda, bez rozlišení velikosti písmen).</summary>
    public required string Department { get; set; }

    public int ReaderGroupId { get; set; }
    public ReaderGroup? ReaderGroup { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Fáze schvalování položky žádosti — pořadí matic v řetězu (skupina → předci).</summary>
public class AccessRequestItemStage
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public AccessRequestItem? Item { get; set; }

    /// <summary>Pořadí fáze (1, 2, …) — položka jimi prochází postupně.</summary>
    public int Order { get; set; }

    public int MatrixId { get; set; }
    public ApprovalMatrix? Matrix { get; set; }
}
