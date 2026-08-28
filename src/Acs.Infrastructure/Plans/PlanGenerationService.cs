using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Plans;

/// <summary>Podle čeho se plán patra poskládal.</summary>
public enum PlanGenerationMode
{
    /// <summary>Nebylo z čeho generovat (patro nemá místnosti ani čtečky).</summary>
    Empty,

    /// <summary>Z poloh ve výkresech DPS — plán odpovídá skutečnému rozložení budovy.</summary>
    FromDrawing,

    /// <summary>Schéma odvozené ze struktury: místnosti seřazené po chodbách.</summary>
    Schematic,
}

public record PlanGenerationResult(
    int FloorId,
    string FloorName,
    PlanGenerationMode Mode,
    int RoomsPlaced,
    int ReadersPlaced,
    int Skipped)
{
    public override string ToString()
    {
        var how = Mode switch
        {
            PlanGenerationMode.FromDrawing => "z výkresů",
            PlanGenerationMode.Schematic => "schéma podle chodeb",
            _ => "nebylo z čeho generovat",
        };

        return Skipped > 0
            ? $"{FloorName}: {how} — místností {RoomsPlaced}, čteček {ReadersPlaced}, ponecháno {Skipped}"
            : $"{FloorName}: {how} — místností {RoomsPlaced}, čteček {ReadersPlaced}";
    }
}

/// <summary>Výsledek generování za celou budovu.</summary>
public record BuildingPlanGenerationResult(string BuildingName, IReadOnlyList<PlanGenerationResult> Floors)
{
    public int RoomsPlaced => Floors.Sum(f => f.RoomsPlaced);
    public int ReadersPlaced => Floors.Sum(f => f.ReadersPlaced);
    public int FromDrawing => Floors.Count(f => f.Mode == PlanGenerationMode.FromDrawing);

    public override string ToString()
        => $"{BuildingName}: pater {Floors.Count} (z výkresů {FromDrawing}), "
           + $"místností {RoomsPlaced}, čteček {ReadersPlaced}";
}

/// <summary>
/// Sestaví plán patra z dat, která už v systému jsou — ať se stovky místností
/// nemusí do plánu ručně přetahovat.
///
/// Nejlepší podklad jsou polohy z projektových výkresů (<see cref="Room.SourceX"/>);
/// pak plán odpovídá skutečnému rozložení budovy. Když je patro nemá (například
/// zadané ručně), poskládá se schéma podle chodeb — místnosti jedné chodby jdou
/// v jednom pásu za sebou. Obojí je jen výchozí rozvržení, které si správce
/// v editoru může doladit.
/// </summary>
public class PlanGenerationService(AcsDbContext db, AuditService audit)
{
    /// <summary>Okraj plánu v procentech, ať prvky nelepí na hranu.</summary>
    private const double Margin = 4;

    /// <summary>
    /// Mez, pod kterou se rozměr místnosti nesmí zmenšit (procenta plochy).
    /// Musí být malá — patra mají i přes dvě stě místností nahloučených v křídlech,
    /// větší box by se v nich nutně překrýval. Popisek se u malých boxů skryje
    /// a název zůstane v bublině.
    /// </summary>
    private const double MinRoomSize = 1.5;

    private const double MaxRoomSize = 14;

    /// <param name="onlyEmpty">
    /// True = doplní jen prvky bez souřadnic a ruční práci nechá být.
    /// False = přerovná celé patro.
    /// </param>
    public async Task<PlanGenerationResult> GenerateFloorAsync(
        int floorId, bool onlyEmpty, string? userName, CancellationToken ct = default)
    {
        var floor = await db.Floors.FirstOrDefaultAsync(f => f.Id == floorId, ct)
            ?? throw new KeyNotFoundException($"Patro {floorId} neexistuje.");

        var rooms = await db.Rooms.Where(r => r.FloorId == floorId).OrderBy(r => r.Name).ToListAsync(ct);
        var readers = await db.Readers
            .Where(r => (r.Room != null && r.Room.FloorId == floorId)
                        || (r.Corridor != null && r.Corridor.FloorId == floorId))
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var result = Generate(floor, rooms, readers, onlyEmpty);

        if (result.RoomsPlaced > 0 || result.ReadersPlaced > 0)
        {
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(userName, "floor-plan-generated", "Floor", floorId.ToString(),
                result.ToString(), ct);
        }

        return result;
    }

