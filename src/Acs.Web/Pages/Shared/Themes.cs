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
        ("sparta", "Sparta"),
    ];

    public static bool IsKnown(string? value) => All.Any(t => t.Value == value);
}
