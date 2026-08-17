using CoreLib.Identity;
using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// The two hand-rolled framings: Bluetooth's three frame types, which are told apart purely
/// by length, and the TCP hello, which grew a second field when identity arrived.
/// </summary>
public class WireFormatTests
{
    // ── Bluetooth control frames ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BleProtocol.ControlPing)]
    [InlineData(BleProtocol.ControlPong)]
    [InlineData(BleProtocol.ControlWakeWiFi)]
    public void A_control_frame_round_trips_with_its_kind(byte kind)
    {
        byte[] frame = BleProtocol.BuildControl(kind);

        Assert.Equal(BleProtocol.ControlLength, frame.Length);
        Assert.True(BleProtocol.TryParseControl(frame, out byte parsed));
        Assert.Equal(kind, parsed);
    }

    /// <summary>
    /// The kind byte used to be discarded on the phone, which was harmless while a pong was
    /// the only thing the computer ever sent. The wake frame made it a bug, so the kinds have
    /// to stay distinct.
    /// </summary>
    [Fact]
    public void The_control_kinds_are_distinct()
    {
        var kinds = new[] { BleProtocol.ControlPing, BleProtocol.ControlPong, BleProtocol.ControlWakeWiFi };
        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }

    /// <summary>
    /// Bluetooth tells its three frame types apart by length alone: 2 is control, 4 is a
    /// chunk receipt, 5 or more is data. If those ever overlap, a receipt gets reassembled as
    /// clipboard content.
    /// </summary>
    [Fact]
    public void Control_receipt_and_data_frames_cannot_be_confused()
    {
        byte[] control = BleProtocol.BuildControl(BleProtocol.ControlWakeWiFi);
        byte[] receipt = BleProtocol.BuildAck(7, 300);
        var data = BleFragmenter.Fragment("some clipboard text"u8.ToArray(), 64, messageId: 7);

        Assert.Equal(2, control.Length);
        Assert.Equal(4, receipt.Length);
        Assert.All(data, chunk => Assert.True(chunk.Length >= BleFragmenter.HeaderSize));
        Assert.True(BleFragmenter.HeaderSize >= 5);

        // Each parser must reject the other two shapes.
        Assert.False(BleProtocol.TryParseControl(receipt, out _));
        Assert.False(BleProtocol.TryParseAck(control, out _, out _));
        Assert.All(data, chunk => Assert.False(BleProtocol.TryParseControl(chunk, out _)));
        Assert.All(data, chunk => Assert.False(BleProtocol.TryParseAck(chunk, out _, out _)));
    }

    [Fact]
    public void A_receipt_round_trips_its_message_and_sequence()
    {
        byte[] receipt = BleProtocol.BuildAck(0x2A, 513);

        Assert.True(BleProtocol.TryParseAck(receipt, out byte messageId, out int sequence));
        Assert.Equal(0x2A, messageId);
        Assert.Equal(513, sequence);
    }

    [Fact]
    public void A_control_frame_with_the_wrong_marker_is_not_a_control_frame()
    {
        Assert.False(BleProtocol.TryParseControl(new byte[] { 0x00, BleProtocol.ControlPing }, out _));
        Assert.False(BleProtocol.TryParseControl(Array.Empty<byte>(), out _));
        Assert.False(BleProtocol.TryParseControl(new byte[] { BleProtocol.ControlMarker }, out _));
    }

    // ── Bluetooth extended control ──────────────────────────────────────────────────────

    /// <summary>
    /// The identity exchange does not fit in two bytes, so it borrows the one value a data
    /// chunk's message id can never take. If that assumption breaks, a public key gets
    /// reassembled as clipboard content.
    /// </summary>
    [Fact]
    public void An_extended_frame_round_trips_a_public_key()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        byte[] key = System.Text.Encoding.UTF8.GetBytes(identity.PublicKey);

        byte[] frame = BleProtocol.BuildExtended(BleProtocol.ExtendedHello, key);

