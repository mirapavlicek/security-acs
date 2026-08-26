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
    public List<Floor> Floors { get; set; } = [];
}

public class Floor
{
    public int Id { get; set; }
    public int BuildingId { get; set; }
    public Building? Building { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public byte[]? SchemaImage { get; set; }
    public string? SchemaContentType { get; set; }
    public List<Room> Rooms { get; set; } = [];
}

public class Room
{
    public int Id { get; set; }
    public int FloorId { get; set; }
    public Floor? Floor { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<Reader> Readers { get; set; } = [];
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

    /// <summary>Pozice na schématu patra (procenta 0–100), pro grafické plány.</summary>
    public double? SchemaX { get; set; }
    public double? SchemaY { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public List<ReaderDependency> Dependencies { get; set; } = [];
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

    /// <summary>Číslo karty / card holder id ve WIN-PAK.</summary>
    public string? WinPakCardHolderId { get; set; }
    public string? CardNumber { get; set; }

    public bool IsActive { get; set; } = true;
    public RecordSource Source { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}
