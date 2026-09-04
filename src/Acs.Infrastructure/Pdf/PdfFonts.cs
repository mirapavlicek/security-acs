using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace Acs.Infrastructure.Pdf;

/// <summary>
/// Písma pro generování PDF. PDFsharp na Linuxu nemá odkud brát fonty, proto si je
/// najdeme sami v systémových adresářích (DejaVu Sans / Liberation Sans / Noto Sans /
/// Arial — všechny mají českou diakritiku). Na RHEL nodech instaluje
/// <c>dejavu-sans-fonts</c> deploy skript; adresář lze přebít proměnnou
/// <c>ACS_PDF_FONT_DIR</c>.
/// </summary>
public sealed class SystemFontResolver : IFontResolver
{
    /// <summary>Logický název rodiny, pod kterým se písmo v PDF používá.</summary>
    public const string Family = "ACS Sans";

    private const string RegularFace = "acs-regular";
    private const string BoldFace = "acs-bold";

    private static readonly (string Regular, string Bold)[] Candidates =
    [
        ("DejaVuSans.ttf", "DejaVuSans-Bold.ttf"),
        ("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf"),
        ("NotoSans-Regular.ttf", "NotoSans-Bold.ttf"),
        ("FreeSans.ttf", "FreeSansBold.ttf"),
        ("arial.ttf", "arialbd.ttf"),
        ("Arial.ttf", "Arial Bold.ttf"),
    ];

    private readonly Lazy<(byte[] Regular, byte[] Bold)> _fonts = new(LoadFonts);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        => new FontResolverInfo(bold ? BoldFace : RegularFace, false, italic);

    public byte[]? GetFont(string faceName)
        => faceName == BoldFace ? _fonts.Value.Bold : _fonts.Value.Regular;

    /// <summary>Nastaví resolver globálně (PDFsharp ho dovolí nastavit jen jednou).</summary>
    public static void EnsureRegistered()
    {
        if (GlobalFontSettings.FontResolver is not SystemFontResolver)
            GlobalFontSettings.FontResolver = new SystemFontResolver();
    }

    /// <summary>Kde hledat písma: přebití z prostředí, adresář aplikace a obvyklé systémové cesty.</summary>
    public static IEnumerable<string> FontDirectories()
    {
        var overrideDir = Environment.GetEnvironmentVariable("ACS_PDF_FONT_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            yield return overrideDir;

        yield return Path.Combine(AppContext.BaseDirectory, "fonts");
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts");
        yield return "/Library/Fonts";
        yield return "/System/Library/Fonts";
        var windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(windowsFonts))
            yield return windowsFonts;
    }

    /// <summary>Najde první dostupnou dvojici (regular, bold); vrací null, když v systému žádné vhodné písmo není.</summary>
    public static (string Regular, string Bold)? FindFontFiles()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in FontDirectories().Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                index.TryAdd(Path.GetFileName(file), file);
        }

        foreach (var (regular, bold) in Candidates)
        {
            if (index.TryGetValue(regular, out var regularPath))
                return (regularPath, index.TryGetValue(bold, out var boldPath) ? boldPath : regularPath);
        }

        return null;
    }

    private static (byte[] Regular, byte[] Bold) LoadFonts()
    {
        var files = FindFontFiles()
            ?? throw new InvalidOperationException(
                "Pro generování PDF nebylo nalezeno žádné TrueType písmo s českou diakritikou "
                + "(DejaVu Sans, Liberation Sans, Noto Sans, Arial). Nainstalujte balíček dejavu-sans-fonts, "
                + "nebo nastavte ACS_PDF_FONT_DIR na adresář s .ttf soubory.");
        return (File.ReadAllBytes(files.Regular), File.ReadAllBytes(files.Bold));
    }
}

/// <summary>Pomocné funkce pro kreslení textu v PDF (písma, zalamování, milimetry).</summary>
internal static class PdfText
{
    public static XFont Font(double sizePt, bool bold = false)
        => new(SystemFontResolver.Family, sizePt, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    public static double Mm(double millimeters) => XUnit.FromMillimeter(millimeters).Point;

    /// <summary>Rozdělí text na řádky tak, aby se vešly do dané šířky (zalamuje po slovech, dlouhé slovo rozseká).</summary>
    public static List<string> Wrap(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (gfx.MeasureString(candidate, font).Width <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                    lines.Add(current);

                current = word;
                while (gfx.MeasureString(current, font).Width > maxWidth && current.Length > 1)
                {
                    var cut = current.Length - 1;
                    while (cut > 1 && gfx.MeasureString(current[..cut], font).Width > maxWidth)
                        cut--;
                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    /// <summary>Vykreslí zalomený text od horního okraje obdélníku; vrátí výšku, kterou zabral.</summary>
    public static double DrawWrapped(XGraphics gfx, string text, XFont font, XBrush brush, XRect rect,
        XStringAlignment alignment = XStringAlignment.Near, double lineSpacing = 1.2)
    {
        var lines = Wrap(gfx, text, font, rect.Width);
        var lineHeight = font.GetHeight() * lineSpacing;
        var y = rect.Y;
        var format = new XStringFormat { Alignment = alignment, LineAlignment = XLineAlignment.Near };
        foreach (var line in lines)
        {
            gfx.DrawString(line, font, brush, new XRect(rect.X, y, rect.Width, lineHeight), format);
            y += lineHeight;
        }

        return lines.Count * lineHeight;
    }
}
