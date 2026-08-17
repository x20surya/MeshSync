using System;

namespace CoreLib.Identity
{
    /// <summary>The peer a payload turned out to have come from, and its decrypted contents.</summary>
    public readonly struct DecryptedPayload
    {
        public DecryptedPayload(PeerRecord peer, byte contentType, byte[] body)
        {
            Peer = peer;
            ContentType = contentType;
            Body = body;
        }

        public PeerRecord Peer { get; }
        public byte ContentType { get; }
        public byte[] Body { get; }
    }

    /// <summary>
    /// Everything to do with who this device trusts and what it encrypts to them with.
    ///
    /// <para>Both apps used to hold a single key from
    /// <c>DeriveKey("MasterPassword123", "Salt")</c> and a boolean's worth of trust - the
    /// listener accepted anything that reached it. This owns the replacement: one identity, a
    /// set of peers, and a distinct key per pair derived by agreement rather than from a
    /// literal.</para>
    ///
    /// <para><b>Why a key per peer rather than one for the mesh.</b> With two devices the
    /// distinction is invisible. With three, a single shared key means any paired device can
    /// read traffic meant for another pair, which is not what "paired with" should mean. It is
    /// the same work either way.</para>
    /// </summary>
    public sealed class PeerSecurity : IDisposable
    {
        private bool _disposed;

        public DeviceIdentity Identity { get; }

        public PeerRegistry Peers { get; }

        /// <summary>Whether this device will currently accept a peer it has never met.</summary>
        public PairingWindow Pairing { get; } = new();

        public PeerSecurity(DeviceIdentity identity, PeerRegistry peers)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Peers = peers ?? throw new ArgumentNullException(nameof(peers));
        }

        /// <summary>Loads or creates an identity and registry under one directory.</summary>
        public static PeerSecurity LoadOrCreate(string directory) =>
            new(DeviceIdentity.LoadOrCreate(directory), PeerRegistry.LoadOrCreate(directory));

        /// <summary>Nothing persisted. For tests.</summary>
        public static PeerSecurity CreateEphemeral() =>
            new(DeviceIdentity.CreateEphemeral(), PeerRegistry.CreateEphemeral());

        /// <summary>
        /// Decides whether a connecting peer may stay.
        ///
        /// A device already paired with is let through. A stranger is let through only while
        /// the pairing window is open - see <see cref="PairingWindow"/> for why that is the
        /// only moment this side can know a stranger was invited - and is recorded as it goes,
        /// so the same device is recognised on every subsequent connection without another
        /// scan.
        /// </summary>
        public bool Authorise(string? publicKey, string? name = null, string? address = null)
        {
            if (string.IsNullOrWhiteSpace(publicKey)) return false;
            if (!DeviceIdentity.IsValidPublicKey(publicKey)) return false;

            string fingerprint = DeviceIdentity.FingerprintOf(publicKey);

            // Refusing this is not paranoia: a device that has somehow been handed its own
            // public key would derive a shared secret with itself and echo its own clipboard
            // back forever.
            if (string.Equals(fingerprint, Identity.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.Log.Write("Peers", "Refusing a connection from this device's own identity.");
                return false;
            }

            if (Peers.IsTrusted(fingerprint))
            {
                Peers.NoteSeen(fingerprint, address, name);
                return true;
            }

            if (!Pairing.IsOpen)
            {
                Diagnostics.Log.Write("Peers",
                    $"Refusing {DeviceIdentity.Shorten(fingerprint)}: not a paired device, and pairing is not open.");
                return false;
            }

            Diagnostics.Log.Write("Peers",
                $"Accepting {name ?? "a new device"} ({DeviceIdentity.Shorten(fingerprint)}) while pairing is open.");

            return Peers.Trust(publicKey!, name, address);
        }

        /// <summary>
        /// Agrees the key for one connection, once both ends have announced an ephemeral key.
        ///
        /// <para>Returns null for a device this one has not paired with, so a session cannot
        /// exist for a peer that was never authorised. The caller owns the result and must
        /// dispose it when the connection ends - that disposal is what makes the traffic
        /// unrecoverable afterwards.</para>
        /// </summary>
        public PeerSession? OpenSession(string? peerPublicKey,
                                        EphemeralKeyPair localEphemeral,
                                        string? peerEphemeralPublicKey)
        {
            if (_disposed) return null;
            if (string.IsNullOrWhiteSpace(peerPublicKey) || string.IsNullOrWhiteSpace(peerEphemeralPublicKey)) return null;
            if (localEphemeral == null) return null;
            if (!DeviceIdentity.IsValidPublicKey(peerPublicKey)) return null;

            string fingerprint = DeviceIdentity.FingerprintOf(peerPublicKey!);

            var peer = Peers.Find(fingerprint);
            if (peer == null)
            {
                Diagnostics.Log.Write("Peers",
                    $"Refusing a session with {DeviceIdentity.Shorten(fingerprint)}: not a paired device.");
                return null;
            }

            try
            {
                var key = SessionKeys.Derive(Identity, peer.PublicKey, localEphemeral, peerEphemeralPublicKey!);
                return new PeerSession(peer, key, Peers);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Peers",
                    $"Could not agree a session key with {DeviceIdentity.Shorten(fingerprint)}", ex);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Identity.Dispose();
        }
    }
}
