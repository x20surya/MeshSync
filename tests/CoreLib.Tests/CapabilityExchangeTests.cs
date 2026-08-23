using CoreLib.Identity;
using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// What each device announces its radio can do, on both wires.
///
/// <para><b>Why this had to go on the wire.</b> <c>BleRoleRules</c> is capability first, and every
/// call site passed <c>BleCapability.Both</c> for the peer - documented as "the optimistic
/// reading". It is the reason two devices that both cannot advertise sit waiting for each other,
/// and it resolves only by luck and only once a link already exists.</para>
///
/// <para>The socket hello is the half that matters most: a Linux box that cannot advertise tells
/// the phone so long before the two ever meet on the air.</para>
/// </summary>
public class CapabilityExchangeTests
{
    // ── TCP ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BleCapability.None)]
    [InlineData(BleCapability.Central)]
    [InlineData(BleCapability.Peripheral)]
    [InlineData(BleCapability.Both)]
    public void The_socket_hello_carries_the_capability_unchanged(BleCapability capability)
    {
        var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        var frame = TcpTransportConnection.BuildHelloFrame(
            "Laptop", identity.PublicKey, "Surya's Mesh", ephemeral.PublicKey, capability);

        // Strip the 8-byte header the frame carries.
        var payload = frame.Skip(8).ToArray();

        Assert.True(TcpTransportConnection.TryParseHello(payload, out string name, out string key,
            out string mesh, out string eph, out var read));

        Assert.Equal("Laptop", name);
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("Surya's Mesh", mesh);
        Assert.Equal(ephemeral.PublicKey, eph);
        Assert.Equal(capability, read);
    }

    /// <summary>
    /// A peer that predates the field sends a shorter payload, which still parses - and reads as
    /// both halves, which is exactly the behaviour it had before the field existed.
    /// </summary>
    [Fact]
    public void A_hello_without_the_field_reads_as_both_halves()
    {
        var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        var frame = TcpTransportConnection.BuildHelloFrame("Laptop", identity.PublicKey, "m", ephemeral.PublicKey);
        var payload = frame.Skip(8).ToArray();

        // Drop the trailing capability byte, as a version 3 peer would never have sent it.
        var older = payload.Take(payload.Length - 1).ToArray();

        Assert.True(TcpTransportConnection.TryParseHello(older, out _, out string key, out _, out string eph, out var read));
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal(ephemeral.PublicKey, eph);
        Assert.Equal(BleCapability.Both, read);
    }

    /// <summary>A newer peer may set bits this build does not know. Mask them rather than misread them.</summary>
    [Fact]
    public void Unknown_capability_bits_are_masked_off()
    {
        var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        var frame = TcpTransportConnection.BuildHelloFrame("L", identity.PublicKey, "m", ephemeral.PublicKey);
        var payload = frame.Skip(8).ToArray();
        payload[^1] = 0xF1;   // Central, plus four bits from the future

        Assert.True(TcpTransportConnection.TryParseHello(payload, out _, out _, out _, out _, out var read));
        Assert.Equal(BleCapability.Central, read);
    }

    [Fact]
    public void The_wire_version_moved_to_four()
    {
        // Read from the constant rather than copied, because a copy in a test file goes stale the
        // moment it is bumped and the test then passes for the wrong reason. That has happened
        // twice in this project already.
        Assert.Equal(4, TcpTransportConnection.ProtocolVersion);
    }

    // ── Bluetooth ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BleCapability.Central)]
    [InlineData(BleCapability.Peripheral)]
    [InlineData(BleCapability.Both)]
    public void The_radio_hello_carries_the_capability_as_a_fifth_field(BleCapability capability)
    {
        var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        var payload = BleProtocol.BuildHelloPayload(identity.PublicKey, "S21 FE", "Surya's Mesh",
                                                    ephemeral.PublicKey, capability);

        Assert.True(BleProtocol.TryParseHelloPayload(payload, out string key, out string name,
            out string mesh, out string eph, out var read));

        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("S21 FE", name);
        Assert.Equal("Surya's Mesh", mesh);
        Assert.Equal(ephemeral.PublicKey, eph);
        Assert.Equal(capability, read);
    }

    [Fact]
    public void A_radio_hello_of_four_fields_still_parses()
    {
        var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        var payload = BleProtocol.BuildHelloPayload(identity.PublicKey, "S21 FE", "m", ephemeral.PublicKey);

        Assert.True(BleProtocol.TryParseHelloPayload(payload, out string key, out _, out _, out string eph, out var read));
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal(ephemeral.PublicKey, eph);
        Assert.Equal(BleCapability.Both, read);
    }

    /// <summary>
    /// The whole frame still has to fit in one attribute write. A hello is written in one go
    /// rather than through the fragmenter, because an extended frame is marked by a leading zero
    /// and a fragmented chunk starts with its message id, so the two shapes cannot be mixed.
    /// </summary>
    [Fact]
    public void The_radio_hello_still_fits_in_one_attribute_write()
    {
        var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        var payload = BleProtocol.BuildHelloPayload(identity.PublicKey,
            new string('n', BleProtocol.MaxDeviceNameBytes / 4),
            new string('m', PeerRegistry.MaxMeshNameLength),
            ephemeral.PublicKey, BleCapability.Both);

        var frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello, payload);

        Assert.True(frame.Length <= BleProtocol.MaxAttributeValueBytes,
            $"a hello of {frame.Length} bytes will not fit in a {BleProtocol.MaxAttributeValueBytes}-byte write");
    }

    // ── the registry remembers it ────────────────────────────────────────────

    [Fact]
    public void A_peers_capability_is_remembered_and_offered_to_the_policy()
    {
        var registry = PeerRegistry.CreateEphemeral();
        var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Framework 13");
        Assert.Empty(registry.Capabilities);

        registry.NoteSeen(peer.Fingerprint, null, null, BleCapability.Central);

        Assert.Equal(BleCapability.Central, registry.Capabilities[peer.Fingerprint]);
        Assert.Equal(BleCapability.Central, registry.Find(peer.Fingerprint)!.BleCapability);
    }

    /// <summary>
    /// A peer that has never announced is absent rather than recorded as both, so the optimistic
    /// reading applies only where nothing is known.
    /// </summary>
    [Fact]
    public void A_peer_that_has_never_announced_is_absent_from_the_map()
    {
        var registry = PeerRegistry.CreateEphemeral();
        var peer = DeviceIdentity.CreateEphemeral();

        registry.Trust(peer.PublicKey, "Unknown");
        registry.NoteSeen(peer.Fingerprint, "192.168.0.9", "Unknown");

        Assert.DoesNotContain(peer.Fingerprint, registry.Capabilities.Keys);
    }
}
