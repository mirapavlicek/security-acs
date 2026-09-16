using Acs.Infrastructure.Auth;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Bez keytabu GSSAPI na Linuxu žádný Kerberos ticket neověří (UnknownCredentials) — přihlášení Windows
/// se proto nabízí jen s čitelným keytabem z KRB5_KTNAME; výchozí /etc/krb5.keytab (hostitelský keytab
/// jiného realmu) se nebere.
/// </summary>
public sealed class KerberosKeytabTests
{
    [Fact]
    public void Bez_cesty_nebo_bez_souboru_keytab_neni_a_problem_rika_proc()
    {
        Assert.False(KerberosKeytab.IsReadable(null));
        Assert.False(KerberosKeytab.IsReadable(""));
        Assert.Contains("KRB5_KTNAME není nastavená", KerberosKeytab.ProblemWith(null));

        var missing = Path.Combine(Path.GetTempPath(), $"acs-{Guid.NewGuid():N}.keytab");
        Assert.False(KerberosKeytab.IsReadable(missing));
        Assert.Contains(missing, KerberosKeytab.ProblemWith(missing));
        Assert.Contains("neexistuje", KerberosKeytab.ProblemWith(missing));
    }

    [Fact]
    public void Citelny_soubor_je_keytab_bez_problemu()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acs-{Guid.NewGuid():N}.keytab");
        File.WriteAllBytes(path, [0x05, 0x02]);
        try
        {
            Assert.True(KerberosKeytab.IsReadable(path));
            Assert.Null(KerberosKeytab.ProblemWith(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Predpona_FILE_se_z_KRB5_KTNAME_odstrani()
    {
        // Cesta se čte z prostředí; testovací továrna ji nastavuje bez předpony, tady jen tvar hodnoty.
        var configured = Environment.GetEnvironmentVariable("KRB5_KTNAME");
        if (string.IsNullOrEmpty(configured))
            return;
        Assert.False(KerberosKeytab.Path!.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase));
    }
}
