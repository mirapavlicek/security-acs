using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Acs.Infrastructure.Import;

/// <summary>
/// Přečte buňky prvního listu ze souboru .xlsx.
///
/// Tabulky od projektantů jsou prosté: jeden list, text a čísla, žádné vzorce
/// ani formátování, na kterém by záleželo. Na to není potřeba tahat celou
/// knihovnu pro Excel — .xlsx je zip s několika XML soubory a tohle z nich
/// vytáhne hodnoty. Vzorce se berou jako uložený výsledek, data jako číslo.
/// </summary>
public static class MinimalXlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Řádky prvního listu; každá buňka jako text, prázdné jako null.</summary>
    public static List<string?[]> ReadFirstSheet(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var sharedStrings = ReadSharedStrings(zip);
        var sheetPath = FirstSheetPath(zip);
        var sheet = Load(zip, sheetPath)
            ?? throw new InvalidDataException("Soubor neobsahuje žádný list.");

        var rows = new List<string?[]>();
        foreach (var row in sheet.Descendants(Main + "row"))
        {
            var cells = new List<(int Column, string? Value)>();
            var nextColumn = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                // Buňky bez adresy jdou za sebou; s adresou se bere sloupec z ní.
                var reference = (string?)cell.Attribute("r");
                var column = reference is null ? nextColumn : ColumnIndex(reference);
                nextColumn = column + 1;
                cells.Add((column, CellValue(cell, sharedStrings)));
            }

            if (cells.Count == 0)
            {
                rows.Add([]);
                continue;
            }

            var values = new string?[cells.Max(c => c.Column) + 1];
            foreach (var (column, value) in cells)
                values[column] = value;
            rows.Add(values);
        }

        return rows;
    }

    private static string? CellValue(XElement cell, List<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        var raw = cell.Element(Main + "v")?.Value;

        switch (type)
        {
            case "s":
                return int.TryParse(raw, out var index) && index < sharedStrings.Count
                    ? Clean(sharedStrings[index])
                    : null;
            case "inlineStr":
                return Clean(string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)));
            case "b":
                return raw == "1" ? "true" : "false";
            case "str":
            case "e":
                return Clean(raw);
        }

        if (raw is null)
            return null;

        // Čísla: celé bez desetinné části („362002“, ne „362002.0“), ostatní invariantně.
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return Math.Abs(number - Math.Round(number)) < 1e-9 && Math.Abs(number) < 1e15
                ? ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture)
                : number.ToString("0.############", CultureInfo.InvariantCulture);
        }

        return Clean(raw);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var doc = Load(zip, "xl/sharedStrings.xml");
        if (doc is null)
            return [];

        // Formátovaný text je rozsekaný do <r><t>…</t></r>; spojí se dohromady.
        return doc.Root!.Elements(Main + "si")
            .Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var relationships = Load(zip, "xl/_rels/workbook.xml.rels");

        var firstSheet = workbook?.Descendants(Main + "sheet").FirstOrDefault();
        var relationshipId = (string?)firstSheet?.Attribute(Rel + "id");
        var target = relationships?.Descendants(Pkg + "Relationship")
            .FirstOrDefault(r => (string?)r.Attribute("Id") == relationshipId)
            ?.Attribute("Target")?.Value;

        if (target is null)
            return "xl/worksheets/sheet1.xml";

        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
            return null;

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>„A“ → 0, „B“ → 1, „AA“ → 26 — z adresy buňky typu „C12“.</summary>
    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch))
                break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return index - 1;
    }
}
