using System.Reflection;
using PdfSharp.Drawing;

namespace Acs.Infrastructure.Pdf;

/// <summary>
/// Logo FNMH vložené do assembly (grafický manuál: modrý monogram „FN / M+H“ s názvem,
/// šedivý monogram a jeho světlá varianta pro vodoznak). Rastr z dodaného vektoru,
/// dostatečný pro tisk kartičky i hlavičky reportu.
/// </summary>
public static class BrandAssets
{
    /// <summary>Firemní modrá FNMH (#27348B).</summary>
    public static readonly XColor Blue = XColor.FromArgb(0x27, 0x34, 0x8B);

    /// <summary>Modré logo s textem „Fakultní nemocnice Motol a Homolka“ (poměr stran ≈ 2,83 : 1).</summary>
    public static XImage Logo() => Load("fnmh-logo.png");

    /// <summary>Šedivý monogram (poměr ≈ 1,12 : 1).</summary>
    public static XImage MonogramGrey() => Load("fnmh-monogram-grey.png");

    /// <summary>Monogram v firemní modré s nízkou průhledností — vodoznak.</summary>
    public static XImage MonogramWatermark() => Load("fnmh-monogram-watermark.png");

    private static XImage Load(string fileName)
    {
        var assembly = typeof(BrandAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream($"Acs.Pdf.Assets.{fileName}")
            ?? throw new InvalidOperationException($"Vložený obrázek {fileName} nebyl v assembly nalezen.");
        // PDFsharp potřebuje stream, který lze číst opakovaně — zkopírujeme do paměti.
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return XImage.FromStream(buffer);
    }

    /// <summary>Vykreslí obrázek do obdélníku se zachováním poměru stran (vycentrovaně).</summary>
    public static XRect DrawFitted(XGraphics gfx, XImage image, XRect box, XStringAlignment horizontal = XStringAlignment.Center)
    {
        var ratio = image.PixelWidth / (double)image.PixelHeight;
        var width = Math.Min(box.Width, box.Height * ratio);
        var height = width / ratio;
        var x = horizontal switch
        {
            XStringAlignment.Near => box.X,
            XStringAlignment.Far => box.X + box.Width - width,
            _ => box.X + (box.Width - width) / 2,
        };
        var target = new XRect(x, box.Y + (box.Height - height) / 2, width, height);
        gfx.DrawImage(image, target);
        return target;
    }
}
