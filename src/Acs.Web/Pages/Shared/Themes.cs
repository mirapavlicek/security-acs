namespace Acs.Web.Pages.Shared;

/// <summary>
/// Barevná témata GUI — hodnota jde do <c>html[data-theme]</c> a musí mít blok proměnných
/// v <c>wwwroot/css/site.css</c>. Jeden seznam pro přepínač v hlavičce i výchozí téma v Nastavení.
/// </summary>
public static class Themes
{
    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("light", "Světlé"),
        ("dark", "Tmavé"),
        ("blue", "Modré"),
        ("green", "Zelené"),
        ("purple", "Fialové"),
        ("contrast", "Kontrastní"),
        ("dark-red", "Dark red"),
    ];

    /// <summary>Původní název tématu „dark-red“ — uložený v cookie a u uživatelů z dřívějška.</summary>
    private const string LegacyDarkRed = "sparta";

    public static bool IsKnown(string? value) => All.Any(t => t.Value == value);

    /// <summary>Převede uložené téma na aktuální hodnotu (včetně přejmenovaných); neznámé → světlé.</summary>
    public static string Normalize(string? value)
    {
        if (value == LegacyDarkRed)
            return "dark-red";
        return IsKnown(value) ? value! : "light";
    }
}
