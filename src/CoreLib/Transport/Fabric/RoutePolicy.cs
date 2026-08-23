using System;
using System.Collections.Generic;
using System.Linq;
using CoreLib.Identity;

namespace CoreLib.Transport.Fabric
{
    /// <summary>
    /// Everything about this device that decides which routes it wants.
    ///
    /// <para>Gathered by the head - the screen watcher, the transport preference, the radio probe -
    /// and passed in whole, so the rule that consumes it is a function of its arguments and
    /// nothing else.</para>
    /// </summary>
    public sealed record LocalConditions
    {
        public required string LocalFingerprint { get; init; }

        /// <summary>Wi&#8209;Fi follows the screen: someone looking at a device may be about to copy.</summary>
        public bool ScreenOn { get; init; } = true;

        /// <summary>False when there is no Wi&#8209;Fi or Ethernet transport that could reach a LAN peer.</summary>
        public bool HasUsableNetwork { get; init; } = true;

        public TransportPreference Transport { get; init; } = TransportPreference.Both;

        /// <summary>
        /// What this device's radio can actually do, taken from whether the peripheral half
        /// <em>started</em> - never from what the adapter claimed.
        ///
        /// Reporting <see cref="BleCapability.Both"/> from an adapter that then fails to advertise
        /// makes the arbiter answer "you advertise", and the device neither advertises nor scans.
        /// That is a deadlock rather than a degraded state, and it has happened.
        /// </summary>
        public BleCapability LocalCapability { get; init; } = BleCapability.Central;

        /// <summary>What each peer announced it can do. Absent means assume both halves.</summary>
        public IReadOnlyDictionary<string, BleCapability> PeerCapabilities { get; init; } =
            new Dictionary<string, BleCapability>();

        /// <summary>Peers with a send in flight that needs a socket.</summary>
        public IReadOnlySet<string> WiFiHolds { get; init; } = new HashSet<string>();

        /// <summary>Peers that asked for Wi&#8209;Fi over the radio, and until when.</summary>
        public IReadOnlyDictionary<string, DateTime> WiFiWakeUntilUtc { get; init; } =
            new Dictionary<string, DateTime>();

        /// <summary>
        /// Peers currently reachable over something that carries presence.
        ///
        /// <para><b>Per peer, and that is the whole point.</b> This used to be one boolean for the
        /// device, so a radio link to the laptop made the phone conclude Wi&#8209;Fi was
        /// unnecessary and drop its socket to the desktop as well - a device the radio link could
        /// not reach and never claimed to.</para>
        /// </summary>
        public IReadOnlySet<string> PeersWithPresence { get; init; } = new HashSet<string>();

        /// <summary>A human is standing at this device inviting something in.</summary>
        public bool PairingOpen { get; init; }
    }

    /// <summary>What the supervisor should make true.</summary>
    public sealed record RoutePlan
    {
        /// <summary>Every route that should exist right now.</summary>
        public required IReadOnlySet<RouteKey> Routes { get; init; }

        /// <summary>
        /// Peers this device owes an outbound radio link to and has not got one for.
        ///
        /// <para>Handed straight to the radio scheduler, which scans exactly while this is
        /// non-empty. <b>The old condition was "is any link up".</b> All three heads stopped
        /// looking for peers the moment one Bluetooth link existed, so the second and third device
        /// in a mesh were never reached over the radio at all - not because a rule said so, but
        /// because the loop asked about the app instead of about the peers.</para>
        /// </summary>
        public required IReadOnlySet<string> BleCentralPeers { get; init; }

        /// <summary>
        /// Whether to publish the GATT service.
        ///
        /// <para><b>Advertising is never gated on having something to talk to.</b> A peer that
        /// cannot advertise depends on this device staying findable, so the only condition is the
        /// transport preference and a radio that can do it.</para>
        /// </summary>
        public required bool ShouldAdvertise { get; init; }
    }

