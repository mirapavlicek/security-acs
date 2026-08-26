namespace Acs.Domain.Entities;

/// <summary>Role v aplikaci. Uloženo jako bitová maska.</summary>
[Flags]
public enum AppRole
{
    None = 0,
    /// <summary>Lokální/AD administrátor — plný přístup včetně nastavení.</summary>
    Admin = 1,
    /// <summary>Správa číselníků (čtečky, místnosti, zaměstnanci…).</summary>
    CatalogManager = 2,
    /// <summary>Schvalovatel v maticích.</summary>
    Approver = 4,
    /// <summary>Správce karet — fronta předání do WIN-PAK.</summary>
    CardAdmin = 8,
    /// <summary>Běžný uživatel — žádosti a přehled vlastních přístupů.</summary>
    Employee = 16,
}

/// <summary>Uživatelský účet (lokální nebo mapovaný z Active Directory).</summary>
public class AppUser
{
    public int Id { get; set; }
    public required string UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    /// <summary>true = lokální účet s heslem v DB; false = ověřuje se proti AD.</summary>
    public bool IsLocal { get; set; }

    /// <summary>PBKDF2 hash hesla (pouze lokální účty).</summary>
    public string? PasswordHash { get; set; }

    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; } = true;
    public AppRole Roles { get; set; } = AppRole.Employee;

    /// <summary>Vazba na zaměstnance (kvůli „moje přístupy“ a schvalování).</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Preferované barevné téma GUI.</summary>
    public string? Theme { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}
