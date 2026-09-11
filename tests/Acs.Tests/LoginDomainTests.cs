using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Settings;
using Acs.Web.Pages.Shared;
using Xunit;

namespace Acs.Tests;

/// <summary>Doplnění domény k AD účtu při přihlášení a přejmenované téma „dark red“.</summary>
public class LoginDomainTests
{
    [Theory]
    [InlineData("jnovak", "nnh.local", "jnovak@nnh.local")]
    [InlineData("  jnovak ", "nnh.local", "jnovak@nnh.local")]
    [InlineData("jnovak", "@nnh.local", "jnovak@nnh.local")]
    [InlineData("jnovak@fnmh.cz", "nnh.local", "jnovak@fnmh.cz")]
    [InlineData(@"NNH\jnovak", "nnh.local", @"NNH\jnovak")]
    [InlineData("jnovak", "", "jnovak")]
    [InlineData("jnovak", null, "jnovak")]
    public void Bind_ucet_dopln_domenu_jen_k_holemu_jmenu(string userName, string? domain, string expected)
        => Assert.Equal(expected, LdapAuthenticator.BindUserName(userName, domain));

    [Theory]
    [InlineData("DC=nnh,DC=local", "nnh.local")]
    [InlineData("OU=Users,OU=FNM,DC=nnh,DC=local", "nnh.local")]
    [InlineData(" dc=fnmh , dc=cz ", "fnmh.cz")]
    [InlineData("OU=Users", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Domena_z_base_dn(string? baseDn, string? expected)
        => Assert.Equal(expected, SettingsService.DomainFromBaseDn(baseDn));

    [Fact]
    public void Vychozi_domena_je_nnh_local()
        => Assert.Equal("nnh.local", SettingsService.DefaultLdapDomain);

    [Fact]
    public void Tema_sparta_se_prevede_na_dark_red_a_nezname_na_svetle()
    {
        Assert.Equal("dark-red", Themes.Normalize("sparta"));
        Assert.Equal("dark-red", Themes.Normalize("dark-red"));
        Assert.Equal("dark", Themes.Normalize("dark"));
        Assert.Equal("light", Themes.Normalize("neexistuje"));
        Assert.Equal("light", Themes.Normalize(null));
        Assert.Contains(Themes.All, t => t.Value == "dark-red" && t.Label == "Dark red");
        Assert.DoesNotContain(Themes.All, t => t.Value == "sparta");
    }
}
