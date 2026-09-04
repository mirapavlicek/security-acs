using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Acs.Infrastructure.Pdf;

/// <summary>
/// Kartička parkovacího povolení za čelní sklo jako PDF — rozměr 150 × 70 mm podle
/// předlohy FNMH: modrý pruh s „P“ vlevo, logo FNMH, nadpis, druh povolení velkými
/// písmeny, rozsah areálů, SPZ (nebo funkce), číslo a platnost, pokyn k umístění.
/// Jedna stránka = jedna kartička; více povolení lze spojit do jednoho souboru.
/// </summary>
public static class PermitCardPdf
{
    public const double WidthMm = 150;
    public const double HeightMm = 70;

    private static readonly XColor Blue = BrandAssets.Blue;
    private static readonly XColor Ink = XColor.FromArgb(0x1C, 0x27, 0x33);
    private static readonly XColor Gray = XColor.FromArgb(0x4A, 0x55, 0x63);
    private static readonly XColor GrayLight = XColor.FromArgb(0x63, 0x70, 0x7E);

    public static byte[] Render(PermitCardView card) => Render([card]);

    /// <summary>Vygeneruje PDF s jednou kartičkou na stránku.</summary>
    public static byte[] Render(IReadOnlyList<PermitCardView> cards)
    {
        if (cards.Count == 0)
            throw new ArgumentException("Není co tisknout — žádná kartička.", nameof(cards));

        SystemFontResolver.EnsureRegistered();

        using var document = new PdfDocument();
        document.Info.Title = cards.Count == 1
            ? $"Parkovací povolení {cards[0].PermitNumber ?? cards[0].TypeName}"
            : $"Parkovací povolení ({cards.Count} kartiček)";
        document.Info.Author = "ACS FNMH";

        // Obrázky se v dokumentu sdílí — jeden XImage pro všechny stránky.
        using var logo = BrandAssets.Logo();
        using var watermark = BrandAssets.MonogramWatermark();

        foreach (var card in cards)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(WidthMm);
            page.Height = XUnit.FromMillimeter(HeightMm);
            using var gfx = XGraphics.FromPdfPage(page);
            DrawCard(gfx, card, logo, watermark);
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static void DrawCard(XGraphics gfx, PermitCardView card, XImage logo, XImage watermark)
    {
        var w = PdfText.Mm(WidthMm);
        var h = PdfText.Mm(HeightMm);
        var border = PdfText.Mm(0.6);
        var bandWidth = w * 0.24;

        // Rám a levý pruh s „P“.
        gfx.DrawRectangle(new XPen(Blue, border), border / 2, border / 2, w - border, h - border);
        gfx.DrawRectangle(new XSolidBrush(Blue), 0, 0, bandWidth, h);
        gfx.DrawString("P", PdfText.Font(96, bold: true), XBrushes.White,
            new XRect(0, 0, bandWidth, h), XStringFormats.Center);

        // Vodoznak — monogram FNMH z grafického manuálu, vpravo, přes celou výšku.
        BrandAssets.DrawFitted(gfx, watermark,
            new XRect(w - PdfText.Mm(58), PdfText.Mm(4), PdfText.Mm(55), h - PdfText.Mm(8)), XStringAlignment.Far);

        // Tělo kartičky (vpravo od pruhu).
        var bodyX = bandWidth + PdfText.Mm(4);
        var bodyW = w - bodyX - PdfText.Mm(4);
        var top = PdfText.Mm(3);
        var bottom = h - PdfText.Mm(2.5);

        // Logo FNMH (modrý monogram s názvem) — originál z grafického manuálu, vycentrované.
        var logoBox = BrandAssets.DrawFitted(gfx, logo, new XRect(bodyX, top, bodyW, PdfText.Mm(14)));
        var blueBrush = new XSolidBrush(Blue);
        var logoBottom = logoBox.Bottom + PdfText.Mm(1);

        // Patička (dva řádky) — kreslí se od spodu, aby se vědělo, kolik místa zbývá.
        var footerFont = PdfText.Font(7);
        var footerLine = footerFont.GetHeight() * 1.25;
        var footerBrush = new XSolidBrush(GrayLight);
        gfx.DrawString("Fakultní nemocnice Motol a Homolka (FNMH)", footerFont, footerBrush,
            new XRect(bodyX, bottom - footerLine, bodyW, footerLine), XStringFormats.Center);
        gfx.DrawString("Umístěte viditelně za čelní sklo vozidla.", footerFont, footerBrush,
            new XRect(bodyX, bottom - 2 * footerLine, bodyW, footerLine), XStringFormats.Center);
        var footerTop = bottom - 2 * footerLine;

        // Střední blok: nadpis, druh, (funkce), rozsah, (SPZ), meta — vycentrovaný svisle mezi logem a patičkou.
        var titleFont = PdfText.Font(16, bold: true);
        var typeFont = PdfText.Font(12.5, bold: true);
        var functionFont = PdfText.Font(9);
        var scopeFont = PdfText.Font(8.2);
        var plateFont = PdfText.Font(14, bold: true);
        var metaFont = PdfText.Font(7.4);

        var scopeLines = PdfText.Wrap(gfx, card.ScopeText, scopeFont, bodyW);
        var blockHeight = titleFont.GetHeight() + PdfText.Mm(0.8) + typeFont.GetHeight()
            + (card.FunctionTitle is null ? 0 : PdfText.Mm(0.6) + functionFont.GetHeight())
            + PdfText.Mm(1.6) + scopeLines.Count * scopeFont.GetHeight() * 1.15
            + (card.Plates is null ? 0 : PdfText.Mm(1.6) + plateFont.GetHeight() + PdfText.Mm(1.2))
            + PdfText.Mm(1.4) + metaFont.GetHeight();
        var y = logoBottom + Math.Max(PdfText.Mm(1.5), (footerTop - logoBottom - blockHeight) / 2);

        gfx.DrawString(card.Title, titleFont, blueBrush, new XRect(bodyX, y, bodyW, titleFont.GetHeight()), XStringFormats.TopCenter);
        y += titleFont.GetHeight() + PdfText.Mm(0.8);

        var inkBrush = new XSolidBrush(Ink);
        gfx.DrawString(card.TypeName.ToUpperInvariant(), typeFont, inkBrush,
            new XRect(bodyX, y, bodyW, typeFont.GetHeight()), XStringFormats.TopCenter);
        y += typeFont.GetHeight();

        if (card.FunctionTitle is not null)
        {
            y += PdfText.Mm(0.6);
            var functionText = card.HolderName is null ? card.FunctionTitle : $"{card.FunctionTitle} — {card.HolderName}";
            gfx.DrawString(functionText, functionFont, inkBrush,
                new XRect(bodyX, y, bodyW, functionFont.GetHeight()), XStringFormats.TopCenter);
            y += functionFont.GetHeight();
        }

        y += PdfText.Mm(1.6);
        var grayBrush = new XSolidBrush(Gray);
        y += PdfText.DrawWrapped(gfx, card.ScopeText, scopeFont, grayBrush,
            new XRect(bodyX, y, bodyW, scopeLines.Count * scopeFont.GetHeight() * 1.15), XStringAlignment.Center, 1.15);

        if (card.Plates is not null)
        {
            y += PdfText.Mm(1.6);
            var plateText = string.Join("   ", card.Plates.Split("  ·  "));
            var plateWidth = gfx.MeasureString(plateText, plateFont).Width + PdfText.Mm(6);
            var plateHeight = plateFont.GetHeight() + PdfText.Mm(1.2);
            var plateX = bodyX + (bodyW - plateWidth) / 2;
            gfx.DrawRoundedRectangle(new XPen(Ink, PdfText.Mm(0.4)), XBrushes.White,
                new XRect(plateX, y, plateWidth, plateHeight), new XSize(PdfText.Mm(1), PdfText.Mm(1)));
            gfx.DrawString(plateText, plateFont, inkBrush, new XRect(plateX, y, plateWidth, plateHeight), XStringFormats.Center);
            y += plateHeight;
        }

        y += PdfText.Mm(1.4);
        var meta = new List<string>();
        if (card.PermitNumber is not null)
            meta.Add($"č. {card.PermitNumber}");
        meta.Add(card.ValidTo is { } to ? $"platí do {to:d. M. yyyy}" : "platnost bez omezení");
        gfx.DrawString(string.Join("      ", meta), metaFont, grayBrush,
            new XRect(bodyX, y, bodyW, metaFont.GetHeight()), XStringFormats.TopCenter);
    }
}
