using System.DirectoryServices.Protocols;
using System.Net;
using Acs.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Auth;

public record LdapUserInfo(string UserName, string? DisplayName, string? Email, IReadOnlyList<string> Groups);

/// <summary>Active Directory teď není k dispozici (žádný řadič neodpovídá nebo nedokončí přihlášení v limitu) — není to špatné heslo.</summary>
public sealed class LdapUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Ověření uživatele proti Active Directory přes LDAP(S) —
/// System.DirectoryServices.Protocols (funguje i na Linuxu).
/// Cílový řadič vrací <see cref="DcLocator"/> (DNS SRV `_ldap._tcp.dc._msdcs`);
/// při výpadku řadiče se cache zneplatní a ověření se zopakuje proti dalšímu
/// živému DC. Konfigurace se čte z nastavení v DB (editovatelné v GUI).
/// </summary>
public class LdapAuthenticator(SettingsService settings, DcLocator dcLocator, ILogger<LdapAuthenticator> logger)
{
    /// <summary>
    /// Nejdelší čekání na spojení + TLS + bind k jednomu řadiči při přihlášení. Dva pokusy
    /// (řadič a náhradní) i s dohledáním uživatele se vejdou pod obvyklý limit reverzní proxy (60 s).
    /// </summary>
    public static readonly TimeSpan LoginBindTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Kód libldap <c>LDAP_TIMEOUT</c> (klient se odpovědi nedočkal).</summary>
    public const int LdapTimeoutErrorCode = 85;

