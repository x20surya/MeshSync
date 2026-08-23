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
    /// One TCP socket to one peer, wearing the route interface.
    ///
    /// <para>The transport underneath is unchanged - the framing, the hello, the heartbeat and the
    /// key agreement are all where they were. What moves is the accounting: the peer table, the
    /// collision rule and the retry policy belonged to <see cref="MeshLinks"/> and now belong to
    /// <see cref="PeerLink"/>, so the socket tier and the radio tier are governed by the same
    /// object rather than by two that disagreed.</para>
    /// </summary>
    public sealed class WiFiRoute : IPeerRoute
    {
        private readonly TcpTransportConnection _connection;
        private readonly ILinkClock _clock;
        private readonly object _gate = new();

        private RouteState _state;
        private string _fingerprint = "";
        private string? _lastFailure;
        private int _closed;

        internal WiFiRoute(TcpTransportConnection connection, ILinkClock clock, bool outbound)
        {
            _connection = connection;
            _clock = clock;
            IsOutbound = outbound;
            _state = RouteState.Connecting;
            StateSinceUtc = clock.UtcNow;

            _connection.PeerIdentified += OnPeerIdentified;
            _connection.PayloadReceived += OnPayload;
            _connection.ConnectionClosed += OnClosed;
        }

        public RouteKind Kind => RouteKind.WiFi;

        public string PeerFingerprint { get { lock (_gate) return _fingerprint; } }

        public RouteState State { get { lock (_gate) return _state; } }

        public DateTime StateSinceUtc { get; private set; }

        public PeerSession? Session => _connection.Peer;

        public string? LastFailure { get { lock (_gate) return _lastFailure; } }

        public DateTime RetryAtUtc { get; private set; }

        public bool IsOutbound { get; }

        public int MaxPayloadBytes => TcpTransportConnection.MaxPayloadBytes;

        /// <summary>
        /// A socket says nothing about proximity.
        ///
        /// <para>It is raised on demand and dropped again, and a peer reachable over a VPN or a
        /// bridged network is not in the room. Only the radio answers "is this device near me",
        /// which is why presence is a property of a route rather than of a peer.</para>
        /// </summary>
        public bool CarriesPresence => false;

        /// <summary>The peer's name, once its hello has crossed.</summary>
        public string? RemoteDeviceName => _connection.RemoteDeviceName;

        public event Action<IPeerRoute, RouteState, RouteState>? StateChanged;
        public event Action<IPeerRoute, RoutePayload>? PayloadReceived;

        /// <summary>Raised with the peer's hello, so the registry can note where it was reached.</summary>
        public event Action<WiFiRoute, PeerIdentifiedEventArgs>? Identified;

        internal void Adopt(TcpClient client, CancellationToken cancellationToken)
        {
            _connection.Adopt(client, cancellationToken);
            Move(RouteState.Handshaking);
        }

        internal async Task DialAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(timeout);

                Move(RouteState.Connecting);
                await _connection.ConnectAsync(host, bounded.Token).ConfigureAwait(false);
                Move(RouteState.Handshaking);
            }
            catch (Exception ex)
            {
                // A connect timeout raises OperationCanceledException, and treating that as "the
                // caller cancelled us" is what once meant a phone with Wi-Fi off never fell back.
                bool timedOut = ex is OperationCanceledException or TimeoutException;
                Fail(timedOut ? $"connecting to {host} timed out" : $"connecting to {host} failed: {ex.Message}");
                throw;
            }
        }

        private void OnPeerIdentified(object? sender, PeerIdentifiedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Fingerprint)) return;

            lock (_gate) _fingerprint = e.Fingerprint;

            try { Identified?.Invoke(this, e); }
            catch (Exception ex) { Log.Write("Fabric", "An Identified handler threw", ex); }

            // Identified is not the same as usable: with an ephemeral agreement the key is not
            // complete until both hellos have crossed, and IsConnected is what knows that.
            if (_connection.IsConnected) Move(RouteState.Established);
        }

        private void OnPayload(object? sender, PayloadReceivedEventArgs e)
        {
            var session = _connection.Peer;
            if (session == null)
            {
                Log.Write("Fabric", "Dropped a payload that arrived before a key was agreed.");
                return;
            }

            if (!session.TryDecrypt(e.EncryptedPayload, out var decrypted))
            {
                Log.Write("Fabric",
                    $"Dropped a payload from {DeviceIdentity.Shorten(session.Fingerprint)}: it does not authenticate under this session's key.");
                return;
            }

            try
            {
                PayloadReceived?.Invoke(this, new RoutePayload
                {
                    Peer = decrypted.Peer,
                    ContentType = decrypted.ContentType,
                    Body = decrypted.Body,
                    Via = RouteKind.WiFi,
                });
            }
            catch (Exception ex)
            {
                // A handler that throws must never take the link down with it.
                Log.Write("Fabric", "Payload handling failed", ex);
            }
        }

        private void OnClosed(object? sender, EventArgs e) => Fail(_lastFailure ?? "the socket closed");

        public async Task<bool> SendAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
        {
            if (State != RouteState.Established) return false;

            var session = _connection.Peer;
            if (session == null) return false;

            byte[]? payload = session.Encrypt(contentType, body);
            if (payload == null) return false;

            if (payload.Length > MaxPayloadBytes)
            {
                Log.Write("Fabric",
                    $"Refusing to send {payload.Length} bytes to {DeviceIdentity.Shorten(session.Fingerprint)} (over the limit).");
                return false;
            }

            try
            {
                await _connection.SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Fabric", $"Sending to {DeviceIdentity.Shorten(session.Fingerprint)} failed", ex);
                return false;
            }
        }

        public Task CloseAsync(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return Task.CompletedTask;

            lock (_gate) _lastFailure ??= reason;
            Move(RouteState.Draining);

            try { _connection.Dispose(); }
            catch (Exception ex) { Log.Write("Fabric", "Disposing a socket route failed", ex); }

            Move(RouteState.Idle);
            return Task.CompletedTask;
        }

        private void Fail(string reason)
        {
            lock (_gate) _lastFailure = reason;
            Move(RouteState.Backoff);
        }

        private void Move(RouteState to)
        {
            RouteState from;
            lock (_gate)
            {
                from = _state;
                if (from == to) return;

                // Idle is terminal for a route: a disposed socket does not come back, and a
                // resurrection would leave the owning PeerLink holding something dead.
                if (from == RouteState.Idle) return;

                _state = to;
                StateSinceUtc = _clock.UtcNow;
            }

            try { StateChanged?.Invoke(this, from, to); }
            catch (Exception ex) { Log.Write("Fabric", "A StateChanged handler threw", ex); }
        }

        public ValueTask DisposeAsync()
        {
            _connection.PeerIdentified -= OnPeerIdentified;
            _connection.PayloadReceived -= OnPayload;
            _connection.ConnectionClosed -= OnClosed;

            try { _connection.Dispose(); } catch { }

            StateChanged = null;
            PayloadReceived = null;
            Identified = null;

            return ValueTask.CompletedTask;
        }
    }
}
