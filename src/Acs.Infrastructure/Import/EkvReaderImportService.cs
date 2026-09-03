using System.Globalization;
using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Import;

/// <summary>Jeden řádek z tabulky čteček EKV (dveřní i výtahová tabulka).</summary>
public record EkvReaderRow(
    string DeviceNumber,
    string? RoomNumber,
    string? Floor,
    string Cabinet,
    string? Note,
    string? BuildingObject,
    string? Lift,
    string? Function)
{
    /// <summary>Pět číslic dveří — obě strany jedněch dveří ho mají společné.</summary>
    public string DoorNumber => DeviceNumber.Length > 1 ? DeviceNumber[..^1] : DeviceNumber;

    /// <summary>Strana dveří: 1 nebo 2.</summary>
    public string Side => DeviceNumber.Length > 0 ? DeviceNumber[^1..] : "";

    public bool IsLift => !string.IsNullOrWhiteSpace(Lift);
}

public record EkvImportResult(
    int Rows,
    int Created,
    int Updated,
    int ClaimedFromDrawing,
    int RoomsCreated,
    int Deactivated,
    int Ambiguous,
    IReadOnlyList<string> Unresolved,
    bool DryRun)
{
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(DryRun ? "Náhled importu — " : "Import dokončen — ");
        text.Append($"řádků {Rows}, nových čteček {Created}, aktualizováno {Updated} ");
        text.Append($"(z toho převzato z výkresů {ClaimedFromDrawing}), založených částí místností {RoomsCreated}, ");
        text.Append($"deaktivováno čteček z výkresů bez protějšku {Deactivated}.");
        if (Ambiguous > 0)
            text.Append($"\nMístností s více záznamy v číselníku (vybrán ten na patře podle čísla): {Ambiguous}.");
        if (Unresolved.Count > 0)
        {
            text.Append($"\nMístnosti nenalezené v číselníku ({Unresolved.Count}) — čtečky založeny bez místnosti: ");
            text.Append(string.Join(", ", Unresolved));
        }

        return text.ToString();
    }
}

/// <summary>
/// Import čteček z tabulek dokumentace skutečného provedení (DSPS, „čtečky EKV“).
///
/// Tabulka je pro čtečky autoritativní: každá má skutečné šestimístné číslo,
/// jednoznačnou místnost, do které vstupuje, a rozvaděč. Výkresy, ze kterých se
/// číselník plnil dřív, měly jen kód rozvaděče („ACS.41“) a místnost odhadnutou
/// podle nejbližšího popisku — u třetiny rozvaděčů vyšel jiný počet čteček
/// a jeden rozvaděč ve výkresech chyběl celý.
///
/// Import proto čtečky z výkresů nemaže, ale <b>sjednocuje</b>: kde se rozvaděč
/// i místnost shodují, převezme stávající záznam (žádosti na něj navázané tak
/// zůstanou), doplní mu číslo a přejmenuje ho. Co v tabulce protějšek nemá, je
/// odhad, který neseděl — takové čtečky se deaktivují a vypíšou.
/// </summary>
public class EkvReaderImportService(AcsDbContext db, AuditService audit)
{
    private const string DrawingDescriptionPrefix = "Import DPS";

    // ---------- Čtení tabulky ----------

    /// <summary>
    /// Načte řádky z .xlsx. Hlavičku hledá podle sloupce s číslem čtečky, takže
    /// nezáleží na tom, kolik řádků s názvem je nad ní, ani na pořadí sloupců —
    /// dveřní a výtahová tabulka mají sloupce v jiném pořadí.
    /// </summary>
    public static List<EkvReaderRow> Parse(Stream xlsx)
    {
        var rows = MinimalXlsxReader.ReadFirstSheet(xlsx);
        var headerIndex = rows.FindIndex(r => r.Any(c => Matches(c, "cislo ctecky")));
        if (headerIndex < 0)
            throw new InvalidDataException("V tabulce chybí sloupec „číslo čtečky EKV“.");

        var header = rows[headerIndex];
        int Column(string key) => Array.FindIndex(header, c => Matches(c, key));

        var number = Column("cislo ctecky");
        var room = Column("vstup do");
        var floor = Column("podlazi");
        var cabinet = Column("rozvadec");
        var note = Column("poznamka");
        var buildingObject = Column("stavebni objekt");
        var lift = Column("komentar");

        if (cabinet < 0)
            throw new InvalidDataException("V tabulce chybí sloupec „Rozvaděč EKV“.");

        var result = new List<EkvReaderRow>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var deviceNumber = Cell(row, number);
            // Mezisoučty („-02PP: 6“) a prázdné řádky nemají číslo čtečky.
            if (deviceNumber is null || !deviceNumber.All(char.IsDigit))
                continue;

            var liftName = Cell(row, lift);
            var roomOrFunction = Cell(row, room);
            result.Add(new EkvReaderRow(
                DeviceNumber: deviceNumber,
                // Ve výtahové tabulce je ve sloupci „vstup do m.č.“ funkce čtečky, ne místnost.
                RoomNumber: liftName is null ? roomOrFunction : null,
                Floor: Cell(row, floor),
                Cabinet: Cell(row, cabinet) ?? "",
                Note: Cell(row, note),
                BuildingObject: Cell(row, buildingObject),
                Lift: liftName,
                Function: liftName is null ? null : roomOrFunction));
        }

