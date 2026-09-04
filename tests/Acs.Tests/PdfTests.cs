using System.Text;
using Acs.Domain.Entities;
using Acs.Infrastructure.Pdf;
using Xunit;

namespace Acs.Tests;

/// <summary>Generování PDF: kartičky parkovacích povolení a tabulkové reporty.</summary>
public class PdfTests
{
    private static bool IsPdf(byte[] bytes)
        => bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-"
           && Encoding.ASCII.GetString(bytes, bytes.Length - 7, 7).Contains("%%EOF");

    private static int PageCount(byte[] bytes)
    {
        // Počet objektů typu /Page (ne /Pages) — stačí pro kontrolu počtu stránek.
        var text = Encoding.Latin1.GetString(bytes);
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("/Type/Page", index, StringComparison.Ordinal)) >= 0)
        {
            index += "/Type/Page".Length;
            if (index < text.Length && text[index] != 's')
                count++;
        }

        return count;
    }

    [Fact]
    public void SystemFonts_AreAvailable()
    {
        // Bez písma s diakritikou by PDF nešlo vygenerovat — CI i nody mají DejaVu Sans.
        var fonts = SystemFontResolver.FindFontFiles();
        Assert.NotNull(fonts);
        Assert.True(File.Exists(fonts.Value.Regular));
        Assert.True(File.Exists(fonts.Value.Bold));
    }

    [Fact]
    public void BrandAssets_LogoAndMonogram_AreEmbedded()
    {
        using var logo = BrandAssets.Logo();
        using var grey = BrandAssets.MonogramGrey();
        using var watermark = BrandAssets.MonogramWatermark();

        // Logo s textem je na šířku (≈ 2,8 : 1), monogram skoro čtvercový.
        Assert.InRange(logo.PixelWidth / (double)logo.PixelHeight, 2.6, 3.0);
        Assert.InRange(grey.PixelWidth / (double)grey.PixelHeight, 1.0, 1.25);
        Assert.Equal(grey.PixelWidth, watermark.PixelWidth);
    }

    [Fact]
    public void PermitCard_PlateBinding_RendersSinglePage()
    {
        var card = new PermitCardView(
            Title: "POVOLENÍ K PARKOVÁNÍ", TypeName: "Zaměstnanec",
            ScopeText: "Platí pro všechna pracoviště FNMH (Motol i Homolka)",
            Plates: "1AB2345  ·  2CD3456", FunctionTitle: null,
            PermitNumber: "P-2026-0001", ValidTo: new DateTime(2027, 9, 3), HolderName: null);

        var pdf = PermitCardPdf.Render(card);

        Assert.True(IsPdf(pdf));
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void PermitCard_FunctionBinding_WithLongScope_Renders()
    {
        var card = new PermitCardView(
            Title: "POVOLENÍ K PARKOVÁNÍ", TypeName: "Vedení nemocnice",
            ScopeText: "Platí pro areál: Motol, Homolka, Detašované pracoviště Řepy a všechna další pracoviště, "
                       + "která nemocnice provozuje včetně příležitostných akcí",
            Plates: null, FunctionTitle: "Náměstek pro léčebnou péči",
            PermitNumber: null, ValidTo: null, HolderName: "MUDr. Jan Novák, Ph.D.");

        var pdf = PermitCardPdf.Render(card);
        Assert.True(IsPdf(pdf));
    }

    [Fact]
    public void PermitCards_Batch_OnePagePerCard()
    {
        var type = new ParkingPermitType { Name = "Dodavatel", CardScopeText = "Platí pro areál Motol" };
        var cards = Enumerable.Range(1, 3).Select(_ => PermitCardView.Sample(type)).ToList();

        var pdf = PermitCardPdf.Render(cards);

        Assert.Equal(3, PageCount(pdf));
    }

    [Fact]
    public void PermitCards_EmptyBatch_Throws()
        => Assert.Throws<ArgumentException>(() => PermitCardPdf.Render([]));

    [Fact]
    public void PermitCardView_For_BuildsScopeFromSites_WhenTypeHasNoText()
    {
        var permit = new ParkingPermit
        {
            PermitType = new ParkingPermitType { Name = "Zaměstnanec", Binding = PermitBinding.LicensePlate },
            Sites = [new ParkingPermitSite { Site = new Site { Name = "Motol" } }, new ParkingPermitSite { Site = new Site { Name = "Homolka" } }],
            Plates = [new ParkingPermitPlate { Value = "1AB2345" }],
            PermitNumber = "P-2026-0007",
        };

        var view = PermitCardView.For(permit, new Employee { FirstName = "Jan", LastName = "Novák" });

        Assert.Equal("Platí pro areál: Motol, Homolka", view.ScopeText);
        Assert.Equal("1AB2345", view.Plates);
        Assert.Null(view.FunctionTitle);
        Assert.Null(view.HolderName); // jméno se tiskne jen u vazby na funkci
    }

    [Fact]
    public void TableReport_PaginatesLongTables_AndRepeatsHeader()
    {
        var rows = Enumerable.Range(1, 120)
            .Select(i => new string?[] { $"Čtečka {i}", "Budova A / 2. patro / chodba A200 / místnost 214", $"Zaměstnanec {i}", "Chirurgie", $"C-{i:0000}", "1. 9. 2026" })
            .ToList();

        var pdf = TableReportPdf.Render("Přístupy podle čtečky", "test", [
            new PdfColumn("Čtečka", 1.2), new PdfColumn("Umístění", 2), new PdfColumn("Zaměstnanec", 1.4),
            new PdfColumn("Oddělení", 1), new PdfColumn("Karta", 0.8), new PdfColumn("Od", 0.8),
        ], [("", rows)]);

        Assert.True(IsPdf(pdf));
        Assert.True(PageCount(pdf) >= 3, "120 řádků se na jednu stránku A4 nevejde — očekává se stránkování.");
    }

    [Fact]
    public void TableReport_WithSections_AndNoRows_Renders()
    {
        var empty = TableReportPdf.Render("Prázdný report", null, [new PdfColumn("A")], []);
        Assert.True(IsPdf(empty));
        Assert.Equal(1, PageCount(empty));

        var sections = TableReportPdf.Render("Se sekcemi", null, [new PdfColumn("Zaměstnanec"), new PdfColumn("Od")],
        [
            ("Operační sál 1 (2 osob)", new List<string?[]> { new[] { "Jan Novák", "1. 1. 2026" }, new string?[] { "Eva Malá", null } }),
            ("Sklad léčiv (1 osob)", new List<string?[]> { new[] { "Petr Velký", "3. 3. 2026" } }),
        ]);
        Assert.True(IsPdf(sections));
    }
}
