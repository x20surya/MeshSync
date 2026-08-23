using CoreLib.Identity;

namespace CoreLib.Tests;

public class PeerRegistryTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void A_paired_device_survives_a_restart()
    {
        string directory = TempDirectory();
        using var peer = DeviceIdentity.CreateEphemeral();

        var first = PeerRegistry.LoadOrCreate(directory);
        Assert.True(first.Trust(peer.PublicKey, "Surya's laptop", "192.168.1.20"));

        var second = PeerRegistry.LoadOrCreate(directory);

        Assert.Equal(1, second.Count);
        var restored = second.Find(peer.Fingerprint);
        Assert.NotNull(restored);
        Assert.Equal("Surya's laptop", restored!.Name);
        Assert.Equal("192.168.1.20", restored.LastAddress);
    }

    [Fact]
    public void The_registry_holds_several_devices()
    {
        var registry = PeerRegistry.CreateEphemeral();

        using var a = DeviceIdentity.CreateEphemeral();
        using var b = DeviceIdentity.CreateEphemeral();
        using var c = DeviceIdentity.CreateEphemeral();

        registry.Trust(a.PublicKey, "Laptop");
        registry.Trust(b.PublicKey, "Phone");
        registry.Trust(c.PublicKey, "Tablet");

        Assert.Equal(3, registry.Count);
        Assert.True(registry.IsTrusted(a.Fingerprint));
        Assert.True(registry.IsTrusted(b.Fingerprint));
        Assert.True(registry.IsTrusted(c.Fingerprint));
    }

    /// <summary>Pairing the same device twice updates it rather than duplicating it.</summary>
    [Fact]
    public void Trusting_a_device_twice_updates_it()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Old name", "192.168.1.10");
        registry.Trust(peer.PublicKey, "New name", "192.168.1.99");

        Assert.Equal(1, registry.Count);
        Assert.Equal("New name", registry.Find(peer.Fingerprint)!.Name);
        Assert.Equal("192.168.1.99", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    /// <summary>
    /// A hello that carries no address must not erase the address we successfully dialled
    /// last time, or a reconnect would forget where the device lives.
    /// </summary>
    [Fact]
    public void An_update_without_an_address_keeps_the_one_already_known()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Laptop", "192.168.1.10");
        registry.Trust(peer.PublicKey, "Laptop");

        Assert.Equal("192.168.1.10", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    /// <summary>The address is a hint. Identity is the key, so a moved device is still itself.</summary>
    [Fact]
    public void A_device_that_changes_address_is_still_the_same_device()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Laptop", "192.168.1.10");
        registry.NoteSeen(peer.Fingerprint, "192.168.1.240");

        Assert.Equal(1, registry.Count);
        Assert.True(registry.IsTrusted(peer.Fingerprint));
        Assert.Equal("192.168.1.240", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    /// <summary>
    /// A pairing code carries <c>host:port</c>; a connection reports the host alone. The second
    /// must not erase the first, or a peer that does not listen on the default port becomes
    /// undialable the moment it first connects.
    /// </summary>
    [Fact]
    public void Seeing_a_peer_does_not_drop_the_port_it_was_paired_with()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Second device", "192.168.1.41:45091");
        registry.NoteSeen(peer.Fingerprint, "192.168.1.41");

        Assert.Equal("192.168.1.41:45091", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    /// <summary>The guard is about the port, not about pinning a device to one address.</summary>
    [Fact]
    public void A_peer_that_moves_to_another_host_still_updates()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Second device", "192.168.1.41:45091");
        registry.NoteSeen(peer.Fingerprint, "192.168.1.99");

        Assert.Equal("192.168.1.99", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    /// <summary>A newer port for the same host is still news.</summary>
    [Fact]
    public void A_peer_that_changes_port_still_updates()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Second device", "192.168.1.41:45091");
        registry.NoteSeen(peer.Fingerprint, "192.168.1.41:45092");

        Assert.Equal("192.168.1.41:45092", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    /// <summary>
    /// An IPv6 address is full of colons and is not a host and port. Reading one as such would
    /// refuse every later address for that peer.
    /// </summary>
    [Fact]
    public void An_ipv6_address_is_not_mistaken_for_a_host_and_port()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Second device", "fe80::1c2d:3e4f:5a6b:7c8d");
        registry.NoteSeen(peer.Fingerprint, "192.168.1.41");

        Assert.Equal("192.168.1.41", registry.Find(peer.Fingerprint)!.LastAddress);
    }

    [Fact]
    public void An_unreadable_key_is_refused()
    {
        var registry = PeerRegistry.CreateEphemeral();

        Assert.False(registry.Trust("not a key"));
        Assert.False(registry.Trust(""));
        Assert.True(registry.IsEmpty);
    }

    [Fact]
    public void Forgetting_removes_a_device()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey);
        Assert.True(registry.Forget(peer.Fingerprint));

        Assert.True(registry.IsEmpty);
        Assert.False(registry.Forget(peer.Fingerprint));
    }

    /// <summary>Introduction shares the rest of the set, never the requester back to itself.</summary>
    [Fact]
    public void Introduction_offers_every_peer_except_the_one_asking()
    {
        var registry = PeerRegistry.CreateEphemeral();

        using var phone = DeviceIdentity.CreateEphemeral();
        using var tablet = DeviceIdentity.CreateEphemeral();
        using var desktop = DeviceIdentity.CreateEphemeral();

        registry.Trust(phone.PublicKey, "Phone");
        registry.Trust(tablet.PublicKey, "Tablet");
        registry.Trust(desktop.PublicKey, "Desktop");

        var offered = registry.PeersToIntroduceTo(phone.Fingerprint);

        Assert.Equal(2, offered.Count);
        Assert.DoesNotContain(offered, p => p.Fingerprint == phone.Fingerprint);
        Assert.Contains(offered, p => p.Fingerprint == tablet.Fingerprint);
        Assert.Contains(offered, p => p.Fingerprint == desktop.Fingerprint);
    }

    /// <summary>
    /// A truncated or corrupt file must start empty rather than throw. An app that will not
    /// start is a worse outcome than one that asks to be re-paired.
    /// </summary>
    [Fact]
    public void A_corrupt_file_starts_empty_instead_of_throwing()
    {
        string directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "peers.json"), "{ this is not json");

        var registry = PeerRegistry.LoadOrCreate(directory);

        Assert.True(registry.IsEmpty);
    }

    [Fact]
    public void Changes_raise_the_changed_event()
    {
        var registry = PeerRegistry.CreateEphemeral();
        using var peer = DeviceIdentity.CreateEphemeral();

        int changes = 0;
        registry.Changed += () => changes++;

        registry.Trust(peer.PublicKey, "Phone");
        Assert.Equal(1, changes);

        registry.Forget(peer.Fingerprint);
        Assert.Equal(2, changes);
    }
}