        return result;
    }

    private static string? Cell(string?[] row, int index)
        => index >= 0 && index < row.Length ? row[index] : null;

    /// <summary>Porovnání hlaviček bez diakritiky a velikosti písmen.</summary>
    private static bool Matches(string? cell, string key)
        => cell is not null && Fold(cell).Contains(key, StringComparison.Ordinal);

    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    // ---------- Import ----------

    /// <summary>
    /// Náhled běží stejnou cestou kódu v transakci, která se odvolá — počty
    /// odpovídají tomu, co by ostrý běh udělal.
    /// </summary>
    public async Task<EkvImportResult> ImportAsync(
        IReadOnlyList<EkvReaderRow> rows, string buildingName, bool dryRun,
        bool deactivateUnmatched, string? userName, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await RunAsync(rows, buildingName, dryRun, deactivateUnmatched, userName, ct);
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

    private async Task<EkvImportResult> RunAsync(
        IReadOnlyList<EkvReaderRow> rows, string buildingName, bool dryRun,
        bool deactivateUnmatched, string? userName, CancellationToken ct)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Name == buildingName, ct)
            ?? throw new InvalidOperationException(
                $"Budova „{buildingName}“ v číselníku není. Nejdřív naimportujte strukturu z výkresů.");

        var floorIds = await db.Floors.Where(f => f.BuildingId == building.Id).Select(f => f.Id).ToListAsync(ct);
        var rooms = await db.Rooms.Where(r => floorIds.Contains(r.FloorId)).ToListAsync(ct);
        var corridors = await db.Corridors.Where(c => floorIds.Contains(c.FloorId)).ToListAsync(ct);
        var readers = await db.Readers
            .Where(r => (r.Room != null && floorIds.Contains(r.Room.FloorId))
                        || (r.Corridor != null && floorIds.Contains(r.Corridor.FloorId))
                        || (r.RoomId == null && r.CorridorId == null))
            .ToListAsync(ct);

        var floorNames = await db.Floors.Where(f => floorIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        // Výkresy ukazují místnost i na listech sousedních pater, takže jedno číslo
        // může být v číselníku vícekrát. Vybírá se podle patra zakódovaného v čísle.
        var roomsByNumber = rooms.GroupBy(r => Number(r.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var corridorsByNumber = corridors.GroupBy(c => Number(c.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var ambiguous = 0;
        var byDevice = readers.Where(r => r.DeviceNumber is not null)
            .ToDictionary(r => r.DeviceNumber!, StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<int>();

        int created = 0, updated = 0, claimedFromDrawing = 0, roomsCreated = 0;
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            Room? room = null;
            Corridor? corridor = null;
            if (!row.IsLift && row.RoomNumber is { } roomNumber)
            {
                bool wasAmbiguous;
                (room, corridor, wasAmbiguous) = Resolve(roomNumber, roomsByNumber, corridorsByNumber, floorNames, readers, row.Cabinet);
                if (wasAmbiguous)
                    ambiguous++;
                if (room is null && corridor is null)
                {
                    // „23-00502/01“ je část místnosti 23-00502 — založí se na jejím patře.
                    room = await CreateSubRoomAsync(roomNumber, roomsByNumber, floorNames, ct);
                    if (room is not null)
                        roomsCreated++;
                    else
                        unresolved.Add(roomNumber);
                }
            }

            var reader = byDevice.GetValueOrDefault(row.DeviceNumber);
            if (reader is not null)
            {
                updated++;
            }
            else
            {
                reader = ClaimDrawingReader(readers, claimed, row, room, corridor);
                if (reader is not null)
                {
                    claimedFromDrawing++;
                    updated++;
                }
                else
                {
                    reader = new Reader { Name = "", Source = RecordSource.Manual, IsActive = true };
                    db.Readers.Add(reader);
                    readers.Add(reader);
                    created++;
                }
            }

            Apply(reader, row, room, corridor);
            byDevice[row.DeviceNumber] = reader;
        }

        await db.SaveChangesAsync(ct);

        // Deaktivace má smysl jen u dveřní tabulky celého objektu. Výtahová tabulka
        // dveřní čtečky neobsahuje — kdyby se podle ní deaktivovalo, zmizel by
        // celý číselník.
        var deactivated = 0;
        if (deactivateUnmatched && rows.Any(r => !r.IsLift))
        {
            foreach (var reader in readers.Where(IsUnclaimedDrawingReader))
            {
                reader.IsActive = false;
                reader.Description = $"{reader.Description} — v tabulce čteček EKV nenalezena, deaktivováno importem.";
                deactivated++;
            }

            await db.SaveChangesAsync(ct);
        }

        var result = new EkvImportResult(rows.Count, created, updated, claimedFromDrawing,
            roomsCreated, deactivated, ambiguous, [.. unresolved], dryRun);

        if (!dryRun)
            await audit.LogAsync(userName, "ekv-readers-imported", "Building", building.Id.ToString(), result.ToString(), ct);

        return result;
    }

    /// <summary>Číslo místnosti z jejího názvu „23-02301 — STROJOVNA“.</summary>
    internal static string Number(string name)
    {
        var separator = name.IndexOf(" — ", StringComparison.Ordinal);
        return (separator < 0 ? name : name[..separator]).Trim();
    }

    private static (Room? Room, Corridor? Corridor, bool Ambiguous) Resolve(
        string number,
        Dictionary<string, List<Room>> rooms,
        Dictionary<string, List<Corridor>> corridors,
        Dictionary<int, string> floorNames,
        List<Reader> readers,
        string cabinet)
    {
        number = number.Trim();
        if (rooms.TryGetValue(number, out var roomCandidates))
        {
            var room = Pick(roomCandidates, r => r.FloorId, r => readers.Any(x => x.RoomId == r.Id && x.Name.StartsWith(cabinet + " — ", StringComparison.OrdinalIgnoreCase)), number, floorNames);
            return (room, null, roomCandidates.Count > 1);
        }

        if (corridors.TryGetValue(number, out var corridorCandidates))
        {
            var corridor = Pick(corridorCandidates, c => c.FloorId, c => readers.Any(x => x.CorridorId == c.Id && x.Name.StartsWith(cabinet + " — ", StringComparison.OrdinalIgnoreCase)), number, floorNames);
            return (null, corridor, corridorCandidates.Count > 1);
        }

        return (null, null, false);
    }

    /// <summary>
    /// Z více záznamů téhož čísla vybere ten na patře, které je v čísle zakódované;
    /// když jich tam zbývá víc (části A/B téhož patra), dá přednost tomu, u kterého
    /// výkres ukazoval čtečku stejného rozvaděče.
    /// </summary>
    private static T Pick<T>(List<T> candidates, Func<T, int> floorId, Func<T, bool> hasCabinetEvidence,
        string number, Dictionary<int, string> floorNames)
    {
        if (candidates.Count == 1)
            return candidates[0];

        var expectedFloor = FloorLabel(number);
        var onFloor = expectedFloor is null
            ? candidates
            : candidates.Where(c => floorNames.GetValueOrDefault(floorId(c), "")
                .StartsWith(expectedFloor, StringComparison.OrdinalIgnoreCase)).ToList();
        if (onFloor.Count == 0)
            onFloor = candidates;

        return onFloor.FirstOrDefault(hasCabinetEvidence) ?? onFloor[0];
    }

    /// <summary>
    /// Číslo místnosti „23-FFxxx“ nese kód patra: 02 = 2PP, 01 = 1PP, 00 = 1NP,
    /// 10 = 2NP, 20 = 3NP … 60 = technické podlaží.
    /// </summary>
    internal static string? FloorLabel(string number)
    {
        var digits = number.Length >= 5 && number.StartsWith("23-", StringComparison.Ordinal) ? number[3..5] : null;
        return digits switch
        {
            "02" => "2PP",
            "01" => "1PP",
            "00" => "1NP",
            "10" => "2NP",
            "20" => "3NP",
            "30" => "4NP",
            "40" => "5NP",
            "50" => "6NP",
            "60" => "TP",
            _ => null,
        };
    }

    private async Task<Room?> CreateSubRoomAsync(
        string number, Dictionary<string, List<Room>> rooms, Dictionary<int, string> floorNames, CancellationToken ct)
    {
        var slash = number.IndexOf('/');
        if (slash <= 0 || !rooms.TryGetValue(number[..slash], out var parents))
            return null;

        var parent = Pick(parents, r => r.FloorId, _ => false, number[..slash], floorNames);

        var room = new Room
        {
            FloorId = parent.FloorId,
            CorridorId = parent.CorridorId,
            Name = number,
            Description = $"Část místnosti {Number(parent.Name)} (z tabulky čteček EKV)",
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync(ct);
        rooms[number] = [room];
        return room;
    }

    /// <summary>
    /// Čtečka z výkresů se jmenuje „ACS.03 — 23-02301 — NÁZEV“ (případně s „(2)“).
    /// Když se shoduje rozvaděč i místnost, jde o tutéž čtečku — převezme se,
    /// aby na ni navázané žádosti nezůstaly viset na deaktivovaném záznamu.
    /// </summary>
    private static Reader? ClaimDrawingReader(
        List<Reader> readers, HashSet<int> claimed, EkvReaderRow row, Room? room, Corridor? corridor)
    {
        if (room is null && corridor is null)
            return null;

        var prefix = $"{row.Cabinet} — ";
        var candidate = readers
            .Where(r => r.DeviceNumber is null && !claimed.Contains(r.Id) && r.Id != 0)
            .Where(r => room is not null ? r.RoomId == room.Id : r.CorridorId == corridor!.Id)
            .Where(r => r.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (candidate is not null)
            claimed.Add(candidate.Id);
        return candidate;
    }

    private static void Apply(Reader reader, EkvReaderRow row, Room? room, Corridor? corridor)
    {
        // Odkud čtečka původně přišla, se drží v popisu i při opakovaném importu.
        var previous = reader.DeviceNumber is null && reader.Description?.StartsWith(DrawingDescriptionPrefix) == true
            ? $"Výkres: {reader.Name}. "
            : DrawingOrigin(reader.Description);

        reader.DeviceNumber = row.DeviceNumber;
        reader.PanelName = row.Cabinet;
        reader.IsActive = true;
        reader.RoomId = room?.Id;
        reader.CorridorId = room is null ? corridor?.Id : null;

        if (row.IsLift)
        {
            reader.Name = $"{row.DeviceNumber} — {row.Lift} (kabina)";
            reader.Description = $"{previous}Čtečka v kabině výtahu; {row.Function}; podlaží {row.Floor}; rozvaděč {row.Cabinet}."
                + Note(row);
            return;
        }

        var place = room?.Name ?? corridor?.Name ?? row.RoomNumber ?? "";
        reader.Name = $"{row.DeviceNumber} — {place}";
        reader.Description = $"{previous}Dveře {row.DoorNumber}, strana {row.Side}; rozvaděč {row.Cabinet}; "
            + $"podlaží {row.Floor}; stavební objekt {row.BuildingObject}."
            + (room is null && corridor is null ? $" Místnost {row.RoomNumber} v číselníku nenalezena." : "")
            + Note(row);
    }

    private static string Note(EkvReaderRow row)
        => string.IsNullOrWhiteSpace(row.Note) ? "" : $" Poznámka: {row.Note}";

    /// <summary>Úvodní věta „Výkres: … .“ z dřívějšího importu, pokud tam je.</summary>
    private static string DrawingOrigin(string? description)
    {
        const string marker = "Výkres: ";
        if (description is null || !description.StartsWith(marker, StringComparison.Ordinal))
            return "";

        var end = description.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? description + " " : description[..(end + 2)];
    }

    private static bool IsUnclaimedDrawingReader(Reader reader)
        => reader.DeviceNumber is null
           && reader.IsActive
           && reader.Description?.StartsWith(DrawingDescriptionPrefix, StringComparison.Ordinal) == true;
}
