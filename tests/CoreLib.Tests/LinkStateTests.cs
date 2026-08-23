using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// One answer to "is anything reachable, and over what".
///
/// <para>This type exists because both heads used to work it out for themselves and disagree.
/// The Windows dashboard read the TCP transport directly, so a phone that fell back to Bluetooth
/// left the window saying "waiting for a device"; the Linux shell combined the two tiers for the
/// sidebar but not for the device list, so the sidebar said "Bluetooth" while every row called
/// the same peer disconnected. The tests below are that pair of bugs, written down.</para>
/// </summary>
public class LinkStateTests
{
    [Fact]
    public void Nothing_is_connected_to_begin_with()
    {
        var links = new LinkState();

        Assert.False(links.IsConnected);
        Assert.Equal(LinkKind.None, links.ActiveLink);
        Assert.Null(links.PeerName);
    }

    /// <summary>The bug the type was written for: Bluetooth alone still means connected.</summary>
    [Fact]
    public void A_peer_on_bluetooth_alone_is_connected()
    {
        var links = new LinkState();

        links.SetBle(true, "S21 FE");

        Assert.True(links.IsConnected);
        Assert.Equal(LinkKind.Ble, links.ActiveLink);
        Assert.Equal("S21 FE", links.PeerName);
    }

    /// <summary>Wi-Fi wins when both are up, because it is the link that carries everything.</summary>
    [Fact]
    public void Wifi_wins_when_both_links_are_up()
    {
        var links = new LinkState();

        links.SetBle(true, "S21 FE");
        links.SetWiFi(true, "S21 FE");

        Assert.Equal(LinkKind.WiFi, links.ActiveLink);
    }

    /// <summary>Losing the socket falls back to the radio rather than reading as disconnected.</summary>
    [Fact]
    public void Losing_wifi_falls_back_to_bluetooth()
    {
        var links = new LinkState();
        links.SetBle(true, "S21 FE");
        links.SetWiFi(true, "S21 FE");

        links.SetWiFi(false);

        Assert.True(links.IsConnected);
        Assert.Equal(LinkKind.Ble, links.ActiveLink);
        Assert.Equal("S21 FE", links.PeerName);
    }

    [Fact]
    public void The_name_is_forgotten_only_when_both_links_are_down()
    {
        var links = new LinkState();
        links.SetWiFi(true, "S21 FE");
        links.SetBle(true, "S21 FE");

        links.SetWiFi(false);
        Assert.Equal("S21 FE", links.PeerName);

        links.SetBle(false);
        Assert.Null(links.PeerName);
        Assert.False(links.IsConnected);
    }

    [Fact]
    public void A_change_is_announced_once()
    {
        var links = new LinkState();
        int raised = 0;
        links.Changed += () => raised++;

        links.SetWiFi(true, "MSI-SURYANSHU");

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// Saying the same thing twice is not a change. Both transports report on a timer, so a state
    /// that re-raised on every report would repaint the window several times a second forever.
    /// </summary>
    [Fact]
    public void Repeating_the_same_state_announces_nothing()
    {
        var links = new LinkState();
        links.SetWiFi(true, "MSI-SURYANSHU");

        int raised = 0;
        links.Changed += () => raised++;

        links.SetWiFi(true, "MSI-SURYANSHU");
        links.SetWiFi(true, "MSI-SURYANSHU");

        Assert.Equal(0, raised);
    }

    /// <summary>A peer that only now announced a name is a change worth repainting for.</summary>
    [Fact]
    public void Learning_the_peers_name_is_a_change()
    {
        var links = new LinkState();
        links.SetBle(true);

        int raised = 0;
        links.Changed += () => raised++;

        links.SetBle(true, "S21 FE");

        Assert.Equal(1, raised);
        Assert.Equal("S21 FE", links.PeerName);
    }

    /// <summary>A listener that throws is a broken window, not a reason to stop syncing.</summary>
    [Fact]
    public void A_throwing_listener_does_not_escape()
    {
        var links = new LinkState();
        links.Changed += () => throw new InvalidOperationException("the window is gone");

        links.SetWiFi(true, "MSI-SURYANSHU");

        Assert.True(links.IsConnected);
    }

    /// <summary>
    /// Two devices in one process must not share link state.
    ///
    /// This is why the type is an instance rather than the static it began as: the Linux head
    /// runs two devices on one machine to exercise the mesh without a second computer.
    /// </summary>
    [Fact]
    public void Two_devices_keep_their_own_state()
    {
        var first = new LinkState();
        var second = new LinkState();

        first.SetWiFi(true, "surya-katana");

        Assert.True(first.IsConnected);
        Assert.False(second.IsConnected);
        Assert.Null(second.PeerName);
    }
}
