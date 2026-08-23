using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;
using CoreLib.Identity;

namespace CoreLib.Transport.Fabric
{
    /// <summary>
    /// Listens for sockets and dials them, and hands both to the fabric as ordinary routes.
    ///
    /// <para><b>Both directions arrive the same way.</b> Every head had a dial loop and an accept
    /// path that shared no code and disagreed about when a link counted. Here a dialled socket and
    /// an accepted one are the same type in the same state machine, and which end opened it is one
    /// boolean used by exactly one rule - the collision settlement.</para>
    /// </summary>
    public sealed class WiFiRouteProvider : IRouteProvider
    {
        private readonly PeerSecurity _security;
        private readonly TcpAcceptor _acceptor;
        private readonly ILinkClock _clock;
        private readonly int _port;
        private readonly int _peerPort;

        private bool _disposed;

        /// <param name="port">The port this device listens on.</param>
        /// <param name="peerPort">
        /// The port to dial when a stored address carries none. Defaults to
        /// <paramref name="port"/>, which is right for every device in the field because they all
        /// listen on 45001 - but not for two devices sharing one machine, where each listens
        /// somewhere different and would otherwise dial a bare address on its own port and reach
        /// itself. That is not hypothetical: it logged
        /// <c>Refusing a connection from this device's own identity</c> in a loop.
        /// </param>
        public WiFiRouteProvider(PeerSecurity security, int port = TcpTransportConnection.DefaultPort,
                                 int? peerPort = null, ILinkClock? clock = null)
        {
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _clock = clock ?? SystemClock.Instance;
            _port = port;
            _peerPort = peerPort ?? port;

            _acceptor = new TcpAcceptor(port);
            _acceptor.Accepted += OnAccepted;
        }

        public RouteKind Kind => RouteKind.WiFi;

        /// <summary>
        /// Set by the head from the transport preference and whether a LAN-capable network exists.
        ///
        /// Mobile data counts as "a network" and can never route to a private address, so asking
        /// for a Wi-Fi or Ethernet transport specifically is the difference between failing fast
        /// and spending the connect timeout proving it.
        /// </summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>Name announced to peers, for their device lists.</summary>
        public string LocalDeviceName { get; set; } = Environment.MachineName;

        /// <summary>How long a dial may take. <c>TcpClient.ConnectAsync</c> has no default at all.</summary>
        public TimeSpan DialTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// What this device's radio can do, announced in the socket hello.
        ///
        /// A function rather than a value because it is only known once the peripheral half has
        /// tried to start, which happens after the listener is already up.
        /// </summary>
        public Func<BleCapability> LocalCapability { get; set; } = () => BleCapability.Both;

        public bool IsListening => _acceptor.IsListening;

        public int Port => _acceptor.Port;

        public event Action<IPeerRoute>? RouteArrived;

        public Task StartListeningAsync(CancellationToken cancellationToken = default) =>
            _acceptor.StartAsync(cancellationToken);

        public void StopListening() => _acceptor.Stop();

        public IPeerRoute? Open(PeerRecord peer)
        {
            if (_disposed || !IsAvailable) return null;
            if (string.IsNullOrWhiteSpace(peer?.LastAddress)) return null;

            var (host, port) = SplitAddress(peer!.LastAddress!);
            var route = NewRoute(port, outbound: true);

            // Dialled in the background: the route reports its own progress, so a caller never
            // blocks on a peer that is not there. A failure lands as Backoff, which is exactly
            // what the supervisor is watching for.
            _ = Task.Run(async () =>
            {
                try { await route.DialAsync(host, DialTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Logs the host actually dialled, not the stored value. They differ whenever a
                    // registry written by an earlier build still holds an IPv4-mapped address, and
                    // reporting the stored form sends you looking in the wrong place.
                    Log.Write("Fabric",
                        $"Dialling {DeviceIdentity.Shorten(peer.Fingerprint)} at {host}:{port} failed: {ex.GetType().Name}");
                }
            });

            return route;
        }

        private void OnAccepted(TcpClient client)
        {
            if (_disposed) { try { client.Dispose(); } catch { } return; }

            var route = NewRoute(_port, outbound: false);

            try
            {
                route.Adopt(client, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Write("Fabric", "Adopting an accepted connection failed", ex);
                _ = route.DisposeAsync();
                try { client.Dispose(); } catch { }
                return;
            }

            // Handed over with no fingerprint yet. The fabric holds it under the handshake
            // deadline until its hello crosses, which is the window an unidentified link used to
            // live in forever.
            try { RouteArrived?.Invoke(route); }
            catch (Exception ex) { Log.Write("Fabric", "A RouteArrived handler threw", ex); }
        }

        private WiFiRoute NewRoute(int port, bool outbound)
        {
            var connection = new TcpTransportConnection(port)
            {
                LocalDeviceName = LocalDeviceName,
                LocalPublicKey = _security.Identity.PublicKey,
                LocalMeshName = _security.Peers.MeshName,
                LocalCapability = LocalCapability(),

                // Authorising and agreeing a key are one step: a peer this device has not paired
                // with never reaches the point of having a session to encrypt with.
                OpenSession = (peerKey, peerName, peerEphemeral, localEphemeral) =>
                    _security.Authorise(peerKey, peerName)
                        ? _security.OpenSession(peerKey, localEphemeral, peerEphemeral)
                        : null,
            };

            var route = new WiFiRoute(connection, _clock, outbound);
            route.Identified += OnIdentified;
            return route;
        }

        private void OnIdentified(WiFiRoute route, PeerIdentifiedEventArgs e)
        {
            _security.Peers.NoteSeen(e.Fingerprint, e.Address, e.DeviceName, e.Capability);

            // Adopted only by a device that has none of its own, which is what stops two devices
            // that disagree overwriting each other on every reconnect.
            _security.Peers.AdoptMeshName(e.MeshName);
        }

        /// <summary>
        /// Splits a stored address into a host and the port to dial.
        ///
        /// Almost always a bare address, because every device listens on the same port and a peer's
        /// inbound socket has an ephemeral source port that would be useless to record.
        /// </summary>
        internal (string Host, int Port) SplitAddress(string address)
        {
            if (IPEndPoint.TryParse(address, out var endpoint) && endpoint.Port != 0)
            {
                return (Unwrap(endpoint.Address), endpoint.Port);
            }

            // Unwrapped here too, not only where addresses are recorded: a registry written by an
            // earlier build still holds the IPv4-mapped form, which parses as an address, reads
            // perfectly well in a log, and can never be dialled back.
            if (IPAddress.TryParse(address, out var parsed)) return (Unwrap(parsed), _peerPort);

            return (address, _peerPort);
        }

        private static string Unwrap(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;

            _acceptor.Accepted -= OnAccepted;
            _acceptor.Dispose();
            RouteArrived = null;

            return ValueTask.CompletedTask;
        }
    }
}
