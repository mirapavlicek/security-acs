using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Acs.WinPakConnector.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Acs.WinPakConnector.Auth;

/// <summary>
/// Přihlášení do administračního GUI konektoru. Heslem je buď zvlášť nastavené
/// <c>Security:AdminPassword</c>, nebo — pokud nastavené není — samotný API klíč.
/// Klíč už dnes umožňuje i odemykat dveře, takže tím nevzniká slabší ochrana;
/// oddělené heslo se přesto doporučuje, aby se klíč nemusel zadávat do prohlížeče.
/// </summary>
public sealed class AdminAuthentication(ConnectorSettingsStore store, ILogger<AdminAuthentication> logger)
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string LoginPath = "/ui/login";

    /// <summary>Zpoždění po neúspěšném pokusu — brzdí hádání hesla hrubou silou.</summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    /// <summary>Je konektor vůbec chráněný? Bez klíče i hesla se do GUI nedá přihlásit.</summary>
    public bool IsConfigured
    {
        get
        {
            var settings = store.Current();
            return !string.IsNullOrWhiteSpace(settings.AdminPassword)
                   || !string.IsNullOrWhiteSpace(settings.ApiKey);
        }
    }

    /// <summary>Zda se přihlašuje samostatným heslem (jinak API klíčem).</summary>
    public bool UsesSeparatePassword => !string.IsNullOrWhiteSpace(store.Current().AdminPassword);

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string? password, CancellationToken ct = default)
    {
        var settings = store.Current();
        var expected = string.IsNullOrWhiteSpace(settings.AdminPassword)
            ? settings.ApiKey
            : settings.AdminPassword;

        if (string.IsNullOrWhiteSpace(expected) || password is null || !FixedTimeEquals(password, expected))
        {
            logger.LogWarning("Neúspěšné přihlášení do administrace konektoru.");
            await Task.Delay(FailureDelay, ct);
            return null;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin")],
            Scheme));
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
