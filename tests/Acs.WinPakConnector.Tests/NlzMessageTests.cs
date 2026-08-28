using Acs.WinPakConnector.Providers.Com;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Parser zpráv komunikačního serveru. Ukázky jsou přímo z příručky
/// „WIN-PAK 4.9 Communication Server API“.
/// </summary>
public sealed class NlzMessageTests
{
    [Fact]
    public void Stav_dveri_se_precte_vcetne_neznamych_hodnot()
    {
        const string message = """
            <NLZ>
            <Door_IsOpen>1</Door_IsOpen>
            <Door_IsShunted>0</Door_IsShunted>
            <Door_ForcedOpen>-1</Door_ForcedOpen>
            <Door_Ajar>0</Door_Ajar>
            <ADV_Hid>23</ADV_Hid>
            <ADV_DeviceName>Hlavní vchod</ADV_DeviceName>
            <Account>FNMH</Account>
            <SubAccount>Default</SubAccount>
            </NLZ>
            """;

        var status = NlzMessage.ParseDoorStatus(23, message);

        Assert.Equal("23", status.Hid);
        Assert.Equal("Hlavní vchod", status.DeviceName);
        Assert.True(status.IsOpen);
        Assert.False(status.IsShunted);
        Assert.Null(status.ForcedOpen);   // -1 = WIN-PAK stav nezná
        Assert.False(status.Ajar);
        Assert.Equal("FNMH", status.Account);
    }

    [Fact]
    public void Prazdna_odpoved_na_stav_dveri_vrati_zaznam_s_neznamymi_hodnotami()
    {
        var status = NlzMessage.ParseDoorStatus(7, null);

        Assert.Equal("7", status.Hid);
        Assert.Null(status.IsOpen);
        Assert.Null(status.DeviceName);
    }

    [Fact]
    public void Stav_serveru_se_precte_z_vice_bloku()
    {
        const string message = """
            <NLZ><SrvId>1</SrvId><Server>WPDB</Server><Connected>1</Connected><SerType>1</SerType></NLZ>
            <NLZ><SrvId>2</SrvId><Server>WPCOMM</Server><Connected>0</Connected><SerType>2</SerType></NLZ>
            """;

        var servers = NlzMessage.ParseServerStatus(message);

        Assert.Equal(2, servers.Count);
        Assert.True(servers[0].Connected);
        Assert.False(servers[1].Connected);
        Assert.Equal("WPCOMM", servers[1].ServerName);
    }

    [Fact]
    public void Udalost_z_panelu_se_rozebere_na_kartu_ctecku_a_cas()
    {
        const string message = """
            <NLZ>
            <AckStatus>1</AckStatus>
            <Idx>-1</Idx>
            <CommSrvID>1</CommSrvID>
            <Account>NCI</Account>
            <SubAccount>Default</SubAccount>
            <HID>23</HID>
            <Prio>1</Prio>
            <Date>2/16/07</Date>
            <Time>18:46:00</Time>
            <Status>Valid Card</Status>
            <EventID>701</EventID>
            <CardNumber>1234</CardNumber>
            <FullName>Bunny Bugs</FullName>
            <RP>Panel 1 - Read 1</RP>
            </NLZ>
            """;

        var winPakEvent = Assert.Single(WinPakEvent.Parse(message));

        Assert.False(winPakEvent.IsAlarm);   // <Idx> -1 = běžná událost
        Assert.Equal(701, winPakEvent.EventId);
        Assert.Equal("23", winPakEvent.Hid);
        Assert.Equal("1234", winPakEvent.CardNumber);
        Assert.Equal("Bunny Bugs", winPakEvent.FullName);
        Assert.Equal("Panel 1 - Read 1", winPakEvent.ReaderPoint);
        Assert.Equal("Valid Card", winPakEvent.Status);
        Assert.Equal(new DateTime(2007, 2, 16, 18, 46, 0), winPakEvent.At);
    }

    [Fact]
    public void Kladny_index_znamena_alarm()
    {
        var alarm = Assert.Single(WinPakEvent.Parse("<NLZ><Idx>1</Idx><EventID>405</EventID></NLZ>"));

        Assert.True(alarm.IsAlarm);
        Assert.Equal(405, alarm.EventId);
    }

    [Fact]
    public void Prvni_vyskyt_opakovane_znacky_vyhrava()
    {
        // Příručka má v ukázce <Account> dvakrát; bereme první výskyt.
        var tags = NlzMessage.ParseTags("<Account>Test</Account><Account>NCI</Account>");

        Assert.Equal("Test", tags["Account"]);
    }

    [Fact]
    public void Neuplna_zprava_nespadne()
    {
        Assert.Empty(WinPakEvent.Parse(""));
        Assert.Empty(WinPakEvent.Parse(null));
        Assert.Empty(NlzMessage.ParseServerStatus("<NLZ></NLZ>"));
    }
}
