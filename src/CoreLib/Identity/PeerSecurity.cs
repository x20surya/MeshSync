using System;
using System.Collections.Generic;
using System.Linq;

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

            // A device that knocked while the code was on screen must not be confirmable an
            // hour after it came down.
            Pairing.Changed += OnPairingChanged;
        }

        private void OnPairingChanged()
        {
            if (!Pairing.IsOpen) ClearPendingPairings();
        }

        /// <summary>
        /// Loads or creates an identity and registry under one directory.
        ///
        /// <paramref name="protector"/> wraps the private key before it reaches the disk. Left
        /// null it is stored as it always was, which is what the tests use and what a platform
        /// with nothing to offer falls back to.
        /// </summary>
        public static PeerSecurity LoadOrCreate(string directory, IKeyProtector? protector = null) =>
            new(DeviceIdentity.LoadOrCreate(directory, protector), PeerRegistry.LoadOrCreate(directory));

        /// <summary>Nothing persisted. For tests.</summary>
        public static PeerSecurity CreateEphemeral() =>
            new(DeviceIdentity.CreateEphemeral(), PeerRegistry.CreateEphemeral());

        private readonly object _pendingGate = new();
        private readonly Dictionary<string, PendingPairing> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A device is waiting to be confirmed. Raised once per device per window, so a peer
        /// retrying every few seconds does not reopen the prompt each time.
        /// </summary>
        public event Action<PendingPairing>? PairingRequested;

        /// <summary>Devices knocking right now, for a UI to show and a human to compare.</summary>
        public IReadOnlyList<PendingPairing> PendingPairings
        {
            get { lock (_pendingGate) return _pending.Values.ToList(); }
        }

        /// <summary>
        /// Decides whether a connecting peer may stay.
        ///
        /// <para>A device already paired with is let through. A stranger is <em>not</em>, even
        /// while the pairing window is open - it is recorded as pending and refused, and the
        /// user is asked to compare its fingerprint against the one shown on the device that
        /// scanned the code. It connects on its next retry once confirmed.</para>
        ///
        /// <para>Refusing and retrying rather than holding the connection open is deliberate.
        /// Holding it would mean an authorisation decision that can answer "not yet", which
        /// changes the contract for all four transports at once; the retry loops that make the
        /// mesh reconnect already exist and cost one cycle. The user is comparing a fingerprint
        /// in that time anyway.</para>
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

            NotePending(publicKey!, fingerprint, name, address);
            return false;
        }

        private void NotePending(string publicKey, string fingerprint, string? name, string? address)
        {
            PendingPairing pending;

            lock (_pendingGate)
            {
                if (_pending.ContainsKey(fingerprint)) return; // Already asked; a retry is not a new request.

                pending = new PendingPairing(publicKey, name, address);
                _pending[fingerprint] = pending;
            }

            Diagnostics.Log.Write("Peers",
                $"{name ?? "A new device"} ({pending.ShortFingerprint}) is asking to join. Waiting for it to be confirmed.");

            try { PairingRequested?.Invoke(pending); }
            catch (Exception ex) { Diagnostics.Log.Write("Peers", "PairingRequested handler threw", ex); }
        }

        /// <summary>
        /// Accepts a device whose fingerprint the user has compared. It connects on its next
        /// retry, which the caller should nudge rather than wait out.
        /// </summary>
        public bool ConfirmPairing(string fingerprint)
        {
            // The window is the statement that a human is standing at this device inviting
            // something in. Confirming after it has shut would let a prompt left on screen be
            // answered by whoever walks past next.
            if (!Pairing.IsOpen)
            {
                Diagnostics.Log.Write("Peers", "Refusing to confirm a device: pairing is no longer open.");
                ClearPendingPairings();
                return false;
            }

            PendingPairing? pending;
            lock (_pendingGate)
            {
                if (!_pending.Remove(fingerprint, out pending)) return false;
            }

            Diagnostics.Log.Write("Peers", $"Confirmed {pending!.ShortFingerprint} by hand.");
            return Peers.Trust(pending.PublicKey, pending.Name, pending.Address);
        }

        /// <summary>
        /// Turns a device away. It is forgotten rather than blocked, so a genuine device that
        /// was rejected by a mis-tap can simply try again.
        /// </summary>
        public bool RejectPairing(string fingerprint)
        {
            PendingPairing? pending;
            lock (_pendingGate)
            {
                if (!_pending.Remove(fingerprint, out pending)) return false;
            }

            Diagnostics.Log.Write("Peers", $"Turned away {pending!.ShortFingerprint}.");
            return true;
        }

        /// <summary>
        /// Drops everything still waiting. Called when the pairing window shuts, so a device
        /// that knocked while the code was up cannot be confirmed an hour later.
        /// </summary>
        public void ClearPendingPairings()
        {
            int dropped;
            lock (_pendingGate)
            {
                dropped = _pending.Count;
                _pending.Clear();
            }

            if (dropped > 0) Diagnostics.Log.Write("Peers", $"Pairing closed with {dropped} device(s) unconfirmed.");
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

            Pairing.Changed -= OnPairingChanged;
            PairingRequested = null;

            Identity.Dispose();
        }
    }
}
