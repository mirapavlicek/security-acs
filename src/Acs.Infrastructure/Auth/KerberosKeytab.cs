using System.Runtime.InteropServices;

namespace Acs.Infrastructure.Auth;

/// <summary>
/// Keytab pro ověřování Kerberos ticketů (přihlášení účtem Windows přes Negotiate). Na Linuxu
/// bez něj GSSAPI žádný ticket neověří a každý pokus skončí <c>UnknownCredentials</c> — proto se
/// automatické přihlášení Windows nabízí jen tehdy, když keytab existuje; jinak by každý uživatel
/// nejdřív absolvoval marnou výzvu Negotiate a teprve pak dostal formulář.
/// </summary>
public static class KerberosKeytab
{
    /// <summary>
    /// Cesta ke keytabu z <c>KRB5_KTNAME</c> (bez předpony <c>FILE:</c>); null, když proměnná není nastavená.
    /// Výchozí <c>/etc/krb5.keytab</c> se úmyslně nebere: nody jsou klienty jiného realmu (IPA) a jejich
    /// hostitelský keytab SPN aplikace neobsahuje — keytab pro ACS musí být nastavený výslovně.
    /// </summary>
    public static string? Path
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("KRB5_KTNAME");
            if (string.IsNullOrWhiteSpace(configured))
                return null;
            return configured.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) ? configured[5..] : configured;
        }
    }

    /// <summary><c>KRB5_KTNAME</c> je nastavená.</summary>
    public static bool IsConfigured => Path is not null;

    /// <summary>
    /// Ověření ticketů je možné: Windows (SSPI, keytab nepotřebuje), nebo keytab z <c>KRB5_KTNAME</c>
    /// existuje a účet služby ho může číst. Neřeší, zda obsahuje správný SPN — to ukáže až test v Nastavení.
    /// </summary>
    public static bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IsReadable(Path);

    /// <summary>Soubor keytabu existuje a jde otevřít ke čtení.</summary>
    public static bool IsReadable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Proč Kerberos nemůže fungovat — pro nastavení a log; null, když je vše na místě.</summary>
    public static string? Problem => IsAvailable ? null : ProblemWith(Path);

    /// <summary>Popis problému pro danou cestu (null = keytab je čitelný).</summary>
    public static string? ProblemWith(string? path)
    {
        if (IsReadable(path))
            return null;
        return path is { Length: > 0 }
            ? $"keytab {path} z KRB5_KTNAME neexistuje nebo ho účet služby nemůže číst"
            : "KRB5_KTNAME není nastavená (v /etc/acs/acs.env)";
    }
}
