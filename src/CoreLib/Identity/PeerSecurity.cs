using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
        private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public DeviceIdentity Identity { get; }

        public PeerRegistry Peers { get; }

        /// <summary>Whether this device will currently accept a peer it has never met.</summary>
        public PairingWindow Pairing { get; } = new();

        public PeerSecurity(DeviceIdentity identity, PeerRegistry peers)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Peers = peers ?? throw new ArgumentNullException(nameof(peers));

            // A forgotten device must not leave a usable key behind in the cache.
            Peers.Changed += InvalidateStaleKeys;
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

        /// <summary>The AES-256 key shared with one peer, or null if it is not paired.</summary>
        public byte[]? KeyFor(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return null;

            if (_keys.TryGetValue(fingerprint, out var cached)) return cached;

            var peer = Peers.Find(fingerprint);
            if (peer == null) return null;

            try
            {
                var key = Identity.DeriveSharedKey(peer.PublicKey);
                _keys[fingerprint] = key;
                return key;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Peers", $"Could not derive a key for {DeviceIdentity.Shorten(fingerprint)}", ex);
                return null;
            }
        }

        /// <summary>Encrypts a payload for one peer. Null when that peer is not paired.</summary>
        public byte[]? EncryptFor(string fingerprint, byte contentType, ReadOnlySpan<byte> body)
        {
            var key = KeyFor(fingerprint);
            if (key == null) return null;

            try { return CryptoEngine.EncryptTagged(contentType, body, key); }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Peers", "Encryption failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Decrypts a payload, working out which peer sent it.
        ///
        /// <paramref name="hint"/> is used first when the transport knows who it is talking to,
        /// which TCP does from the hello. Bluetooth carries no such exchange, so the remaining
        /// peers are tried in turn. That is not a guess: AES-GCM authenticates, so a key that
        /// is not the right one fails rather than producing plausible rubbish - succeeding
        /// <em>is</em> the proof of who sent it.
        ///
        /// <para>The cost is one authentication attempt per paired device in the worst case,
        /// which for a personal device set is not worth optimising and for a large one would
        /// be.</para>
        /// </summary>
        public bool TryDecrypt(byte[] encrypted, string? hint, out DecryptedPayload result)
        {
            result = default;
            if (encrypted == null || encrypted.Length == 0) return false;

            if (!string.IsNullOrWhiteSpace(hint) && TryDecryptFrom(hint!, encrypted, out result)) return true;

            foreach (var peer in Peers.Peers)
            {
                if (string.Equals(peer.Fingerprint, hint, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryDecryptFrom(peer.Fingerprint, encrypted, out result)) return true;
            }

            return false;
        }

        private bool TryDecryptFrom(string fingerprint, byte[] encrypted, out DecryptedPayload result)
        {
            result = default;

            var peer = Peers.Find(fingerprint);
            if (peer == null) return false;

            var key = KeyFor(fingerprint);
            if (key == null) return false;

            try
            {
                var (contentType, body) = CryptoEngine.DecryptTagged(encrypted, key);
                result = new DecryptedPayload(peer, contentType, body);
                return true;
            }
            catch
            {
                // Expected whenever this is not the sender. Only the caller can tell that
                // every peer failed, so nothing is logged here.
                return false;
            }
        }

        /// <summary>Drops cached keys for devices that are no longer paired.</summary>
        private void InvalidateStaleKeys()
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var peer in Peers.Peers) live.Add(peer.Fingerprint);

            foreach (var fingerprint in _keys.Keys)
            {
                if (live.Contains(fingerprint)) continue;
                if (_keys.TryRemove(fingerprint, out var key)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Peers.Changed -= InvalidateStaleKeys;

            foreach (var key in _keys.Values) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            _keys.Clear();

            Identity.Dispose();
        }
    }
}
