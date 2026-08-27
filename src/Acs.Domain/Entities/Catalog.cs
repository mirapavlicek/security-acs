namespace Acs.Domain.Entities;

/// <summary>Původ záznamu číselníku.</summary>
public enum RecordSource
{
    /// <summary>Importováno synchronizací (WIN-PAK, MSSQL, API).</summary>
    Imported = 0,
    /// <summary>Vytvořeno ručně v ACS.</summary>
    Manual = 1,
}

public class Building
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public byte[]? SchemaImage { get; set; }
    public string? SchemaContentType { get; set; }
    public List<BuildingSection> Sections { get; set; } = [];
    public List<Floor> Floors { get; set; } = [];
}

/// <summary>Část budovy (např. „G“ v budově MOC). Patra mohou (ale nemusí) patřit do části.</summary>
public class BuildingSection
{
    public int Id { get; set; }
    public int BuildingId { get; set; }
    public Building? Building { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public List<Floor> Floors { get; set; } = [];
}

public class Floor
{
    public int Id { get; set; }
    public int BuildingId { get; set; }
    public Building? Building { get; set; }

    /// <summary>Část budovy, do které patro patří (volitelné — malé budovy části nemají).</summary>
    public int? SectionId { get; set; }
    public BuildingSection? Section { get; set; }

    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public byte[]? SchemaImage { get; set; }
    public string? SchemaContentType { get; set; }
    public List<Corridor> Corridors { get; set; } = [];
    public List<Room> Rooms { get; set; } = [];
}

/// <summary>
/// Chodba (např. „A100“). Může mít čtečky a místnosti; chodby se mohou řetězit
/// přes <see cref="ParentCorridorId"/> (i napříč patry — např. chodba → schodiště).
/// Při žádosti o čtečku místnosti se automaticky přidají čtečky celého řetězu chodeb.
/// </summary>
public class Corridor
{
    public int Id { get; set; }
    public int FloorId { get; set; }
    public Floor? Floor { get; set; }
    public required string Name { get; set; }

    /// <summary>Nadřazená chodba v řetězu (kudy se do této chodby vchází).</summary>
    public int? ParentCorridorId { get; set; }
    public Corridor? ParentCorridor { get; set; }

    public List<Room> Rooms { get; set; } = [];
    public List<Reader> Readers { get; set; } = [];
}

public class Room
{
    public int Id { get; set; }
    public int FloorId { get; set; }
    public Floor? Floor { get; set; }

    /// <summary>Chodba, ze které se do místnosti vchází (volitelné).</summary>
    public int? CorridorId { get; set; }
    public Corridor? Corridor { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<Reader> Readers { get; set; } = [];

    /// <summary>Geometrie na interaktivním plánu patra (procenta plochy, null = neumístěno).</summary>
    public double? PlanX { get; set; }
    public double? PlanY { get; set; }
    public double? PlanW { get; set; }
    public double? PlanH { get; set; }
}

/// <summary>Čtečka — importovaná z WIN-PAK přes konektor, nebo ruční.</summary>
public class Reader
{
    public int Id { get; set; }

    /// <summary>Id čtečky ve WIN-PAK (u ručních záznamů null).</summary>
    public string? ExternalId { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? PanelName { get; set; }
    public string? AccountName { get; set; }

    /// <summary>WIN-PAK access level, který odpovídá této čtečce (pro zápis přístupu).</summary>
    public string? AccessLevelExternalId { get; set; }

    public bool IsActive { get; set; } = true;
    public RecordSource Source { get; set; }

    public int? RoomId { get; set; }
    public Room? Room { get; set; }

    /// <summary>Čtečka umístěná přímo na chodbě (alternativa k místnosti).</summary>
    public int? CorridorId { get; set; }
    public Corridor? Corridor { get; set; }

    /// <summary>Schvalovací matice pro žádosti o tuto čtečku (null = bez schvalování).</summary>
    public int? ApprovalMatrixId { get; set; }
    public ApprovalMatrix? ApprovalMatrix { get; set; }

    /// <summary>Pozice na schématu patra (procenta 0–100), pro grafické plány.</summary>
    public double? SchemaX { get; set; }
    public double? SchemaY { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public List<ReaderDependency> Dependencies { get; set; } = [];

    /// <summary>Cesta umístění: budova / část / patro / chodba / místnost (vyžaduje načtené navigace).</summary>
    public string LocationPath()
    {
        List<string?> parts = Room is not null
            ? [Room.Floor?.Building?.Name, Room.Floor?.Section?.Name, Room.Floor?.Name, Room.Corridor?.Name, Room.Name]
            : Corridor is not null
                ? [Corridor.Floor?.Building?.Name, Corridor.Floor?.Section?.Name, Corridor.Floor?.Name, Corridor.Name]
                : [];
        var path = string.Join(" / ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return path.Length > 0 ? path : "—";
    }
}

/// <summary>
/// Řetězec čteček: přístup ke čtečce <see cref="ReaderId"/> vyžaduje
/// i přístup ke čtečce <see cref="RequiresReaderId"/> (místnost → chodba → patro…).
/// </summary>
public class ReaderDependency
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public Reader? Reader { get; set; }
    public int RequiresReaderId { get; set; }
    public Reader? RequiresReader { get; set; }
}

/// <summary>Zaměstnanec — importovaný (MSSQL/API) nebo ruční.</summary>
public class Employee
{
    public int Id { get; set; }
    public string? ExternalId { get; set; }
    public string? PersonalNumber { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Email { get; set; }
    public string? Department { get; set; }

    /// <summary>AD účet (sAMAccountName) pro párování s přihlášeným uživatelem.</summary>
    public string? AdAccount { get; set; }

    /// <summary>Card holder id ve WIN-PAK (jedno na osobu).</summary>
    public string? WinPakCardHolderId { get; set; }

    /// <summary>
    /// Primární číslo karty — zkratka pro zobrazení a hledání.
    /// Úplný seznam (více karet, SPZ, PIN…) je v <see cref="Identifiers"/>.
    /// </summary>
    public string? CardNumber { get; set; }

    /// <summary>Všechny identifikátory zaměstnance (karty, SPZ, PIN…).</summary>
    public List<EmployeeIdentifier> Identifiers { get; set; } = [];

    public bool IsActive { get; set; } = true;
    public RecordSource Source { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}
