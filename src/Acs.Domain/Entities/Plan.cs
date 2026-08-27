namespace Acs.Domain.Entities;

/// <summary>Typ zařízení na plánu patra (rozšiřitelné — kamery, EZS, rozhlas, TV…).</summary>
public enum PlanDeviceType
{
    Camera = 1,
    Ezs = 2,
    Rozhlas = 3,
    Tv = 4,
}

/// <summary>
/// Zařízení umístěné na interaktivním plánu patra (mimo čteček — ty mají pozici
/// přímo v <see cref="Reader.SchemaX"/>/<see cref="Reader.SchemaY"/>).
/// Souřadnice jsou v procentech plochy plánu (0–100).
/// </summary>
public class PlanDevice
{
    public int Id { get; set; }
    public int FloorId { get; set; }
    public Floor? Floor { get; set; }

    public PlanDeviceType Type { get; set; }
    public required string Name { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
}
