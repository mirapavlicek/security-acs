using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Acs.Infrastructure.Notifications;

/// <summary>
/// Interní SMTP relay často mluví STARTTLS s certifikátem podepsaným sám sebou nebo vydaným na
/// jiné jméno (<c>smtpcl.nnh.local</c> předkládá <c>CN=srlsmtp01.nnh.local</c>) a <see cref="System.Net.Mail.SmtpClient"/>
/// pak odmítne odeslat cokoli. <c>SmtpClient</c> nemá vlastní callback pro ověření certifikátu — čte
/// jen <see cref="ServicePointManager.ServerCertificateValidationCallback"/>. Ten se tu nastaví jednou
/// a ověření uvolní pouze pro SMTP servery, u kterých to správce v Nastavení výslovně povolil
/// (<c>Smtp:IgnoreTlsErrors</c>); HttpClient (SocketsHttpHandler) tento callback nepoužívá, takže
/// ostatní TLS spojení aplikace zůstávají nedotčená. Spojení je dál šifrované, jen se neověřuje
/// identita serveru — správné řešení je certifikát z interní CA s názvem relaye.
/// </summary>
public static class SmtpCertificateTrust
{
    private static readonly ConcurrentDictionary<string, byte> RelaxedHosts = new(StringComparer.OrdinalIgnoreCase);
    private static int _installed;

    /// <summary>Povolí (nebo zruší) odesílání přes <paramref name="host"/> i s neplatným certifikátem.</summary>
    public static void SetRelaxed(string host, bool relaxed)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;
        if (relaxed)
        {
            RelaxedHosts[host.Trim()] = 0;
            if (Interlocked.Exchange(ref _installed, 1) == 0)
                ServicePointManager.ServerCertificateValidationCallback += Validate;
        }
        else
        {
            RelaxedHosts.TryRemove(host.Trim(), out _);
        }
    }

    /// <summary>Servery, pro které se certifikát neověřuje (pro test a zobrazení v Nastavení).</summary>
    public static IReadOnlyCollection<string> Relaxed => RelaxedHosts.Keys.ToList();

    internal static bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        // SmtpClient předá jako sender SslStream; cílový host je ten, na který se navazovalo STARTTLS.
        return sender is SslStream { TargetHostName: { Length: > 0 } target } && RelaxedHosts.ContainsKey(target);
    }
}
