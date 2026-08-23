using System;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using CoreLib.Transport.Fabric;

namespace CoreLib.Transport.Ble
{
    /// <summary>
    /// What to advertise, and which advertisements are worth connecting to.
    ///
    /// <para>The one place a head asks "is this one of mine". Keeping it here rather than in three
    /// daemons is the rule <c>AGENTS.md</c> already states: a platform should be wiring and
    /// storage, never its own copy of a rule.</para>
    ///
    /// <para><b>Before there is a mesh key</b> - a fresh install, or a v0.3 registry that has just
    /// been upgraded - nothing is advertised beyond the service UUID and every Mesh Sync device is
    /// still a candidate. That is precisely how every build before this one behaved, so an
    /// upgrading mesh keeps working while the key is still crossing.</para>
    /// </summary>
    public sealed class MeshDiscovery
    {
        private readonly PeerSecurity _security;
        private readonly ILinkClock _clock;

        public MeshDiscovery(PeerSecurity security, ILinkClock? clock = null)
        {
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>What this device should be publishing right now.</summary>
        public BleAdvertisement CurrentAdvertisement(BleCapability capability)
        {
            var flags = MeshBeaconFlags.None;
            if (capability.HasFlag(BleCapability.Central)) flags |= MeshBeaconFlags.CanBeCentral;

            var key = _security.Peers.MeshKey;

            // A device with nothing paired and its pairing window open advertises under a tag the
            // joiner can compute from the code it just scanned. That is what lets two devices pair
            // with no network at all - the one step of this project that has never honoured its
            // own central claim.
            if (_security.Pairing.IsOpen)
            {
                flags |= MeshBeaconFlags.PairingOpen;

                if (key == null)
                {
                    return new BleAdvertisement
                    {
                        Beacon = MeshBeacon.Build(MeshBeacon.PairingSecretFrom(_security.Identity.PublicKey),
                                                  _clock.UtcNow, flags),
                    };
                }
            }

            return new BleAdvertisement { Beacon = MeshBeacon.Build(key, _clock.UtcNow, flags) };
        }

        /// <summary>
        /// True when a candidate is worth opening a connection to.
        ///
        /// <para>The saving is the whole point: a device from another mesh costs one comparison
        /// instead of a connect, an MTU exchange, a hello, and this device's name and mesh name
        /// given away to a stranger before either end has authorised anything.</para>
        /// </summary>
        public bool Accepts(BleCandidate candidate)
        {
            var key = _security.Peers.MeshKey;

            // Nothing to check against yet. Accepting everything is what every build before this
            // did, and the handshake grace still bounds what a stranger costs.
            if (key == null && !_security.Pairing.IsOpen) return true;

            if (candidate.Beacon is not { Length: > 0 } beacon)
            {
                // A device that publishes no beacon is either older than this or not ours. While
                // this device has no key of its own it is worth trying; once it has one, a silent
                // advertisement is somebody else's business.
                return key == null;
            }

            if (key != null && MeshBeacon.Verify(key, beacon, _clock.UtcNow, out _)) return true;

            if (_security.Pairing.IsOpen && AcceptsAsPairingTarget(beacon)) return true;

            return false;
        }

        /// <summary>
        /// True when this beacon is the device whose pairing code was scanned.
        ///
        /// The joiner knows the inviter's public key from the code, so it can compute the same tag
        /// and find exactly that device and nothing else.
        /// </summary>
        public bool AcceptsAsPairingTarget(byte[] beacon)
        {
            foreach (var pending in _security.PendingPairings)
            {
                var secret = MeshBeacon.PairingSecretFrom(pending.PublicKey);
                if (MeshBeacon.Verify(secret, beacon, _clock.UtcNow, out _)) return true;
            }

            var invited = InvitedPublicKey;
            if (string.IsNullOrWhiteSpace(invited)) return false;

            return MeshBeacon.Verify(MeshBeacon.PairingSecretFrom(invited!), beacon, _clock.UtcNow, out _);
        }

        /// <summary>
        /// The public key from a pairing code this device has just scanned, while it looks for
        /// that device over the radio. Cleared when the pairing window shuts.
        /// </summary>
        public string? InvitedPublicKey { get; set; }

        /// <summary>
        /// Mints a mesh key if this device has peers and no key, so an upgraded mesh converges.
        ///
        /// <para>Deliberately not called on a device with nothing paired: a key minted before the
        /// first pairing would be replaced by the inviter's anyway, and minting one per fresh
        /// install would put a beacon on the air for a mesh of one.</para>
        /// </summary>
        public byte[]? MintIfDue()
        {
            if (_security.Peers.IsEmpty) return null;
            if (_security.Peers.HasMeshKey) return _security.Peers.MeshKey;

            var key = _security.Peers.MintMeshKeyIfMissing();
            Log.Write("Ble", "This device minted the mesh discovery key; offering it to every peer.");
            return key;
        }

        /// <summary>
        /// Takes a key a peer offered, and says whether this device's advertisement has to change.
        ///
        /// Lowest key wins, so two halves that minted separately converge in one exchange without
        /// a coordinator and without either having to be in charge.
        /// </summary>
        public bool Adopt(byte[]? offered) => _security.Peers.AdoptMeshKey(offered);
    }
}
