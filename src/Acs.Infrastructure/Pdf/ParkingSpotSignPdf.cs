using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Acs.Infrastructure.Pdf;

/// <summary>
/// Cedule A4 (na výšku) „VYHRAZENÉ PARKOVÁNÍ“ na konkrétní parkovací místo — podle předlohy FNMH:
/// modrý rám, logo, dopravní značka „P“, nadpis, řádek s označením místa a registrační značky
/// vykreslené jako skutečné SPZ (EU pruh s dvanácti hvězdami a „CZ“). Vše se kreslí vektorově.
/// Do pěti značek jeden sloupec, do dvanácti dva sloupce, víc pokračuje na další stránce.
/// </summary>
public static class ParkingSpotSignPdf
{
    public const double WidthMm = 210;
    public const double HeightMm = 297;

    /// <summary>Kolik řádků (SPZ) se vejde na jednu stránku.</summary>
    public const int RowsPerPage = 12;
    private const int SingleColumnMax = 5;

    private static readonly XColor Blue = BrandAssets.Blue;
    private static readonly XColor Ink = XColor.FromArgb(0x11, 0x11, 0x11);
    private static readonly XColor Gray = XColor.FromArgb(0x4A, 0x55, 0x63);
    private static readonly XColor GrayLight = XColor.FromArgb(0x63, 0x70, 0x7E);
    private static readonly XColor EuBlue = XColor.FromArgb(0x00, 0x33, 0x99);
    private static readonly XColor EuYellow = XColor.FromArgb(0xFF, 0xCC, 0x00);

    public static byte[] Render(ParkingSpotSignView sign) => Render([sign]);