    /// <summary>
    /// Which routes this device wants, given what it knows.
    ///
    /// <para><b>Why it is a pure static function.</b> These rules were previously spread across
    /// five loops in three heads - <c>WiFiWanted()</c>, <c>ShouldDialAnyPeerOverBluetooth()</c>,
    /// <c>ShouldDialOverBluetooth()</c>, two dial loops - each holding its own slice of state and
    /// signalling the others through semaphores. Not one of them could be asserted on without a
    /// radio in the room. As one expression over its arguments the whole connection policy becomes
    /// a table of test cases.</para>
    /// </summary>
    public static class RoutePolicy
    {
        public static RoutePlan Plan(IEnumerable<PeerRecord> peers, LocalConditions local, DateTime utcNow)
        {
            var routes = new HashSet<RouteKey>();
            var bleCentral = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var settings = local.Transport;
            bool allowsWiFi = settings != TransportPreference.Ble;
            bool allowsBle = settings != TransportPreference.WiFi;
            bool canAdvertise = allowsBle && local.LocalCapability.HasFlag(BleCapability.Peripheral);

            foreach (var peer in peers ?? Enumerable.Empty<PeerRecord>())
            {
                string fingerprint = peer.Fingerprint;
                if (string.IsNullOrWhiteSpace(fingerprint)) continue;

                if (allowsWiFi && WiFiWantedFor(fingerprint, local, utcNow))
                {
                    routes.Add(new RouteKey(fingerprint, RouteKind.WiFi));
                }

                if (!allowsBle) continue;

                var peerCapability = CapabilityOf(fingerprint, local);
                var role = BleLinkArbiter.KeepFor(local.LocalFingerprint, local.LocalCapability,
                                                  fingerprint, peerCapability);

                switch (role)
                {
                    case BleRole.Central:
                        routes.Add(new RouteKey(fingerprint, RouteKind.BleCentral));
                        bleCentral.Add(fingerprint);
                        break;

                    case BleRole.Peripheral:
                        // Nothing to open: the peer connects to us. The route is still wanted, so
                        // one that arrives is adopted rather than treated as a stranger.
                        routes.Add(new RouteKey(fingerprint, RouteKind.BlePeripheral));
                        break;
                }
            }

            return new RoutePlan
            {
                Routes = routes,
                BleCentralPeers = bleCentral,
                ShouldAdvertise = canAdvertise,
            };
        }

        /// <summary>
        /// Whether a socket to one peer is wanted.
        ///
        /// <para>The last clause is the load-bearing one and it is now asked per peer: a peer that
        /// nothing is carrying presence for needs Wi&#8209;Fi, whatever the radio is doing for
        /// somebody else. Without it, losing Bluetooth leaves a device with no link at all, and
        /// inverting the tiers would have been a regression rather than an improvement.</para>
        /// </summary>
        public static bool WiFiWantedFor(string fingerprint, LocalConditions local, DateTime utcNow)
        {
            if (!local.HasUsableNetwork) return false;

            if (local.ScreenOn) return true;
            if (local.WiFiHolds.Contains(fingerprint)) return true;

            if (local.WiFiWakeUntilUtc.TryGetValue(fingerprint, out var until) && utcNow < until) return true;

            return !local.PeersWithPresence.Contains(fingerprint);
        }

        /// <summary>
        /// Whether this device should be scanning at all.
        ///
        /// <para>True while some peer is owed an outbound radio link, or while a human is inviting
        /// something in and there is nothing paired yet to arbitrate a role with. That second case
        /// is not hypothetical: on an adapter that cannot advertise, an empty peer list otherwise
        /// leaves the device neither scanning nor advertising, which is the same deadlock reached
        /// from the other direction.</para>
        /// </summary>
        public static bool ShouldScan(RoutePlan plan, IEnumerable<PeerRecord> peers, LocalConditions local)
        {
            if (local.Transport == TransportPreference.WiFi) return false;
            if (!local.LocalCapability.HasFlag(BleCapability.Central)) return false;

            if (plan.BleCentralPeers.Count > 0) return true;

            bool anyPeers = peers?.Any() == true;
            return !anyPeers && local.PairingOpen;
        }

        private static BleCapability CapabilityOf(string fingerprint, LocalConditions local)
        {
            // Absent means both halves, which is the optimistic reading and the safe one: a peer
            // that can only ever be a central will simply never be found by this scan and will
            // find us instead, because the service stays advertised either way. Guessing wrong
            // therefore costs a scan; not scanning would leave two laptops waiting for each other.
            return local.PeerCapabilities.TryGetValue(fingerprint, out var capability)
                ? capability
                : BleCapability.Both;
        }
    }
}
