using CoreLib.Identity;

namespace CoreLib.Tests;

/// <summary>
/// Covers who is let in and whose key opens what. This is the code that replaced
/// "every install shares one key and the listener accepts anything", so the tests are
/// written as the questions that used to have the wrong answer.
/// </summary>
public class PeerSecurityTests
{
    private static (PeerSecurity Local, DeviceIdentity Remote) Pair()
    {
        var local = PeerSecurity.CreateEphemeral();
        var remote = DeviceIdentity.CreateEphemeral();
        local.Peers.Trust(remote.PublicKey, "Test peer", "192.168.1.50");
        return (local, remote);
    }

    [Fact]
    public void A_paired_device_is_authorised()
    {
        var (local, remote) = Pair();
        using (local)
        {
            Assert.True(local.Authorise(remote.PublicKey));
        }
        remote.Dispose();
    }

    /// <summary>
    /// The listener used to accept any connection that could reach the port. It must not.
    /// </summary>
    [Fact]
    public void A_stranger_is_refused_while_pairing_is_closed()
    {
        using var local = PeerSecurity.CreateEphemeral();
        using var stranger = DeviceIdentity.CreateEphemeral();

        Assert.False(local.Pairing.IsOpen);
        Assert.False(local.Authorise(stranger.PublicKey));
        Assert.True(local.Peers.IsEmpty);
    }

    /// <summary>
    /// Showing the pairing code is the only signal this side gets that a stranger was
    /// invited, so it is the only moment one is accepted - and it is recorded as it goes,
    /// so the same device needs no second scan.
    /// </summary>
    [Fact]
    public void A_stranger_is_accepted_and_remembered_while_pairing_is_open()
    {
        using var local = PeerSecurity.CreateEphemeral();
        using var newcomer = DeviceIdentity.CreateEphemeral();

        local.Pairing.Open(TimeSpan.FromMinutes(1));

        Assert.True(local.Authorise(newcomer.PublicKey, "New phone", "192.168.1.77"));
        Assert.True(local.Peers.IsTrusted(newcomer.Fingerprint));

        local.Pairing.Close();

        // Still authorised afterwards: it is a paired device now, not a stranger.
        Assert.True(local.Authorise(newcomer.PublicKey));
    }

    [Fact]
    public void An_unreadable_key_is_never_authorised_even_while_pairing_is_open()
    {
        using var local = PeerSecurity.CreateEphemeral();
        local.Pairing.Open(TimeSpan.FromMinutes(1));

        Assert.False(local.Authorise("not a key"));
        Assert.False(local.Authorise(""));
        Assert.False(local.Authorise(null));
        Assert.True(local.Peers.IsEmpty);
    }

    /// <summary>
    /// A device handed its own public key would agree a secret with itself and echo its own
    /// clipboard back for ever.
    /// </summary>
    [Fact]
    public void A_device_refuses_its_own_identity()
    {
        using var local = PeerSecurity.CreateEphemeral();
        local.Pairing.Open(TimeSpan.FromMinutes(1));

        Assert.False(local.Authorise(local.Identity.PublicKey));
    }

    /// <summary>
    /// It has to shut on its own. A window that stayed open because nothing remembered to
    /// close it would leave the device accepting strangers indefinitely.
    /// </summary>
    [Fact]
    public void The_pairing_window_lapses_on_its_own()
    {
        using var local = PeerSecurity.CreateEphemeral();
        using var stranger = DeviceIdentity.CreateEphemeral();

        local.Pairing.Open(TimeSpan.FromMilliseconds(40));
        Assert.True(local.Pairing.IsOpen);

        Thread.Sleep(120);

        Assert.False(local.Pairing.IsOpen);
        Assert.Equal(TimeSpan.Zero, local.Pairing.Remaining);
        Assert.False(local.Authorise(stranger.PublicKey));
    }

    [Fact]
    public void A_payload_round_trips_between_two_paired_devices()
    {
        using var laptop = PeerSecurity.CreateEphemeral();
        using var phone = PeerSecurity.CreateEphemeral();

        laptop.Peers.Trust(phone.Identity.PublicKey, "Phone");
        phone.Peers.Trust(laptop.Identity.PublicKey, "Laptop");

        byte[] body = "correct horse battery staple"u8.ToArray();
        byte[]? sealed_ = laptop.EncryptFor(phone.Identity.Fingerprint, 0x00, body);

        Assert.NotNull(sealed_);
        Assert.True(phone.TryDecrypt(sealed_!, hint: null, out var decrypted));
        Assert.Equal(body, decrypted.Body);
        Assert.Equal((byte)0x00, decrypted.ContentType);
        Assert.Equal(laptop.Identity.Fingerprint, decrypted.Peer.Fingerprint);
    }

    /// <summary>
    /// Bluetooth carries no identity exchange, so the receiver works out the sender by which
    /// key authenticates the payload. With three peers paired, it has to pick the right one.
    /// </summary>
    [Fact]
    public void The_sender_is_identified_by_whose_key_opens_the_payload()
    {
        using var receiver = PeerSecurity.CreateEphemeral();
        using var alice = PeerSecurity.CreateEphemeral();
        using var bob = PeerSecurity.CreateEphemeral();
        using var carol = PeerSecurity.CreateEphemeral();

        foreach (var sender in new[] { alice, bob, carol })
        {
            receiver.Peers.Trust(sender.Identity.PublicKey);
            sender.Peers.Trust(receiver.Identity.PublicKey);
        }

        byte[]? fromBob = bob.EncryptFor(receiver.Identity.Fingerprint, 0x01, "from bob"u8);

        // No hint at all, exactly as a Bluetooth payload arrives.
        Assert.True(receiver.TryDecrypt(fromBob!, hint: null, out var decrypted));
        Assert.Equal(bob.Identity.Fingerprint, decrypted.Peer.Fingerprint);
        Assert.Equal("from bob"u8.ToArray(), decrypted.Body);
    }

    [Fact]
    public void A_payload_from_an_unpaired_device_is_refused()
    {
        using var receiver = PeerSecurity.CreateEphemeral();
        using var paired = PeerSecurity.CreateEphemeral();
        using var stranger = PeerSecurity.CreateEphemeral();

        receiver.Peers.Trust(paired.Identity.PublicKey);
        stranger.Peers.Trust(receiver.Identity.PublicKey);

        byte[]? fromStranger = stranger.EncryptFor(receiver.Identity.Fingerprint, 0x00, "inject this"u8);

        Assert.NotNull(fromStranger);
        Assert.False(receiver.TryDecrypt(fromStranger!, hint: null, out _));
    }

    [Fact]
    public void Nothing_can_be_encrypted_for_a_device_that_is_not_paired()
    {
        using var local = PeerSecurity.CreateEphemeral();
        using var stranger = DeviceIdentity.CreateEphemeral();

        Assert.Null(local.KeyFor(stranger.Fingerprint));
        Assert.Null(local.EncryptFor(stranger.Fingerprint, 0x00, "hello"u8));
    }

    /// <summary>Forgetting a device has to actually revoke it, not just hide it from a list.</summary>
    [Fact]
    public void Forgetting_a_device_revokes_its_key()
    {
        var (local, remote) = Pair();
        using (local)
        {
            Assert.NotNull(local.KeyFor(remote.Fingerprint));

            Assert.True(local.Peers.Forget(remote.Fingerprint));

            Assert.Null(local.KeyFor(remote.Fingerprint));
            Assert.False(local.Authorise(remote.PublicKey));
        }
        remote.Dispose();
    }
}