    /// <summary>Vygeneruje PDF — každé místo na vlastní stránce (stránkách).</summary>
    public static byte[] Render(IReadOnlyList<ParkingSpotSignView> signs)
    {
        if (signs.Count == 0)
            throw new ArgumentException("Není co tisknout — žádné parkovací místo.", nameof(signs));

        SystemFontResolver.EnsureRegistered();

        using var document = new PdfDocument();
        document.Info.Title = signs.Count == 1
            ? $"Vyhrazené parkování — místo {signs[0].SpotCode} ({signs[0].SiteName})"
            : $"Vyhrazené parkování — {signs.Count} míst";
        document.Info.Author = "ACS FNMH";

        using var logo = BrandAssets.Logo();

        foreach (var sign in signs)
        {
            var pages = Math.Max(1, (sign.Rows.Count + RowsPerPage - 1) / RowsPerPage);
            for (var pageIndex = 0; pageIndex < pages; pageIndex++)
            {
                var page = document.AddPage();
                page.Width = XUnit.FromMillimeter(WidthMm);
                page.Height = XUnit.FromMillimeter(HeightMm);
                using var gfx = XGraphics.FromPdfPage(page);
                var rows = sign.Rows.Skip(pageIndex * RowsPerPage).Take(RowsPerPage).ToList();
                DrawPage(gfx, sign, rows, logo, pageIndex + 1, pages);
            }
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static void DrawPage(XGraphics gfx, ParkingSpotSignView sign, IReadOnlyList<ParkingSignRow> rows,
        XImage logo, int pageNumber, int pageCount)
    {
        var w = PdfText.Mm(WidthMm);
        var h = PdfText.Mm(HeightMm);
        var margin = PdfText.Mm(18);
        var bodyX = margin;
        var bodyW = w - 2 * margin;
        var blueBrush = new XSolidBrush(Blue);

        // Rám.
        var frameInset = PdfText.Mm(3);
        gfx.DrawRectangle(new XPen(Blue, PdfText.Mm(1.4)),
            frameInset, frameInset, w - 2 * frameInset, h - 2 * frameInset);

        // Logo FNMH a oddělovací linka.
        BrandAssets.DrawFitted(gfx, logo, new XRect(bodyX, PdfText.Mm(16), bodyW, PdfText.Mm(30)));
        var lineY = PdfText.Mm(56);
        gfx.DrawLine(new XPen(Blue, PdfText.Mm(0.6)), PdfText.Mm(25), lineY, w - PdfText.Mm(25), lineY);

        // Dopravní značka „P“ (modrý zaoblený čtverec, bílé P).
        var signSize = PdfText.Mm(58);
        var signRect = new XRect((w - signSize) / 2, PdfText.Mm(74), signSize, signSize);
        gfx.DrawRoundedRectangle(blueBrush, signRect, new XSize(PdfText.Mm(7), PdfText.Mm(7)));
        gfx.DrawString("P", PdfText.Font(150, bold: true), XBrushes.White, signRect, XStringFormats.Center);

        // Nadpis, podtitul, označení místa.
        var titleFont = FitFont("VYHRAZENÉ PARKOVÁNÍ", 36, bold: true, gfx, bodyW);
        var titleY = signRect.Bottom + PdfText.Mm(6);
        gfx.DrawString("VYHRAZENÉ PARKOVÁNÍ", titleFont, blueBrush,
            new XRect(bodyX, titleY, bodyW, titleFont.GetHeight()), XStringFormats.TopCenter);

        var subtitleFont = PdfText.Font(13);
        var subtitleY = titleY + titleFont.GetHeight() + PdfText.Mm(0.5);
        gfx.DrawString("pouze pro vozidla s registrační značkou", subtitleFont, new XSolidBrush(Gray),
            new XRect(bodyX, subtitleY, bodyW, subtitleFont.GetHeight()), XStringFormats.TopCenter);

        var spotFont = PdfText.Font(10, bold: true);
        var spotY = subtitleY + subtitleFont.GetHeight() + PdfText.Mm(1.5);
        gfx.DrawString(sign.SpotText(), spotFont, blueBrush,
            new XRect(bodyX, spotY, bodyW, spotFont.GetHeight()), XStringFormats.TopCenter);

        // Patička.
        var footerFont = PdfText.Font(9);
        var footerSmallFont = PdfText.Font(7.5);
        var footerBrush = new XSolidBrush(GrayLight);
        var bottom = h - PdfText.Mm(14);
        gfx.DrawString("Neoprávněně zaparkovaná vozidla budou odtažena na náklady provozovatele vozidla.",
            footerSmallFont, footerBrush,
            new XRect(bodyX, bottom - footerSmallFont.GetHeight(), bodyW, footerSmallFont.GetHeight()), XStringFormats.TopCenter);
        var footerLineY = bottom - footerSmallFont.GetHeight() - footerFont.GetHeight() - PdfText.Mm(1);
        gfx.DrawString("Fakultní nemocnice Motol a Homolka (FNMH)", footerFont, footerBrush,
            new XRect(bodyX, footerLineY, bodyW, footerFont.GetHeight()), XStringFormats.TopCenter);
        if (pageCount > 1)
        {
            gfx.DrawString($"strana {pageNumber}/{pageCount}", footerSmallFont, footerBrush,
                new XRect(bodyX, footerLineY - footerSmallFont.GetHeight() - PdfText.Mm(1), bodyW, footerSmallFont.GetHeight()),
                XStringFormats.TopCenter);
        }

        // Oblast pro registrační značky.
        var areaTop = spotY + spotFont.GetHeight() + PdfText.Mm(7);
        var areaBottom = footerLineY - PdfText.Mm(8);
        var area = new XRect(bodyX, areaTop, bodyW, areaBottom - areaTop);
        DrawRows(gfx, rows, area);
    }

    private static void DrawRows(XGraphics gfx, IReadOnlyList<ParkingSignRow> rows, XRect area)
    {
        if (rows.Count == 0)
        {
            var emptyFont = PdfText.Font(12);
            gfx.DrawString("zatím bez přiřazeného vozidla", emptyFont, new XSolidBrush(GrayLight),
                new XRect(area.X, area.Y + PdfText.Mm(10), area.Width, emptyFont.GetHeight()), XStringFormats.TopCenter);
            return;
        }

        var columns = rows.Count <= SingleColumnMax ? 1 : 2;
        var rowsPerColumn = (rows.Count + columns - 1) / columns;
        var columnGap = PdfText.Mm(6);
        var columnWidth = (area.Width - (columns - 1) * columnGap) / columns;

        // Výška značky podle volného místa; poměr stran ≈ 4,5 : 1 jako u skutečné SPZ.
        var slot = area.Height / rowsPerColumn;
        var plateHeight = Math.Min(PdfText.Mm(29), slot * 0.84);
        var plateWidth = Math.Min(plateHeight * 4.5, columnWidth);
        plateHeight = plateWidth / 4.5;
        var gap = Math.Min(PdfText.Mm(7), slot - plateHeight);
        var blockHeight = rowsPerColumn * plateHeight + (rowsPerColumn - 1) * gap;
        var startY = area.Y + Math.Max(0, (area.Height - blockHeight) / 2) * 0.35;

        for (var i = 0; i < rows.Count; i++)
        {
            var column = i / rowsPerColumn;
            var rowInColumn = i % rowsPerColumn;
            var columnX = area.X + column * (columnWidth + columnGap);
            var x = columnX + (columnWidth - plateWidth) / 2;
            var y = startY + rowInColumn * (plateHeight + gap);
            var rect = new XRect(x, y, plateWidth, plateHeight);
            if (rows[i].IsPlate)
                DrawPlate(gfx, rows[i].Text, rect);
            else
                DrawFunctionRow(gfx, rows[i].Text, rect);
        }
    }

    /// <summary>Registrační značka: bílé pole s černým rámem, vlevo modrý EU pruh s hvězdami a „CZ“.</summary>
    private static void DrawPlate(XGraphics gfx, string text, XRect rect)
    {
        var radius = rect.Height * 0.12;
        var borderWidth = Math.Max(PdfText.Mm(0.5), rect.Height * 0.035);
        gfx.DrawRoundedRectangle(new XPen(Ink, borderWidth), XBrushes.White, rect, new XSize(radius, radius));

        // Modrý pruh — vlevo zaoblený stejně jako rám, vpravo rovný (ořez přes stav grafiky).
        var bandWidth = rect.Width * 0.115;
        var band = new XRect(rect.X, rect.Y, bandWidth, rect.Height);
        var state = gfx.Save();
        gfx.IntersectClip(band);
        gfx.DrawRoundedRectangle(new XSolidBrush(EuBlue),
            new XRect(rect.X, rect.Y, rect.Width, rect.Height), new XSize(radius, radius));
        gfx.Restore(state);

        // Dvanáct hvězd do kruhu v horní části pruhu.
        var starBrush = new XSolidBrush(EuYellow);
        var circleCenter = new XPoint(band.X + band.Width / 2, band.Y + band.Height * 0.36);
        var circleRadius = band.Width * 0.30;
        var starOuter = band.Width * 0.062;
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Math.PI / 6;
            var center = new XPoint(circleCenter.X + circleRadius * Math.Sin(angle),
                circleCenter.Y - circleRadius * Math.Cos(angle));
            gfx.DrawPolygon(starBrush, StarPoints(center, starOuter), XFillMode.Winding);
        }

        // „CZ“ pod hvězdami.
        var czFont = PdfText.Font(Math.Max(5, band.Width * 0.40), bold: true);
        gfx.DrawString("CZ", czFont, XBrushes.White,
            new XRect(band.X, band.Y + band.Height * 0.62, band.Width, band.Height * 0.32), XStringFormats.Center);

        // Text značky — vycentrovaný v bílé části, zmenší se, kdyby se nevešel.
        var textArea = new XRect(band.Right + rect.Width * 0.03, rect.Y, rect.Width - bandWidth - rect.Width * 0.06, rect.Height);
        // Velikost písma v bodech ≈ 0,72 výšky pole (výška verzálek je zhruba 0,72 velikosti písma).
        var plateFont = FitFont(text, rect.Height * 0.72, bold: true, gfx, textArea.Width);
        gfx.DrawString(text, plateFont, new XSolidBrush(Ink), textArea, XStringFormats.Center);
    }

    /// <summary>Řádek přenosného povolení (na funkci) — bez EU pruhu, modrý rám a název funkce.</summary>
    private static void DrawFunctionRow(XGraphics gfx, string text, XRect rect)
    {
        var radius = rect.Height * 0.12;
        gfx.DrawRoundedRectangle(new XPen(Blue, Math.Max(PdfText.Mm(0.5), rect.Height * 0.035)), XBrushes.White,
            rect, new XSize(radius, radius));
        var inner = new XRect(rect.X + rect.Width * 0.04, rect.Y, rect.Width * 0.92, rect.Height);
        var font = FitFont(text, rect.Height * 0.42, bold: true, gfx, inner.Width);
        gfx.DrawString(text, font, new XSolidBrush(Blue), inner, XStringFormats.Center);
    }

    /// <summary>Písmo dané velikosti, zmenšené tak, aby se text vešel na šířku.</summary>
    private static XFont FitFont(string text, double sizePt, bool bold, XGraphics gfx, double maxWidth)
    {
        var size = sizePt;
        var font = PdfText.Font(size, bold);
        while (size > 6 && gfx.MeasureString(text, font).Width > maxWidth)
        {
            size -= 1;
            font = PdfText.Font(size, bold);
        }

        return font;
    }

    /// <summary>Pěticípá hvězda (10 vrcholů) se středem a vnějším poloměrem; cíp směřuje nahoru.</summary>
    private static XPoint[] StarPoints(XPoint center, double outer)
    {
        var inner = outer * 0.42;
        var points = new XPoint[10];
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            points[i] = new XPoint(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        }

        return points;
    }
}
