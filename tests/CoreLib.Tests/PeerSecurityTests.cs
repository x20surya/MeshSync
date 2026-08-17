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

    /// <summary>
    /// Opens the two ends of one connection, the way a transport does: both sides mint an
    /// ephemeral, announce it in a hello, and agree from what the other announced.
    /// </summary>
    private static (PeerSession A, PeerSession B) Connect(PeerSecurity a, PeerSecurity b)
    {
        using var ephemeralA = EphemeralKeyPair.Create();
        using var ephemeralB = EphemeralKeyPair.Create();

        var sessionA = a.OpenSession(b.Identity.PublicKey, ephemeralA, ephemeralB.PublicKey);
        var sessionB = b.OpenSession(a.Identity.PublicKey, ephemeralB, ephemeralA.PublicKey);

        Assert.NotNull(sessionA);
        Assert.NotNull(sessionB);
        return (sessionA!, sessionB!);
    }

    [Fact]
    public void A_payload_round_trips_between_two_paired_devices()
    {
        using var laptop = PeerSecurity.CreateEphemeral();
        using var phone = PeerSecurity.CreateEphemeral();

        laptop.Peers.Trust(phone.Identity.PublicKey, "Phone");
        phone.Peers.Trust(laptop.Identity.PublicKey, "Laptop");

        var (fromLaptop, atPhone) = Connect(laptop, phone);
        using (fromLaptop)
        using (atPhone)
        {
            byte[] body = "correct horse battery staple"u8.ToArray();
            byte[]? sealed_ = fromLaptop.Encrypt(0x00, body);

            Assert.NotNull(sealed_);
            Assert.True(atPhone.TryDecrypt(sealed_!, out var decrypted));
            Assert.Equal(body, decrypted.Body);
            Assert.Equal((byte)0x00, decrypted.ContentType);
            Assert.Equal(laptop.Identity.Fingerprint, decrypted.Peer.Fingerprint);
        }
    }

    /// <summary>
    /// The session is the answer to who sent a payload. This used to be worked out by trying
    /// every paired device's key until one authenticated, because the key belonged to the peer;
    /// a payload sealed on one connection must now simply not open on another.
    /// </summary>
    [Fact]
    public void A_payload_does_not_open_on_a_different_connection()
    {
        using var receiver = PeerSecurity.CreateEphemeral();
        using var alice = PeerSecurity.CreateEphemeral();
        using var bob = PeerSecurity.CreateEphemeral();

        foreach (var sender in new[] { alice, bob })
        {
            receiver.Peers.Trust(sender.Identity.PublicKey);
            sender.Peers.Trust(receiver.Identity.PublicKey);
        }

        var (fromBob, atReceiverFromBob) = Connect(bob, receiver);
        var (_, atReceiverFromAlice) = Connect(alice, receiver);

        using (fromBob)
        using (atReceiverFromBob)
        using (atReceiverFromAlice)
        {
            byte[]? sealed_ = fromBob.Encrypt(0x01, "from bob"u8);

            Assert.True(atReceiverFromBob.TryDecrypt(sealed_!, out var decrypted));
            Assert.Equal(bob.Identity.Fingerprint, decrypted.Peer.Fingerprint);
            Assert.Equal("from bob"u8.ToArray(), decrypted.Body);

            // Alice's connection to the same receiver cannot open Bob's traffic.
            Assert.False(atReceiverFromAlice.TryDecrypt(sealed_!, out _));
        }
    }

    /// <summary>
    /// The same two devices reconnecting agree a new key, so a payload captured from an earlier
    /// connection cannot be opened by a later one. This is forward secrecy at the session layer.
    /// </summary>
    [Fact]
    public void A_payload_from_an_earlier_connection_does_not_open_on_a_later_one()
    {
        using var laptop = PeerSecurity.CreateEphemeral();
        using var phone = PeerSecurity.CreateEphemeral();

        laptop.Peers.Trust(phone.Identity.PublicKey);
        phone.Peers.Trust(laptop.Identity.PublicKey);

        var (firstSend, _) = Connect(laptop, phone);
        byte[]? captured = firstSend.Encrypt(0x00, "yesterday's clipboard"u8);
        firstSend.Dispose();

        var (_, laterReceive) = Connect(laptop, phone);
        using (laterReceive)
        {
            Assert.False(laterReceive.TryDecrypt(captured!, out _));
        }
    }

    [Fact]
    public void No_session_can_be_opened_with_a_device_that_is_not_paired()
    {
        using var local = PeerSecurity.CreateEphemeral();
        using var stranger = DeviceIdentity.CreateEphemeral();
        using var ephemeral = EphemeralKeyPair.Create();
        using var strangerEphemeral = EphemeralKeyPair.Create();

        Assert.Null(local.OpenSession(stranger.PublicKey, ephemeral, strangerEphemeral.PublicKey));
    }

    /// <summary>A peer that offers no ephemeral key cannot agree one, so it gets no session.</summary>
    [Fact]
    public void A_peer_that_offers_no_ephemeral_key_gets_no_session()
    {
        var (local, remote) = Pair();
        using (local)
        {
            using var ephemeral = EphemeralKeyPair.Create();

            Assert.Null(local.OpenSession(remote.PublicKey, ephemeral, null));
            Assert.Null(local.OpenSession(remote.PublicKey, ephemeral, ""));
        }
        remote.Dispose();
    }

    /// <summary>
    /// Forgetting a device has to revoke it immediately, including on a link that is already
    /// up. The key used to live in a cache the registry could clear; a session holds its own
    /// copy, so without an explicit check a forgotten device would keep syncing until its link
    /// happened to drop.
    /// </summary>
    [Fact]
    public void Forgetting_a_device_revokes_a_live_session()
    {
        using var laptop = PeerSecurity.CreateEphemeral();
        using var phone = PeerSecurity.CreateEphemeral();

        laptop.Peers.Trust(phone.Identity.PublicKey);
        phone.Peers.Trust(laptop.Identity.PublicKey);

        var (atLaptop, atPhone) = Connect(laptop, phone);
        using (atLaptop)
        using (atPhone)
        {
            byte[]? before = atLaptop.Encrypt(0x00, "still paired"u8);
            Assert.NotNull(before);
            Assert.True(atPhone.TryDecrypt(before!, out _));

            Assert.True(laptop.Peers.Forget(phone.Identity.Fingerprint));

            Assert.False(atLaptop.IsUsable);
            Assert.Null(atLaptop.Encrypt(0x00, "after forgetting"u8));
            Assert.False(atLaptop.TryDecrypt(before!, out _));
            Assert.False(laptop.Authorise(phone.Identity.PublicKey));
        }
    }
}
