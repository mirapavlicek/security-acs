namespace Acs.Web;

/// <summary>
/// Co na nodu běží. Updater zapisuje vedle programu soubor <c>.git-sha</c> s commitem
/// nasazené verze; zápatí ho ukazuje, aby bylo bez hádání vidět, zda už se nová verze
/// nasadila (timer, build a testy na nodu trvají 5–15 minut po tagu).
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<string> Current = new(Read);

    public static string Display => Current.Value;

    private static string Read()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ".git-sha");
            if (File.Exists(path))
            {
                var sha = File.ReadAllText(path).Trim();
                if (sha.Length >= 7)
                    return sha[..Math.Min(12, sha.Length)];
            }
        }
        catch (IOException)
        {
        }

        var informational = typeof(AppVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "dev" : informational.Split('+')[0];
    }
}
