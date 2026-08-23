using System;
using System.Collections.Generic;
using CoreLib.Transport.Fabric;

namespace CoreLib.Transport.Ble
{
    /// <summary>
    /// Devices not worth trying again yet, remembered three ways.
    ///
    /// <para><b>Why a refusal has to be remembered at all.</b> Every install advertises the same
    /// service UUID, so a scan finds every Mesh Sync device in range and not only the ones in this
    /// mesh. Refusing is not enough on its own: a refusal that is not remembered is a reconnection
    /// four seconds later, forever. A laptop here held a Bluetooth link to a phone in somebody
    /// else's mesh for as long as both were in range.</para>
    ///
    /// <para><b>Why three keys and not one.</b> They become knowable at different moments and they
    /// fail in different ways.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Address</b> - known before connecting, and useless against a phone that rotates
    /// its LE address for privacy, which is every modern phone.</item>
    /// <item><b>Fingerprint</b> - survives an address rotation, and cannot stop the connection,
    /// because nothing knows who a device is until its hello arrives. It refuses on the hello in
    /// about a second instead of after the full handshake grace.</item>
    /// <item><b>Advertised name</b> - the only one that is both known before connecting and
    /// survives a rotation. It decides who to <em>try</em>, never who is let in, so a device that
    /// spoofs a name gains nothing but its own exclusion.</item>
    /// </list>
    ///
    /// <para>Without the third, a foreign phone sitting closer than your own won every round: a
    /// scan round picks one candidate, connects, is refused, and the round is over. Six minutes of
    /// scans found a stranger's phone over and over and the paired one not once.</para>
    ///
    /// <para>This is promoted from <c>LinuxBleCentral</c>, where it was written and proven, so the
    /// other two heads stop being the versions without it.</para>
    /// </summary>
    public sealed class BleCooldowns
    {
        private readonly ILinkClock _clock;
        private readonly TimeSpan _duration;
        private readonly object _gate = new();

        private readonly Dictionary<string, DateTime> _byAddress = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _byFingerprint = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _byName = new(StringComparer.OrdinalIgnoreCase);

        public BleCooldowns(ILinkClock? clock = null, TimeSpan? duration = null)
        {
            _clock = clock ?? SystemClock.Instance;
            _duration = duration ?? RouteTimings.Default.RefusalCooldown;
        }

        public TimeSpan Duration => _duration;

        /// <summary>Records a refusal against everything known about the device at the time.</summary>
        public void Refuse(string? address, string? fingerprint, string? name)
        {
            var until = _clock.UtcNow + _duration;

            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(address)) _byAddress[address!] = until;
                if (!string.IsNullOrWhiteSpace(fingerprint)) _byFingerprint[fingerprint!] = until;
                if (!string.IsNullOrWhiteSpace(name)) _byName[name!] = until;
            }
        }

        /// <summary>True when this candidate should be skipped without connecting.</summary>
        public bool ShouldSkip(BleCandidate candidate) =>
            IsCool(_byAddress, candidate.Address) || IsCool(_byName, candidate.Name);

        /// <summary>True when a peer that has just identified itself should be dropped at once.</summary>
        public bool ShouldRefuseOnHello(string? fingerprint) => IsCool(_byFingerprint, fingerprint);

        /// <summary>
        /// Forgets everything, because the answer may have changed.
        ///
        /// Called when the peer set changes: a device refused a minute ago because it was not
        /// paired is a device the user may have just confirmed, and making them wait out five
        /// minutes for that reads as the confirmation not having worked.
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                _byAddress.Clear();
                _byFingerprint.Clear();
                _byName.Clear();
            }
        }

        public int Count
        {
            get { lock (_gate) return _byAddress.Count + _byFingerprint.Count + _byName.Count; }
        }

        private bool IsCool(Dictionary<string, DateTime> map, string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            lock (_gate)
            {
                if (!map.TryGetValue(key!, out var until)) return false;
                if (_clock.UtcNow < until) return true;

                map.Remove(key!);
                return false;
            }
        }
    }
}
