using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Přístupové úrovně nese ve WIN-PAKu karta, ne držitel — konektor proto
/// při změně přepočítává seznam úrovní každé karty.
/// </summary>
public sealed class AccessLevelRecalculationTests
{
    [Fact]
    public void Prideleni_prida_uroven_na_konec()
    {
        var result = ComWinPakProvider.RecalculateAccessLevels(["1", "2"], "3", grant: true);

        Assert.Equal(["1", "2", "3"], result);
    }

    [Fact]
    public void Prideleni_jiz_prirazene_urovne_nic_nezmeni()
    {
        var current = new[] { "1", "2" };

        var result = ComWinPakProvider.RecalculateAccessLevels(current, "2", grant: true);

        Assert.Equal(current, result);
    }

    [Fact]
    public void Odebrani_smaze_jen_danou_uroven()
    {
        var result = ComWinPakProvider.RecalculateAccessLevels(["1", "2", "3"], "2", grant: false);

        Assert.Equal(["1", "3"], result);
    }

    [Fact]
    public void Odebrani_neprirazene_urovne_nic_nezmeni()
    {
        var result = ComWinPakProvider.RecalculateAccessLevels(["1"], "9", grant: false);

        Assert.Equal(["1"], result);
    }
}
