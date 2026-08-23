using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CoreLib.Identity;

namespace CoreLib.Transport.Fabric
{
    /// <summary>One route, as the health surface sees it.</summary>
    public sealed record RouteHealth
    {
        public required RouteKind Kind { get; init; }
        public required RouteState State { get; init; }
        public required TimeSpan Since { get; init; }

        /// <summary>Why it is not connected, or what it is waiting for. Never null in the output.</summary>
        public string Detail { get; init; } = "";
    }

    /// <summary>One peer and every way this device has of reaching it.</summary>
    public sealed record PeerHealth
    {
        public required string Fingerprint { get; init; }
        public required string Name { get; init; }
        public required IReadOnlyList<RouteHealth> Routes { get; init; }

        public bool IsConnected => Routes.Any(r => r.State == RouteState.Established);
    }

    /// <summary>
    /// Everything about reachability, in one shape that can be printed or serialised.
    ///
    /// <para><b>Why this is worth its own type.</b> Diagnosing the connection layer meant reading
    /// three log files on three devices, one of them through <c>adb logcat</c>, and inferring state
    /// that no head could actually report - <c>LinkState</c> answered per app, so Windows could
    /// mark only one device connected and guessed which by comparing names.</para>
    ///
    /// <para>The fabric already holds every field here. This is the projection, and it is the
    /// difference between "Bluetooth is not working" and "two devices seen, neither in this
    /// mesh".</para>
    /// </summary>
    public sealed record MeshHealth
    {
        public required string MeshName { get; init; }
        public required bool HasMeshKey { get; init; }
        public required IReadOnlyList<PeerHealth> Peers { get; init; }

        /// <summary>Routes connected but not yet owned by a peer, still inside the handshake grace.</summary>
        public int Handshaking { get; init; }

        public string RadioStatus { get; init; } = "";
        public int RadioLinks { get; init; }
        public int RadioBudget { get; init; }
        public bool Advertising { get; init; }
        public (int Seen, int Ours) LastScan { get; init; }

        public long SupervisorPasses { get; init; }
        public long SupervisorRestarts { get; init; }
        public TimeSpan SinceLastPass { get; init; }

        /// <summary>
        /// Builds the snapshot from the live fabric.
        ///
        /// A method rather than a property because it walks every route and allocates; a UI on a
        /// redraw timer should ask for it deliberately.
        /// </summary>
        public static MeshHealth Of(MeshFabric fabric, ILinkClock clock, DateTime lastPassUtc,
                                    long passes, long restarts,
                                    string radioStatus = "", int radioLinks = 0, int radioBudget = 0,
                                    bool advertising = false, (int Seen, int Ours) lastScan = default)
        {
            var now = clock.UtcNow;

            var peers = fabric.Links.Select(link => new PeerHealth
            {
                Fingerprint = link.Fingerprint,
                Name = link.Peer.Name ?? DeviceIdentity.Shorten(link.Fingerprint),
                Routes = link.AllRoutes
                    .Select(r => new RouteHealth
                    {
                        Kind = r.Kind,
                        State = r.State,
                        Since = now - r.StateSinceUtc,
                        Detail = Describe(r, now),
                    })

                    // A retired route takes its LastFailure with it, so a kind that failed and is
                    // waiting out a backoff would otherwise vanish from the table entirely - and
                    // "no row at all" is the least useful thing a health surface can say.
                    .Concat(link.Backoffs.Select(kind => new RouteHealth
                    {
                        Kind = kind,
                        State = RouteState.Backoff,
                        Since = TimeSpan.Zero,
                        Detail = Retry(link.FailureOf(kind), link.RetryAt(kind), now),
                    }))
                    .OrderBy(r => r.Kind)
                    .ToList(),
            }).ToList();

            return new MeshHealth
            {
                MeshName = fabric.Security.Peers.MeshNameOrDefault,
                HasMeshKey = fabric.Security.Peers.HasMeshKey,
                Peers = peers,
                Handshaking = fabric.PendingCount,
                RadioStatus = radioStatus,
                RadioLinks = radioLinks,
                RadioBudget = radioBudget,
                Advertising = advertising,
                LastScan = lastScan,
                SupervisorPasses = passes,
                SupervisorRestarts = restarts,
                SinceLastPass = now - lastPassUtc,
            };
        }

        private static string Retry(string? failure, DateTime retryAt, DateTime now) =>
            retryAt > now
                ? $"{failure ?? "failed"} · retry in {(retryAt - now).TotalSeconds:F0}s"
                : failure ?? "failed";

        private static string Describe(IPeerRoute route, DateTime now) => route.State switch
        {
            RouteState.Established => "",
            RouteState.Backoff => Retry(route.LastFailure, route.RetryAtUtc, now),
            RouteState.Handshaking => "connected, no session agreed yet",
            RouteState.Connecting => "connecting",
            RouteState.Discovering => "scanning",
            RouteState.Draining => "closing",
            _ => route.LastFailure ?? "",
        };

        /// <summary>
        /// The same thing as a fixed-width table, for a terminal and for a log line.
        ///
        /// <para>The row that earns this whole type is "2 seen, 0 matched beacon": one glance
        /// instead of six minutes of scans finding a stranger's phone over and over.</para>
        /// </summary>
        public string ToTable()
        {
            var text = new StringBuilder();

            text.Append("MESH   ").Append(MeshName)
                .Append("   ·   beacon ").Append(HasMeshKey ? "on" : "off")
                .Append("   ·   ").Append(Peers.Count).AppendLine(Peers.Count == 1 ? " peer" : " peers");
            text.AppendLine();

            text.AppendLine($"{"PEER",-16}{"ROUTE",-17}{"STATE",-15}{"SINCE",-8}DETAIL");

            if (Peers.Count == 0) text.AppendLine("(nothing paired)");

            foreach (var peer in Peers)
            {
                string label = Trim(peer.Name, 15);

                if (peer.Routes.Count == 0)
                {
                    text.AppendLine($"{label,-16}{"-",-17}{"Idle",-15}{"-",-8}no route wanted");
                    continue;
                }

                bool first = true;
                foreach (var route in peer.Routes)
                {
                    text.AppendLine(
                        $"{(first ? label : ""),-16}{Name(route.Kind),-17}{route.State,-15}{Duration(route.Since),-8}{route.Detail}");
                    first = false;
                }
            }

            text.AppendLine();

            if (Handshaking > 0)
                text.AppendLine($"HANDSHAKING  {Handshaking} link(s) connected and not yet identified");

            if (RadioBudget > 0)
            {
                text.AppendLine($"RADIO  {RadioStatus} · {RadioLinks}/{RadioBudget} links · " +
                                $"{(Advertising ? "advertising" : "not advertising")} · " +
                                $"last round {LastScan.Seen} seen, {LastScan.Ours} ours");
            }

            text.AppendLine($"SUPERVISOR  last pass {Duration(SinceLastPass)} ago · " +
                            $"{SupervisorPasses} passes · {SupervisorRestarts} restarts");

            return text.ToString();
        }

        private static string Name(RouteKind kind) => kind switch
        {
            RouteKind.WiFi => "wifi",
            RouteKind.BleCentral => "ble-central",
            RouteKind.BlePeripheral => "ble-peripheral",
            _ => kind.ToString().ToLowerInvariant(),
        };

        private static string Duration(TimeSpan span) =>
            span < TimeSpan.Zero ? "-" :
            span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:D2}" :
            $"{span.Minutes:D2}:{span.Seconds:D2}";

        private static string Trim(string value, int width) =>
            value.Length <= width ? value : value.Substring(0, width - 1) + "…";
    }
}
