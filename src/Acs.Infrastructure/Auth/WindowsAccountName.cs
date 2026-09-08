namespace Acs.Infrastructure.Auth;

/// <summary>
/// Rozklad jména identity z přihlášení Windows na účet a doménu.
/// Windows (SSPI) vrací <c>DOMENA\ucet</c>, Linux (GSSAPI/Kerberos) <c>ucet@REALM.LOCAL</c>;
/// v ACS se uživatel vede pod samotným účtem (sAMAccountName), stejně jako při přihlášení heslem.
/// </summary>
public readonly record struct WindowsAccountName(string Account, string? Domain)
{
    public static WindowsAccountName Parse(string identityName)
    {
        var name = identityName.Trim();
        var backslash = name.IndexOf('\\');
        if (backslash >= 0)
            return new WindowsAccountName(name[(backslash + 1)..], NullIfEmpty(name[..backslash]));

        var at = name.IndexOf('@');
        if (at >= 0)
            return new WindowsAccountName(name[..at], NullIfEmpty(name[(at + 1)..]));

        return new WindowsAccountName(name, null);
    }

    /// <summary>
    /// Je doména účtu mezi povolenými? Seznam je oddělený čárkou; porovnává se NetBIOS
    /// i DNS tvar (položka „FNMH“ pustí i realm „FNMH.LOCAL“, položka „fnmh.local“ jen ten).
    /// Prázdný seznam = bez omezení; bez domény v identitě projde jen prázdný seznam.
    /// </summary>
    public bool IsDomainAllowed(string? allowedDomains)
    {
        var allowed = (allowedDomains ?? "")
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length == 0)
            return true;
        if (Domain is null)
            return false;

        var domainShort = Domain.Split('.')[0];
        foreach (var item in allowed)
        {
            if (Domain.Equals(item, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!item.Contains('.') && domainShort.Equals(item, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