    /// <summary>
    /// Ověří jméno+heslo bind-em do AD; při úspěchu vrátí info o uživateli, při špatném hesle
    /// (nebo vypnutém LDAP) <c>null</c>. Když neodpovídá žádný řadič, vyhodí
    /// <see cref="LdapUnavailableException"/> — přihlašovací stránka to má hlásit jinak než špatné heslo.
    /// </summary>
    public virtual async Task<LdapUserInfo?> AuthenticateAsync(string userName, string password, CancellationToken ct = default)
    {
        if (!await settings.GetBoolAsync(SettingKeys.LdapEnabled, false, ct))
            return null;

        try
        {
            return await AuthenticateOnceAsync(userName, password, ct);
        }
        catch (LdapException ex) when (IsConnectivityError(ex))
        {
            // Řadič vypadl → zneplatnit cache lokátoru a zkusit jiný živý DC.
            logger.LogWarning(ex, "LDAP řadič neodpovídá — zkouším jiný DC.");
            DcLocator.Invalidate();
            try
            {
                return await AuthenticateOnceAsync(userName, password, ct);
            }
            catch (Exception retryEx) when (retryEx is LdapException or InvalidOperationException)
            {
                logger.LogError(retryEx, "LDAP ověření selhalo i proti náhradnímu řadiči.");
                throw new LdapUnavailableException($"Řadič domény neodpovídá ({ex.Message}); náhradní: {retryEx.Message}", retryEx);
            }
        }
        catch (LdapException ex)
        {
            logger.LogWarning(ex, "LDAP ověření uživatele {User} selhalo.", userName);
            return null;
        }
        catch (DirectoryOperationException ex)
        {
            // Heslo prošlo, ale dohledání uživatele server odmítl (špatný Base DN, referral, práva účtu).
            logger.LogError(ex, "LDAP: dohledání uživatele {User} po úspěšném ověření selhalo ({Code}).", userName, ex.Response?.ResultCode);
            throw new LdapUnavailableException(
                $"Heslo bylo ověřeno, ale dohledání uživatele v Active Directory selhalo ({ex.Response?.ResultCode.ToString() ?? ex.Message}) — zkontrolujte Base DN a filtr v Nastavení → AD.", ex);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "LDAP ověření nelze provést (žádný dostupný řadič).");
            throw new LdapUnavailableException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Dohledá uživatele v AD podle účtu (sAMAccountName) bez jeho hesla — bind servisním
    /// účtem (<c>Ldap:BindUser</c>). Používá se po přihlášení účtem Windows (Kerberos/NTLM),
    /// kdy heslo uživatele aplikace nemá, ale potřebuje jméno, e-mail a skupiny pro role.
    /// Vrací <c>null</c>, když LDAP nebo servisní účet není nakonfigurován nebo dotaz selže
    /// (volající pak pracuje jen s tím, co má v DB).
    /// </summary>
    public virtual async Task<LdapUserInfo?> LookupAsync(string samAccount, CancellationToken ct = default)
    {
        if (!await settings.GetBoolAsync(SettingKeys.LdapEnabled, false, ct))
            return null;

        var bindUser = await settings.GetAsync(SettingKeys.LdapBindUser, ct);
        var bindPassword = await settings.GetAsync(SettingKeys.LdapBindPassword, ct);
        if (string.IsNullOrWhiteSpace(bindUser) || string.IsNullOrEmpty(bindPassword))
        {
            logger.LogInformation("LDAP: servisní účet není nastaven — atributy uživatele {User} se z AD nenačtou.", samAccount);
            return null;
        }

        try
        {
            return await LookupOnceAsync(samAccount, bindUser, bindPassword, ct);
        }
        catch (LdapException ex) when (IsConnectivityError(ex))
        {
            logger.LogWarning(ex, "LDAP řadič neodpovídá — zkouším jiný DC.");
            DcLocator.Invalidate();
            try
            {
                return await LookupOnceAsync(samAccount, bindUser, bindPassword, ct);
            }
            catch (Exception retryEx)
            {
                logger.LogError(retryEx, "LDAP dohledání uživatele {User} selhalo i proti náhradnímu řadiči.", samAccount);
                return null;
            }
        }
        catch (Exception ex) when (ex is LdapException or InvalidOperationException)
        {
            logger.LogWarning(ex, "LDAP dohledání uživatele {User} selhalo.", samAccount);
            return null;
        }
    }

    private async Task<LdapUserInfo?> LookupOnceAsync(string samAccount, string bindUser, string bindPassword, CancellationToken ct)
    {
        var server = await dcLocator.GetActiveServerAsync(ct);
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        var port = await settings.GetIntAsync(SettingKeys.LdapPort, useSsl ? 636 : 389, ct);

        using var connection = await OpenTimedAsync(server, port, useSsl, bindUser, bindPassword, ct);
        return await SearchUserAsync(connection, samAccount, ct);
    }

    private async Task<LdapUserInfo?> AuthenticateOnceAsync(string userName, string password, CancellationToken ct)
    {
        var server = await dcLocator.GetActiveServerAsync(ct);
        var useSsl = await settings.GetBoolAsync(SettingKeys.LdapUseSsl, true, ct);
        var port = await settings.GetIntAsync(SettingKeys.LdapPort, useSsl ? 636 : 389, ct);
        var domain = await settings.GetLdapDomainAsync(ct);

        // 1) bind jako přihlašovaný uživatel (ověření hesla) — k účtu bez domény se doplní
        //    UPN sufix (nastavená doména, jinak z Base DN, jinak nnh.local), takže stačí zadat „jnovak“;
        //    zadané „jnovak@nnh.local“ / „NNH\jnovak“ se napřed převede na totéž.
        userName = NormalizeLoginName(userName, domain);
        if (userName.Length == 0)
            return null;
        var bindUser = BindUserName(userName, domain);

        LdapConnection connection;
        try
        {
            connection = await OpenTimedAsync(server, port, useSsl, bindUser, password, ct); // LdapException při špatném hesle
        }
        catch (LdapException ex) when (!IsConnectivityError(ex))
        {
            logger.LogWarning("LDAP: neplatné přihlášení uživatele {User} ({Message}).", userName, ex.Message);
            return null;
        }

        // 2) dohledání atributů uživatele
        using (connection)
        {
            var samAccount = userName.Contains('@') ? userName.Split('@')[0] : userName.Split('\\').Last();
            return await SearchUserAsync(connection, samAccount, ct);
        }
    }

    /// <summary><see cref="OpenAsync"/> s limitem pro přihlášení a záznamem, jak dlouho bind trval (pomalý řadič se pozná v logu).</summary>
    private async Task<LdapConnection> OpenTimedAsync(string server, int port, bool useSsl, string bindUser, string password, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var connection = await OpenAsync(server, port, useSsl, bindUser, password, LoginBindTimeout, ct: ct);
            if (watch.Elapsed > TimeSpan.FromSeconds(3))
                logger.LogWarning("LDAP: bind na {Server}:{Port} trval {Ms} ms — řadič odpovídá pomalu.", server, port, watch.ElapsedMilliseconds);
            else
                logger.LogDebug("LDAP: bind na {Server}:{Port} za {Ms} ms.", server, port, watch.ElapsedMilliseconds);
            return connection;
        }
        catch (LdapException ex) when (IsConnectivityError(ex))
        {
            logger.LogWarning("LDAP: řadič {Server}:{Port} po {Ms} ms — {Message}", server, port, watch.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Účet pro LDAP bind: <c>jnovak</c> → <c>jnovak@doména</c>; UPN (<c>jnovak@nnh.local</c>) i tvar
    /// <c>DOMENA\jnovak</c> zůstávají beze změny.
    /// </summary>
    public static string BindUserName(string userName, string? domain)
    {
        var account = userName.Trim();
        if (string.IsNullOrWhiteSpace(domain) || account.Contains('@') || account.Contains('\\'))
            return account;
        return $"{account}@{domain.Trim().TrimStart('@')}";
    }

    /// <summary>
    /// Jméno z formuláře do tvaru, se kterým pracuje zbytek přihlášení: mezery pryč,
    /// <c>jnovak@nnh.local</c> (přípona = nastavená doména, bez ohledu na velikost písmen) → <c>jnovak</c>,
    /// <c>NNH\jnovak</c> nebo <c>nnh.local\jnovak</c> → <c>jnovak</c>. Jiná přípona (<c>jnovak@jina.domena</c>)
    /// zůstává — uživatel ji zadal záměrně a bind ji použije. Zadání s doménou tak dopadne stejně
    /// jako samotné jméno, které nápověda formuláře doporučuje.
    /// </summary>
    public static string NormalizeLoginName(string userName, string? domain)
    {
        var account = userName.Trim();
        var backslash = account.IndexOf('\\');
        if (backslash >= 0)
            account = account[(backslash + 1)..].Trim();

        var at = account.IndexOf('@');
        if (at < 0)
            return account;

        var suffix = account[(at + 1)..].Trim().TrimEnd('.');
        var configured = domain?.Trim().TrimStart('@').TrimEnd('.');
        var sameDomain = !string.IsNullOrEmpty(configured)
            && (suffix.Equals(configured, StringComparison.OrdinalIgnoreCase)
                || suffix.Equals(configured.Split('.')[0], StringComparison.OrdinalIgnoreCase));
        return sameDomain || suffix.Length == 0 ? account[..at].Trim() : account;
    }

    private async Task<LdapUserInfo> SearchUserAsync(LdapConnection connection, string samAccount, CancellationToken ct)
    {
        var baseDn = await settings.GetAsync(SettingKeys.LdapBaseDn, ct) ?? "";
        var filterTemplate = await settings.GetAsync(SettingKeys.LdapUserFilter, ct)
            ?? "(&(objectClass=user)(sAMAccountName={0}))";
        var filter = string.Format(filterTemplate, EscapeLdapFilter(samAccount));

        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree,
            "displayName", "mail", "memberOf", "sAMAccountName");
        var response = (SearchResponse)connection.SendRequest(request);

        if (response.Entries.Count == 0)
            return new LdapUserInfo(samAccount, null, null, []);

        var entry = response.Entries[0];
        var groups = new List<string>();
        if (entry.Attributes["memberOf"] is { } memberOf)
            groups.AddRange(memberOf.GetValues(typeof(string)).Cast<string>());

        return new LdapUserInfo(
            UserName: GetAttr(entry, "sAMAccountName") ?? samAccount,
            DisplayName: GetAttr(entry, "displayName"),
            Email: GetAttr(entry, "mail"),
            Groups: groups);
    }

    /// <summary>Sdílená tovární metoda LDAP spojení (používá ji i import zaměstnanců).</summary>
    public static LdapConnection CreateConnection(string server, int port, bool useSsl, string bindUser, string password)
    {
        var identifier = new LdapDirectoryIdentifier(server, port, fullyQualifiedDnsHostName: true, connectionless: false);
        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindUser, password),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        if (useSsl)
            connection.SessionOptions.SecureSocketLayer = true;
        return connection;
    }

    /// <summary>
    /// Otevře spojení a provede bind s časovým limitem. System.DirectoryServices.Protocols na Linuxu
    /// <see cref="LdapConnection.Timeout"/> na bind nepoužije (volá synchronní <c>ldap_sasl_bind_s</c> bez
    /// síťového timeoutu): řadič, který TCP spojení přijme, ale TLS handshake nebo bind nedokončí, by
    /// požadavek držel do vypršení TCP (minuty) a přihlašovací stránka skončí 504 od reverzní proxy.
    /// Po limitu se vyhodí <see cref="LdapException"/> s kódem <see cref="LdapTimeoutErrorCode"/>, kterou
    /// volající bere jako výpadek řadiče (<see cref="IsConnectivityError"/>) a zkusí jiný.
    /// Zablokované nativní volání nelze přerušit — spojení se uklidí, až doběhne.
    /// </summary>
    /// <param name="configure">Nastavení spojení před bind-em (např. referral chasing).</param>
    public static async Task<LdapConnection> OpenAsync(string server, int port, bool useSsl, string bindUser, string password,
        TimeSpan timeout, Action<LdapConnection>? configure = null, CancellationToken ct = default)
    {
        var connection = CreateConnection(server, port, useSsl, bindUser, password);
        connection.Timeout = timeout; // platí pro SendRequest (ldap_result), bind hlídá WaitAsync níže
        configure?.Invoke(connection);

        var bind = Task.Run(() => connection.Bind(), CancellationToken.None);
        try
        {
            await bind.WaitAsync(timeout, ct);
            return connection;
        }
        catch (TimeoutException)
        {
            _ = bind.ContinueWith(_ => connection.Dispose(), TaskScheduler.Default);
            throw new LdapException(LdapTimeoutErrorCode,
                $"Řadič {server}:{port} nedokončil spojení a přihlášení do {timeout.TotalSeconds:0} s (TCP port odpovídá, ale TLS/LDAP ne).");
        }
        catch (OperationCanceledException)
        {
            _ = bind.ContinueWith(_ => connection.Dispose(), TaskScheduler.Default);
            throw;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Chyby spojení (server down/nedosažitelný/timeout) vs. chyby přihlášení.</summary>
    public static bool IsConnectivityError(LdapException ex)
        => ex.ErrorCode is 81 or 91 or LdapTimeoutErrorCode or 52; // ServerDown, ConnectError, Timeout, UnavailableCriticalExtension

    private static string? GetAttr(SearchResultEntry entry, string name)
        => entry.Attributes[name] is { Count: > 0 } attr ? attr[0]?.ToString() : null;

    private static string EscapeLdapFilter(string value) => value
        .Replace(@"\", @"\5c").Replace("*", @"\2a").Replace("(", @"\28")
        .Replace(")", @"\29").Replace("\0", @"\00");
}