        Assert.True(BleProtocol.TryParseExtended(frame, out byte kind, out byte[] payload));
        Assert.Equal(BleProtocol.ExtendedHello, kind);
        Assert.Equal(identity.PublicKey, System.Text.Encoding.UTF8.GetString(payload));
    }

    /// <summary>
    /// The counter used to wrap through zero after 255 messages, which would have made one
    /// clipboard item in every 256 parse as an identity exchange.
    /// </summary>
    [Fact]
    public void Message_ids_never_take_the_extended_marker()
    {
        byte counter = 0;

        for (int i = 0; i < 1000; i++)
        {
            byte id = BleProtocol.NextMessageId(ref counter);
            Assert.NotEqual(BleProtocol.ExtendedMarker, id);
        }
    }

    [Fact]
    public void Message_ids_still_advance_and_wrap()
    {
        byte counter = 0;
        var seen = new HashSet<byte>();

        for (int i = 0; i < 255; i++) seen.Add(BleProtocol.NextMessageId(ref counter));

        // Every value except the reserved marker, so nothing is skipped beyond it.
        Assert.Equal(255, seen.Count);
        Assert.DoesNotContain(BleProtocol.ExtendedMarker, seen);
    }

    /// <summary>
    /// An extended frame must be recognised before the receipt parser and the reassembler,
    /// and must not be mistaken for either.
    /// </summary>
    [Fact]
    public void An_extended_frame_is_not_a_receipt_a_control_or_a_data_chunk()
    {
        byte[] extended = BleProtocol.BuildExtended(BleProtocol.ExtendedHello, new byte[120]);

        Assert.False(BleProtocol.TryParseControl(extended, out _));
        Assert.False(BleProtocol.TryParseAck(extended, out _, out _));

        // And no real data chunk is mistaken for an extended frame, because its message id
        // can never be the marker.
        byte counter = 0;
        for (int i = 0; i < 300; i++)
        {
            byte id = BleProtocol.NextMessageId(ref counter);
            foreach (var chunk in BleFragmenter.Fragment(new byte[200], 64, id))
            {
                Assert.False(BleProtocol.TryParseExtended(chunk, out _, out _));
            }
        }
    }

    /// <summary>
    /// The Bluetooth hello carries a key, a device name and a mesh name, all separated by a
    /// newline that base64 can never contain.
    /// </summary>
    [Fact]
    public void A_bluetooth_hello_round_trips_all_four_fields()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        byte[] payload = BleProtocol.BuildHelloPayload(
            identity.PublicKey, "S21 FE", "Surya's Mesh", ephemeral.PublicKey);

        Assert.True(BleProtocol.TryParseHelloPayload(payload, out string key, out string name,
                                                     out string mesh, out string eph));
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("S21 FE", name);
        Assert.Equal("Surya's Mesh", mesh);
        Assert.Equal(ephemeral.PublicKey, eph);
    }

    /// <summary>
    /// Everything after the identity key is optional and positional, so a shorter payload still
    /// parses rather than being refused outright. A peer that offers no ephemeral key cannot
    /// agree a session, but that is the caller's decision to make, not the parser's.
    /// </summary>
    [Fact]
    public void A_shorter_bluetooth_hello_still_parses()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        byte[] keyOnly = BleProtocol.BuildHelloPayload(identity.PublicKey, null);
        Assert.True(BleProtocol.TryParseHelloPayload(keyOnly, out string k1, out string n1, out string m1, out string e1));
        Assert.Equal(identity.PublicKey, k1);
        Assert.Equal("", n1);
        Assert.Equal("", m1);
        Assert.Equal("", e1);

        byte[] keyAndName = BleProtocol.BuildHelloPayload(identity.PublicKey, "S21 FE");
        Assert.True(BleProtocol.TryParseHelloPayload(keyAndName, out string k2, out string n2, out string m2, out string e2));
        Assert.Equal(identity.PublicKey, k2);
        Assert.Equal("S21 FE", n2);
        Assert.Equal("", m2);
        Assert.Equal("", e2);
    }

    /// <summary>
    /// A hello carrying an ephemeral key but no names still lines up, because the fields are
    /// positional - an empty middle field must not shift the ephemeral into the mesh slot.
    /// </summary>
    [Fact]
    public void An_ephemeral_key_stays_in_its_own_field_when_the_names_are_empty()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        byte[] payload = BleProtocol.BuildHelloPayload(identity.PublicKey, null, null, ephemeral.PublicKey);

        Assert.True(BleProtocol.TryParseHelloPayload(payload, out string key, out string name,
                                                     out string mesh, out string eph));
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("", name);
        Assert.Equal("", mesh);
        Assert.Equal(ephemeral.PublicKey, eph);
    }

    /// <summary>A name containing a newline must not be able to forge a later field.</summary>
    [Fact]
    public void A_name_cannot_smuggle_a_separator()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        byte[] payload = BleProtocol.BuildHelloPayload(
            identity.PublicKey, "Evil\nNot my mesh", "Real Mesh", ephemeral.PublicKey);

        Assert.True(BleProtocol.TryParseHelloPayload(payload, out _, out string name,
                                                     out string mesh, out string eph));
        Assert.DoesNotContain("\n", name);
        Assert.Equal("Real Mesh", mesh);
        Assert.Equal(ephemeral.PublicKey, eph);
    }

    // ── TCP hello ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The mesh name rides in the hello as well as the pairing code, because the code only
    /// reaches a device at the moment it joins. A device paired before the mesh had a name
    /// would otherwise never learn one - which is exactly what happened, and left the phone
    /// calling it "your mesh" for ever.
    /// </summary>
    [Fact]
    public void A_hello_carries_the_mesh_name()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        byte[] frame = BuildHello("Surya's laptop", identity.PublicKey, "Surya's Mesh");

        Assert.True(TcpTransportConnection.TryParseHello(frame, out string name, out string key, out string mesh, out _));
        Assert.Equal("Surya's laptop", name);
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("Surya's Mesh", mesh);
    }

    /// <summary>
    /// The ephemeral key is the last field, and it is what a session is agreed from - so it has
    /// to survive the round trip intact, whole, and in its own slot.
    /// </summary>
    [Fact]
    public void A_hello_carries_the_ephemeral_key()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        byte[] frame = BuildHello("Surya's laptop", identity.PublicKey, "Surya's Mesh", ephemeral.PublicKey);

        Assert.True(TcpTransportConnection.TryParseHello(frame, out string name, out string key,
                                                         out string mesh, out string eph));
        Assert.Equal("Surya's laptop", name);
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("Surya's Mesh", mesh);
        Assert.Equal(ephemeral.PublicKey, eph);
    }

    /// <summary>A hello that stops before the later fields parses with them empty.</summary>
    [Fact]
    public void A_hello_without_a_mesh_name_still_parses()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        byte[] frame = BuildHello("Surya's laptop", identity.PublicKey);

        Assert.True(TcpTransportConnection.TryParseHello(frame, out string name, out string key,
                                                         out string mesh, out string eph));
        Assert.Equal("Surya's laptop", name);
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal("", mesh);
        Assert.Equal("", eph);
    }

    /// <summary>
    /// The hello is what carries identity, so it is now load-bearing rather than decorative:
    /// a peer that cannot be parsed out of it gets dropped.
    /// </summary>
    [Fact]
    public void A_hello_round_trips_a_name_and_a_public_key()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        byte[] frame = BuildHello("Surya's laptop", identity.PublicKey);

        Assert.True(TcpTransportConnection.TryParseHello(frame, out string name, out string key, out _, out _));
        Assert.Equal("Surya's laptop", name);
        Assert.Equal(identity.PublicKey, key);
    }

    [Fact]
    public void A_hello_survives_a_name_with_characters_outside_ascii()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();

        byte[] frame = BuildHello("Ordinateur de Renée 🖥", identity.PublicKey, "Le maillage", ephemeral.PublicKey);

        Assert.True(TcpTransportConnection.TryParseHello(frame, out string name, out string key, out _, out string eph));
        Assert.Equal("Ordinateur de Renée 🖥", name);
        Assert.Equal(identity.PublicKey, key);
        Assert.Equal(ephemeral.PublicKey, eph);
    }

    [Fact]
    public void A_hello_with_no_key_parses_but_carries_none()
    {
        byte[] frame = BuildHello("Anonymous", "");

        Assert.True(TcpTransportConnection.TryParseHello(frame, out string name, out string key, out _, out _));
        Assert.Equal("Anonymous", name);
        Assert.Equal("", key);
    }

    /// <summary>
    /// A truncated hello must be refused rather than parsed into something plausible - it is
    /// the frame an authorisation decision is made from.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    public void A_truncated_hello_is_refused(int keepBytes)
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        byte[] frame = BuildHello("Laptop", identity.PublicKey);

        byte[] truncated = frame.AsSpan(0, Math.Min(keepBytes, frame.Length)).ToArray();

        Assert.False(TcpTransportConnection.TryParseHello(truncated, out _, out _, out _, out _));
    }

    [Fact]
    public void A_hello_claiming_a_longer_name_than_it_carries_is_refused()
    {
        // Says the name is 200 bytes; supplies four.
        byte[] frame = new byte[] { 200, (byte)'a', (byte)'b', (byte)'c', (byte)'d' };

        Assert.False(TcpTransportConnection.TryParseHello(frame, out _, out _, out _, out _));
    }

    /// <summary>
    /// Mirrors the private builder, so the parser is tested against the real shape.
    ///
    /// The mesh name is omitted when null, which is what a build predating it sends - the case
    /// the optional trailing field exists to survive.
    /// </summary>
    private static byte[] BuildHello(string name, string publicKey, string? meshName = null,
                                     string? ephemeralKey = null)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(publicKey);
        byte[] meshBytes = System.Text.Encoding.UTF8.GetBytes(meshName ?? "");
        byte[] ephBytes = System.Text.Encoding.UTF8.GetBytes(ephemeralKey ?? "");

        int size = 1 + nameBytes.Length + 2 + keyBytes.Length
                 + (meshName == null ? 0 : 1 + meshBytes.Length)
                 + (ephemeralKey == null ? 0 : 2 + ephBytes.Length);
        var payload = new byte[size];

        payload[0] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);

        int keyOffset = 1 + nameBytes.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(keyOffset, 2), (ushort)keyBytes.Length);
        Buffer.BlockCopy(keyBytes, 0, payload, keyOffset + 2, keyBytes.Length);

        if (meshName != null)
        {
            int meshOffset = keyOffset + 2 + keyBytes.Length;
            payload[meshOffset] = (byte)meshBytes.Length;
            Buffer.BlockCopy(meshBytes, 0, payload, meshOffset + 1, meshBytes.Length);

            if (ephemeralKey != null)
            {
                int ephOffset = meshOffset + 1 + meshBytes.Length;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                    payload.AsSpan(ephOffset, 2), (ushort)ephBytes.Length);
                Buffer.BlockCopy(ephBytes, 0, payload, ephOffset + 2, ephBytes.Length);
            }
        }

        return payload;
    }
}