    /// <summary>Vygeneruje plány všech pater budovy — typicky hned po importu z výkresů.</summary>
    public async Task<BuildingPlanGenerationResult> GenerateBuildingAsync(
        int buildingId, bool onlyEmpty, string? userName, CancellationToken ct = default)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == buildingId, ct)
            ?? throw new KeyNotFoundException($"Budova {buildingId} neexistuje.");

        var floorIds = await db.Floors.Where(f => f.BuildingId == buildingId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => f.Id)
            .ToListAsync(ct);

        var floors = new List<PlanGenerationResult>();
        foreach (var floorId in floorIds)
            floors.Add(await GenerateFloorAsync(floorId, onlyEmpty, userName: null, ct));

        var result = new BuildingPlanGenerationResult(building.Name, floors);
        await audit.LogAsync(userName, "building-plans-generated", "Building", buildingId.ToString(),
            result.ToString(), ct);

        return result;
    }

    /// <summary>Samotný výpočet rozvržení — bez databáze, aby se dal testovat i použít nasucho.</summary>
    internal static PlanGenerationResult Generate(
        Floor floor, List<Room> rooms, List<Reader> readers, bool onlyEmpty)
    {
        var targetRooms = onlyEmpty ? rooms.Where(r => r.PlanX is null).ToList() : rooms;
        var targetReaders = onlyEmpty ? readers.Where(r => r.SchemaX is null).ToList() : readers;
        var skipped = rooms.Count - targetRooms.Count + (readers.Count - targetReaders.Count);

        if (targetRooms.Count == 0 && targetReaders.Count == 0)
            return new PlanGenerationResult(floor.Id, floor.Name, PlanGenerationMode.Empty, 0, 0, skipped);

        // Výkresy dávají skutečné rozložení; bez nich se kreslí schéma podle chodeb.
        var mode = HasDrawing(targetRooms, targetReaders)
            ? PlanGenerationMode.FromDrawing
            : PlanGenerationMode.Schematic;

        if (mode == PlanGenerationMode.FromDrawing)
            PlaceFromDrawing(rooms, readers, targetRooms, targetReaders);
        else
            PlaceSchematic(targetRooms, targetReaders);

        return new PlanGenerationResult(floor.Id, floor.Name, mode,
            targetRooms.Count, targetReaders.Count, skipped);
    }

    private static bool HasDrawing(List<Room> rooms, List<Reader> readers)
        => rooms.Any(r => r.SourceX is not null) || readers.Any(r => r.SourceX is not null);

    // ---------- Rozvržení podle výkresů ----------

    /// <summary>
    /// Souřadnice z výkresu se přepočtou na procenta plochy. Ohraničující obdélník
    /// se počítá ze <em>všech</em> prvků patra, ne jen z generovaných — jinak by se
    /// doplňovaný prvek vešel do jiného měřítka než ten už umístěný.
    /// </summary>
    private static void PlaceFromDrawing(
        List<Room> allRooms, List<Reader> allReaders, List<Room> rooms, List<Reader> readers)
    {
        var xs = allRooms.Select(r => r.SourceX).Concat(allReaders.Select(r => r.SourceX))
            .OfType<double>().ToList();
        var ys = allRooms.Select(r => r.SourceY).Concat(allReaders.Select(r => r.SourceY))
            .OfType<double>().ToList();

        var (minX, maxX) = (xs.Min(), xs.Max());
        var (minY, maxY) = (ys.Min(), ys.Max());

        // Velikost boxu místnosti se odvodí z hustoty popisků: čím blíž jsou
        // k sobě, tím menší obdélníky, ať se nepřekrývají.
        var size = RoomSizeFor(allRooms, maxX - minX, maxY - minY);

        foreach (var room in rooms)
        {
            if (room.SourceX is null || room.SourceY is null)
                continue;

            // Popisek ve výkresu je uprostřed místnosti, proto se box vycentruje.
            room.PlanX = Clamp(Scale(room.SourceX.Value, minX, maxX) - size.Width / 2, size.Width);
            room.PlanY = Clamp(Scale(room.SourceY.Value, minY, maxY) - size.Height / 2, size.Height);
            room.PlanW = size.Width;
            room.PlanH = size.Height;
        }

        foreach (var reader in readers)
        {
            if (reader.SourceX is not null && reader.SourceY is not null)
            {
                reader.SchemaX = Clamp(Scale(reader.SourceX.Value, minX, maxX), 0);
                reader.SchemaY = Clamp(Scale(reader.SourceY.Value, minY, maxY), 0);
                continue;
            }

            // Čtečka bez polohy ve výkresu se položí do místnosti, ke které patří.
            var room = allRooms.FirstOrDefault(r => r.Id == reader.RoomId && r.PlanX is not null);
            if (room is not null)
            {
                reader.SchemaX = room.PlanX + (room.PlanW ?? size.Width) / 2;
                reader.SchemaY = room.PlanY + (room.PlanH ?? size.Height) / 2;
            }
            else
            {
                reader.SchemaX = 50;
                reader.SchemaY = 50;
            }
        }
    }

    /// <summary>
    /// Obdélníky se dimenzují podle skutečných rozestupů popisků ve výkresu
    /// (medián vzdálenosti k nejbližšímu sousedovi). Průměrná hustota by nestačila —
    /// patra mají místnosti nahloučené v křídlech a jinde volnou plochu, takže box
    /// spočítaný z průměru by se v hustých místech překrýval.
    /// </summary>
    private static (double Width, double Height) RoomSizeFor(List<Room> rooms, double spanX, double spanY)
    {
        var points = rooms
            .Where(r => r.SourceX is not null && r.SourceY is not null)
            .Select(r => (X: r.SourceX!.Value, Y: r.SourceY!.Value))
            .ToList();

        if (points.Count <= 1 || spanX <= 0 || spanY <= 0)
            return (MaxRoomSize * 0.7, MaxRoomSize * 0.55);

        // Vzdálenosti se počítají v poměru k rozpětí patra, aby nezáleželo na
        // měřítku výkresu a na tom, že osy se na plán škálují každá zvlášť.
        var spacings = new List<double>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var nearest = double.MaxValue;
            for (var j = 0; j < points.Count; j++)
            {
                if (i == j)
                    continue;

                var dx = (points[j].X - point.X) / spanX;
                var dy = (points[j].Y - point.Y) / spanY;
                var distance = dx * dx + dy * dy;
                if (distance > 0 && distance < nearest)
                    nearest = distance;
            }

            if (nearest < double.MaxValue)
                spacings.Add(Math.Sqrt(nearest));
        }

        if (spacings.Count == 0)
            return (MinRoomSize, MinRoomSize);

        spacings.Sort();
        var median = spacings[spacings.Count / 2];
        var usable = 100 - 2 * Margin;

        // 0,8 × typický rozestup nechá mezi místnostmi vidět mezeru.
        var size = Math.Clamp(median * usable * 0.8, MinRoomSize, MaxRoomSize);
        return (size, size);
    }

    /// <summary>Lineární přepočet souřadnice výkresu na procenta plochy včetně okrajů.</summary>
    private static double Scale(double value, double min, double max)
    {
        if (max - min < double.Epsilon)
            return 50;

        return Margin + (value - min) / (max - min) * (100 - 2 * Margin);
    }

    // ---------- Schematické rozvržení ----------

    /// <summary>
    /// Bez výkresů se kreslí čitelné schéma: každá chodba je jeden pás a její
    /// místnosti jdou v pásu za sebou. Místnosti bez chodby skončí v pásu navíc.
    /// </summary>
    private static void PlaceSchematic(List<Room> rooms, List<Reader> readers)
    {
        var bands = rooms
            .GroupBy(r => r.CorridorId)
            .OrderBy(g => g.Key ?? int.MaxValue)
            .ToList();

        var usable = 100 - 2 * Margin;
        var bandHeight = usable / bands.Count;
        var roomHeight = Math.Clamp(bandHeight * 0.6, MinRoomSize, MaxRoomSize);

        for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
        {
            var band = bands[bandIndex].OrderBy(r => r.Name).ToList();
            var columns = Math.Max(1, (int)Math.Ceiling(usable / (MinRoomSize + 1)));
            var perRow = Math.Min(band.Count, columns);
            var roomWidth = Math.Clamp(usable / perRow * 0.9, MinRoomSize, MaxRoomSize);
            var rowsInBand = (int)Math.Ceiling(band.Count / (double)perRow);
            var rowHeight = rowsInBand > 0 ? Math.Min(roomHeight, bandHeight / rowsInBand * 0.9) : roomHeight;

            for (var index = 0; index < band.Count; index++)
            {
                var room = band[index];
                var column = index % perRow;
                var row = index / perRow;

                room.PlanW = roomWidth;
                room.PlanH = Math.Max(rowHeight, MinRoomSize);
                room.PlanX = Clamp(Margin + column * (usable / perRow), room.PlanW.Value);
                room.PlanY = Clamp(Margin + bandIndex * bandHeight + row * (rowHeight + 0.5), room.PlanH.Value);
            }
        }

        foreach (var reader in readers)
        {
            // Čtečka místnosti sedí na její hraně (u dveří), čtečka chodby do pásu chodby.
            var room = rooms.FirstOrDefault(r => r.Id == reader.RoomId && r.PlanX is not null);
            if (room is not null)
            {
                reader.SchemaX = Math.Clamp(room.PlanX!.Value + (room.PlanW ?? MinRoomSize) / 2, 0, 100);
                reader.SchemaY = Math.Clamp(room.PlanY!.Value + (room.PlanH ?? MinRoomSize), 0, 100);
                continue;
            }

            var bandIndex = bands.FindIndex(g => g.Key == reader.CorridorId);
            if (bandIndex >= 0)
            {
                var corridorReaders = readers.Where(r => r.CorridorId == reader.CorridorId).ToList();
                var order = corridorReaders.IndexOf(reader);
                reader.SchemaX = Math.Clamp(Margin + (order + 0.5) * (usable / Math.Max(corridorReaders.Count, 1)), 0, 100);
                reader.SchemaY = Math.Clamp(Margin + (bandIndex + 1) * bandHeight - 1, 0, 100);
            }
            else
            {
                reader.SchemaX = 50;
                reader.SchemaY = 100 - Margin;
            }
        }
    }

    /// <summary>Udrží prvek celý v ploše plánu.</summary>
    private static double Clamp(double value, double size)
        => Math.Clamp(value, 0, Math.Max(0, 100 - size));
}
