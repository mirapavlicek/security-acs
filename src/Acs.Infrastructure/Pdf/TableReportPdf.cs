using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Acs.Infrastructure.Pdf;

/// <summary>Sloupec tabulkového reportu: hlavička a relativní šířka (podíl na řádku).</summary>
public record PdfColumn(string Header, double Weight = 1);

/// <summary>
/// Tabulkový report do PDF (A4 na šířku): název, podtitul, hlavička tabulky opakovaná
/// na každé stránce, zalamování buněk, číslování stránek. Používají ho reporty přístupů
/// a parkovacích povolení.
/// </summary>
public static class TableReportPdf
{
    private static readonly XColor Ink = XColor.FromArgb(0x1C, 0x27, 0x33);
    private static readonly XColor Muted = XColor.FromArgb(0x63, 0x70, 0x7E);
    private static readonly XColor HeaderFill = XColor.FromArgb(0xEA, 0xF0, 0xF8);
    private static readonly XColor Rule = XColor.FromArgb(0xD8, 0xDE, 0xE6);
    private static readonly XColor Accent = BrandAssets.Blue;

    /// <summary>
    /// Vygeneruje report. <paramref name="sections"/> umožňuje rozdělit řádky do
    /// pojmenovaných bloků (např. per čtečka); prázdný název sekce = bez mezititulku.
    /// </summary>
    public static byte[] Render(string title, string? subtitle, IReadOnlyList<PdfColumn> columns,
        IReadOnlyList<(string Section, IReadOnlyList<string?[]> Rows)> sections)
    {
        SystemFontResolver.EnsureRegistered();

        using var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Author = "ACS FNMH";

        var titleFont = PdfText.Font(15, bold: true);
        var subtitleFont = PdfText.Font(9);
        var sectionFont = PdfText.Font(10.5, bold: true);
        var headerFont = PdfText.Font(8.5, bold: true);
        var cellFont = PdfText.Font(8.5);
        var footerFont = PdfText.Font(7.5);

        var margin = PdfText.Mm(14);
        var cellPadding = PdfText.Mm(1.6);
        var totalWeight = columns.Sum(c => c.Weight);

        using var logo = BrandAssets.Logo();

        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0, contentWidth = 0, pageBottom = 0;
        var pageNumber = 0;
        var pages = new List<(PdfPage Page, XGraphics Gfx)>();

        void NewPage()
        {
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            page.Orientation = PdfSharp.PageOrientation.Landscape;
            gfx = XGraphics.FromPdfPage(page);
            pages.Add((page, gfx));
            pageNumber++;
            contentWidth = page.Width.Point - 2 * margin;
            pageBottom = page.Height.Point - margin - footerFont.GetHeight() - PdfText.Mm(3);
            y = margin;

            if (pageNumber == 1)
            {
                // Logo FNMH vpravo v hlavičce, název a podtitul vlevo.
                var logoHeight = PdfText.Mm(12);
                BrandAssets.DrawFitted(gfx, logo,
                    new XRect(margin + contentWidth - PdfText.Mm(40), y, PdfText.Mm(40), logoHeight), XStringAlignment.Far);
                var textWidth = contentWidth - PdfText.Mm(44);
                gfx.DrawString(title, titleFont, new XSolidBrush(Accent),
                    new XRect(margin, y, textWidth, titleFont.GetHeight()), XStringFormats.TopLeft);
                var generated = $"Vygenerováno {DateTime.Now:d. M. yyyy H:mm}" + (subtitle is null ? "" : $" · {subtitle}");
                gfx.DrawString(generated, subtitleFont, new XSolidBrush(Muted),
                    new XRect(margin, y + titleFont.GetHeight() + PdfText.Mm(1), textWidth, subtitleFont.GetHeight()), XStringFormats.TopLeft);
                y += Math.Max(logoHeight, titleFont.GetHeight() + PdfText.Mm(1) + subtitleFont.GetHeight()) + PdfText.Mm(4);
            }
        }

        double[] ColumnWidths() => columns.Select(c => contentWidth * c.Weight / totalWeight).ToArray();

        void DrawHeader()
        {
            var widths = ColumnWidths();
            var height = headerFont.GetHeight() + 2 * cellPadding;
            gfx!.DrawRectangle(new XSolidBrush(HeaderFill), margin, y, contentWidth, height);
            var x = margin;
            for (var i = 0; i < columns.Count; i++)
            {
                gfx.DrawString(columns[i].Header, headerFont, new XSolidBrush(Muted),
                    new XRect(x + cellPadding, y + cellPadding, widths[i] - 2 * cellPadding, headerFont.GetHeight()),
                    XStringFormats.TopLeft);
                x += widths[i];
            }

            y += height;
            gfx.DrawLine(new XPen(Rule, 0.6), margin, y, margin + contentWidth, y);
        }

        NewPage();
        var anyRows = false;

        foreach (var (section, rows) in sections)
        {
            if (rows.Count == 0)
                continue;
            anyRows = true;

            var sectionHeight = string.IsNullOrEmpty(section) ? 0 : sectionFont.GetHeight() + PdfText.Mm(2);
            if (y + sectionHeight + headerFont.GetHeight() + 3 * cellFont.GetHeight() > pageBottom)
                NewPage();

            if (!string.IsNullOrEmpty(section))
            {
                y += PdfText.Mm(2);
                gfx!.DrawString(section, sectionFont, new XSolidBrush(Ink),
                    new XRect(margin, y, contentWidth, sectionFont.GetHeight()), XStringFormats.TopLeft);
                y += sectionFont.GetHeight() + PdfText.Mm(1);
            }

            DrawHeader();

            foreach (var row in rows)
            {
                var widths = ColumnWidths();
                var wrapped = new List<string>[columns.Count];
                var lineCount = 1;
                for (var i = 0; i < columns.Count; i++)
                {
                    wrapped[i] = PdfText.Wrap(gfx!, i < row.Length ? row[i] ?? "" : "", cellFont, widths[i] - 2 * cellPadding);
                    lineCount = Math.Max(lineCount, wrapped[i].Count);
                }

                var lineHeight = cellFont.GetHeight() * 1.15;
                var rowHeight = lineCount * lineHeight + 2 * cellPadding;
                if (y + rowHeight > pageBottom)
                {
                    NewPage();
                    DrawHeader();
                }

                var x = margin;
                for (var i = 0; i < columns.Count; i++)
                {
                    var ty = y + cellPadding;
                    foreach (var line in wrapped[i])
                    {
                        gfx!.DrawString(line, cellFont, new XSolidBrush(Ink),
                            new XRect(x + cellPadding, ty, widths[i] - 2 * cellPadding, lineHeight), XStringFormats.TopLeft);
                        ty += lineHeight;
                    }

                    x += widths[i];
                }

                y += rowHeight;
                gfx!.DrawLine(new XPen(Rule, 0.4), margin, y, margin + contentWidth, y);
            }
        }

        if (!anyRows)
        {
            gfx!.DrawString("Žádné záznamy.", cellFont, new XSolidBrush(Muted),
                new XRect(margin, y, contentWidth, cellFont.GetHeight()), XStringFormats.TopLeft);
        }

        // Číslování stránek až na konci, kdy je znám celkový počet.
        for (var i = 0; i < pages.Count; i++)
        {
            var (p, g) = pages[i];
            g.DrawString($"{title} · strana {i + 1} / {pages.Count}", footerFont, new XSolidBrush(Muted),
                new XRect(margin, p.Height.Point - margin - footerFont.GetHeight(), contentWidth, footerFont.GetHeight()),
                XStringFormats.TopRight);
            g.Dispose();
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }
}
