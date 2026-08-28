using System.Text.Json;
using System.Text.Json.Serialization;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Import;

/// <summary>Jedna místnost z výkresu (výstup extraktoru <c>import/moc/extract.py</c>).</summary>
public class PlanRoom
{
    public string Number { get; set; } = "";
    public string? NumberDashed { get; set; }
    public string? NumberDotted { get; set; }
    public string? Name { get; set; }
    public bool IsCorridor { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Jedna čtečka z výkresu (popisek ACS.NN u dveří).</summary>
public class PlanReader
{
    public string Code { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public string? Room { get; set; }
    public double? RoomDistance { get; set; }
    public string? RoomNonCorridor { get; set; }
}

/// <summary>Jeden výkres = patro (případně jeho část A/B).</summary>
public class PlanFloor
{
    public string File { get; set; } = "";
    public string Floor { get; set; } = "";
    public string? Section { get; set; }
    public List<PlanRoom> Rooms { get; set; } = [];
    public List<PlanReader> Readers { get; set; } = [];
}

public record PlanImportResult(
    int Sections, int Floors, int Corridors, int Rooms, int Readers,
    int UpdatedRooms, int UpdatedReaders, int UnassignedReaders, bool DryRun)
{
    public override string ToString() =>
        (DryRun ? "NÁHLED (nic neuloženo) — " : "Import dokončen — ")
        + $"částí {Sections}, pater {Floors}, chodeb {Corridors}, místností {Rooms} "
        + $"(z toho aktualizováno {UpdatedRooms}), čteček {Readers} "
        + $"(aktualizováno {UpdatedReaders}, bez přiřazené místnosti {UnassignedReaders}).";
}

/// <summary>
/// Jednorázový import struktury budovy z DPS výkresů: budova → části (A/B) →
/// patra → chodby → místnosti → čtečky. Idempotentní — opakovaný běh existující
/// záznamy jen aktualizuje, nic nemaže.
/// </summary>
public class PlanImportService(AcsDbContext db, AuditService audit)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static List<PlanFloor> Parse(Stream json)
        => JsonSerializer.Deserialize<List<PlanFloor>>(json, JsonOpts) ?? [];

    /// <summary>
    /// Provede import. Náhled (<paramref name="dryRun"/>) běží ve stejné cestě kódu,
    /// ale v transakci, která se na konci odvolá — počty tak odpovídají realitě.
    /// </summary>
    public async Task<PlanImportResult> ImportAsync(
        List<PlanFloor> plan, string buildingName, bool dryRun, bool preferNonCorridor,
        string? userName, CancellationToken ct = default)
    {
        // Na MariaDB je zapnutá retry strategie, která vlastní transakce povoluje
        // jen uvnitř ExecuteAsync — celý import proto běží jako jedna opakovatelná jednotka.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await RunAsync(plan, buildingName, dryRun, preferNonCorridor, userName, ct);
                if (dryRun)
                    await tx.RollbackAsync(ct);
                else
                    await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    private async Task<PlanImportResult> RunAsync(
        List<PlanFloor> plan, string buildingName, bool dryRun, bool preferNonCorridor,
        string? userName, CancellationToken ct)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Name == buildingName, ct);
        if (building is null)
        {
            building = new Building { Name = buildingName, Description = "Import z DPS výkresů" };
            db.Buildings.Add(building);
            await db.SaveChangesAsync(ct);
        }

        var sections = new Dictionary<string, BuildingSection>(StringComparer.OrdinalIgnoreCase);
        var floors = new Dictionary<string, Floor>(StringComparer.OrdinalIgnoreCase);
        int corridorCount = 0, roomCount = 0, readerCount = 0;
        int updatedRooms = 0, updatedReaders = 0, unassigned = 0;

        foreach (var planFloor in plan.OrderBy(f => FloorOrder(f.Floor)).ThenBy(f => f.Section))
        {
            // --- část budovy (A/B) ---
            BuildingSection? section = null;
            if (!string.IsNullOrWhiteSpace(planFloor.Section))
            {
                if (!sections.TryGetValue(planFloor.Section, out section))
                {
                    section = await db.BuildingSections
                        .FirstOrDefaultAsync(s => s.BuildingId == building.Id && s.Name == planFloor.Section, ct);
                    if (section is null)
                    {
                        section = new BuildingSection
                        {
                            BuildingId = building.Id, Name = planFloor.Section,
                            SortOrder = planFloor.Section == "A" ? 1 : 2,
                        };
                        db.BuildingSections.Add(section);
                        await db.SaveChangesAsync(ct);
                    }

                    sections[planFloor.Section] = section;
                }
            }

            // --- patro (jeden výkres = jedno patro, části se do něj slučují) ---
            var floorKey = $"{planFloor.Floor}|{planFloor.Section}";
            if (!floors.TryGetValue(floorKey, out var floor))
            {
                var floorName = planFloor.Section is null
                    ? planFloor.Floor
                    : $"{planFloor.Floor} {planFloor.Section}";
                floor = await db.Floors
                    .FirstOrDefaultAsync(f => f.BuildingId == building.Id && f.Name == floorName, ct);
                if (floor is null)
                {
                    floor = new Floor
                    {
                        BuildingId = building.Id, Name = floorName,
                        SectionId = section?.Id, SortOrder = FloorOrder(planFloor.Floor),
                    };
                    db.Floors.Add(floor);
                    await db.SaveChangesAsync(ct);
                }
                else if (floor.SectionId is null && section is not null)
                {
                    floor.SectionId = section.Id;
                }

                floors[floorKey] = floor;
            }

            // --- chodby a místnosti ---
            var corridorsByNumber = new Dictionary<string, Corridor>(StringComparer.OrdinalIgnoreCase);
            var roomsByNumber = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);

            foreach (var planRoom in planFloor.Rooms)
            {
                var label = Label(planRoom);
                if (planRoom.IsCorridor)
                {
                    var corridor = await db.Corridors
                        .FirstOrDefaultAsync(c => c.FloorId == floor.Id && c.Name == label, ct);
                    if (corridor is null)
                    {
                        corridor = new Corridor { FloorId = floor.Id, Name = label };
                        db.Corridors.Add(corridor);
                        await db.SaveChangesAsync(ct);
                        corridorCount++;
                    }

                    corridorsByNumber[planRoom.Number] = corridor;
                }
                else
                {
                    var room = await db.Rooms
                        .FirstOrDefaultAsync(r => r.FloorId == floor.Id && r.Name == label, ct);
                    if (room is null)
                    {
                        room = new Room { FloorId = floor.Id, Name = label, Description = planRoom.Name };
                        db.Rooms.Add(room);
                        await db.SaveChangesAsync(ct);
                        roomCount++;
                    }
                    else if (room.Description != planRoom.Name)
                    {
                        room.Description = planRoom.Name;
                        updatedRooms++;
                    }

                    // Poloha z výkresu — z ní pak generátor sestaví plán patra.
                    room.SourceX = planRoom.X;
                    room.SourceY = planRoom.Y;

                    roomsByNumber[planRoom.Number] = room;
                }
            }

            await db.SaveChangesAsync(ct);

            // --- čtečky (každý popisek ACS.NN = jedna čtečka u dveří) ---
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var planReader in planFloor.Readers.OrderBy(r => r.Y).ThenBy(r => r.X))
            {
                var targetNumber = preferNonCorridor && planReader.RoomNonCorridor is not null
                    ? planReader.RoomNonCorridor
                    : planReader.Room;

                roomsByNumber.TryGetValue(targetNumber ?? "", out var room);
                corridorsByNumber.TryGetValue(targetNumber ?? "", out var corridor);
                if (room is null && corridor is null && planReader.Room is not null)
                {
                    roomsByNumber.TryGetValue(planReader.Room, out room);
                    corridorsByNumber.TryGetValue(planReader.Room, out corridor);
                }

                if (room is null && corridor is null)
                    unassigned++;

                // Název čtečky musí být jednoznačný: kód + místo + pořadí na patře.
                var place = room?.Name ?? corridor?.Name ?? floor.Name;
                var baseName = $"{planReader.Code} — {place}";
                counters[baseName] = counters.GetValueOrDefault(baseName) + 1;
                var name = counters[baseName] == 1 ? baseName : $"{baseName} ({counters[baseName]})";

                var reader = await db.Readers.FirstOrDefaultAsync(r => r.Name == name, ct);
                if (reader is null)
                {
                    reader = new Reader
                    {
                        Name = name,
                        Description = $"Import DPS {planFloor.File}",
                        Source = RecordSource.Manual,
                        IsActive = true,
                        RoomId = room?.Id,
                        CorridorId = room is null ? corridor?.Id : null,
                    };
                    db.Readers.Add(reader);
                    readerCount++;
                }
                else
                {
                    reader.RoomId = room?.Id;
                    reader.CorridorId = room is null ? corridor?.Id : null;
                    updatedReaders++;
                }

                reader.SourceX = planReader.X;
                reader.SourceY = planReader.Y;
            }

            await db.SaveChangesAsync(ct);
        }

        var result = new PlanImportResult(sections.Count, floors.Count, corridorCount, roomCount,
            readerCount, updatedRooms, updatedReaders, unassigned, dryRun);

        if (!dryRun)
            await audit.LogAsync(userName, "plan-imported", "Building", building.Id.ToString(), result.ToString(), ct);

        return result;
    }

    /// <summary>Popisek místnosti/chodby: „číslo — název“ (číslo dle pravidla pomlčka &gt; tečka).</summary>
    private static string Label(PlanRoom room)
        => string.IsNullOrWhiteSpace(room.Name) ? room.Number : $"{room.Number} — {room.Name}";

    /// <summary>Pořadí pater: TP, 2PP, 1PP, 1NP … 6NP.</summary>
    private static int FloorOrder(string floor) => floor.ToUpperInvariant() switch
    {
        "TP" => -100,
        var f when f.EndsWith("PP") && int.TryParse(f[..^2], out var n) => -n,
        var f when f.EndsWith("NP") && int.TryParse(f[..^2], out var n) => n,
        _ => 999,
    };

}
