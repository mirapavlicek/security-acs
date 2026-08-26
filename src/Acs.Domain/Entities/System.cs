namespace Acs.Domain.Entities;

/// <summary>Nastavení aplikace (vše editovatelné v GUI). Citlivé hodnoty jsou šifrované.</summary>
public class Setting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>Auditní záznam.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string? UserName { get; set; }
    public required string Action { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
}
