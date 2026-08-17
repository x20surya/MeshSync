using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;
using CoreLib.Identity;

namespace CoreLib.Transport
{
    /// <summary>A payload that arrived, and the device it came from.</summary>
    public sealed class MeshPayloadEventArgs : EventArgs
    {
        public PeerRecord Peer { get; init; } = null!;
        public byte ContentType { get; init; }
        public byte[] Body { get; init; } = Array.Empty<byte>();
        public string Via { get; init; } = "Wi-Fi";
    }

    /// <summary>
    /// The Wi-Fi links to every paired device, and the rules for how they come and go.
    ///
    /// <para><b>What it replaces.</b> One <see cref="TcpTransportConnection"/> holding a single
    /// session, on a device hardcoded as either the server or the client. A second peer
    /// connecting evicted the first, because there was one session field to put it in, and
    /// phone-to-phone or laptop-to-laptop was impossible because the roles were fixed by
    /// platform rather than negotiated.</para>
    ///
    /// <para><b>Symmetry.</b> Every device both listens and dials. Which one ends up accepting
    /// a given link is decided per connection, so nothing here is a server. When two devices
    /// dial each other at the same moment they collide, and both sides settle it identically by
    /// comparing fingerprints - no negotiation round trip, because both already know the
    /// answer.</para>
    ///
    /// <para><b>No relaying.</b> Every device talks to every other directly, so there is no
    /// routing and no loops to prevent. The trade is that it assumes a complete graph: two
    /// devices that cannot reach each other simply do not sync, rather than being bridged by a
    /// third.</para>
    /// </summary>
    public sealed class MeshLinks : IDisposable
    {
        private readonly PeerSecurity _security;
        private readonly TcpAcceptor _acceptor;
        private readonly int _port;

        private readonly object _gate = new();
        private readonly Dictionary<string, TcpTransportConnection> _links = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TcpTransportConnection, TaskCompletionSource<string>> _pending = new();

        private bool _disposed;

        /// <summary>Name announced to peers, for their device lists.</summary>
        public string LocalDeviceName { get; set; } = Environment.MachineName;

        /// <summary>A decrypted payload from a known device.</summary>
        public event EventHandler<MeshPayloadEventArgs>? PayloadReceived;

        /// <summary>A device's link became usable.</summary>
        public event Action<PeerRecord>? PeerConnected;

        /// <summary>A device's link went away. Carries the fingerprint, since the record may be gone.</summary>
        public event Action<string>? PeerDisconnected;

        public MeshLinks(PeerSecurity security, int port = TcpTransportConnection.DefaultPort)
        {
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _port = port;
            _acceptor = new TcpAcceptor(port);
            _acceptor.Accepted += OnAccepted;
        }

        /// <summary>Fingerprints of every device with a live, identified link.</summary>
        public IReadOnlyList<string> ConnectedPeers
        {
            get { lock (_gate) return _links.Where(p => p.Value.IsConnected).Select(p => p.Key).ToList(); }
        }

        public int ConnectedCount
        {
            get { lock (_gate) return _links.Count(p => p.Value.IsConnected); }
        }

        public bool IsConnectedToAny => ConnectedCount > 0;

        public bool IsConnectedTo(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return false;
            lock (_gate) return _links.TryGetValue(fingerprint, out var link) && link.IsConnected;
        }

        /// <summary>The friendly name a connected peer announced, if it has.</summary>
        public string? NameOf(string fingerprint)
        {
            lock (_gate) return _links.TryGetValue(fingerprint, out var link) ? link.RemoteDeviceName : null;
        }

        public Task StartListeningAsync(CancellationToken cancellationToken = default) =>
            _acceptor.StartAsync(cancellationToken);

        public void StopListening() => _acceptor.Stop();

        /// <summary>
        /// Drops every link without stopping the listener.
        ///
        /// This is what Bluetooth standby does when the screen goes off: Wi-Fi is put away
        /// while Bluetooth keeps holding presence. Listening continues, because a peer may
        /// still have something worth sending and dialling in costs this side nothing.
        /// </summary>
        public void DisconnectAll()
        {
            List<KeyValuePair<string, TcpTransportConnection>> links;

            lock (_gate)
            {
                links = _links.ToList();
                _links.Clear();
            }

            foreach (var pair in links)
            {
                Retire(pair.Value);

                try { PeerDisconnected?.Invoke(pair.Key); }
                catch (Exception ex) { Log.Write("Mesh", "PeerDisconnected handler threw", ex); }
            }
        }

        // ──────────────────────────────── dialling

        /// <summary>
        /// Opens a link to one device, if it is not already up.
        ///
        /// Returns only once the peer has introduced itself and been authorised, because a
        /// socket that has connected proves nothing: the address may now belong to something
        /// else entirely, which is exactly what a DHCP lease change does.
        /// </summary>
        public async Task<bool> ConnectToAsync(PeerRecord peer, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (IsConnectedTo(peer.Fingerprint)) return true;
            if (string.IsNullOrWhiteSpace(peer.LastAddress)) return false;

            var (host, port) = SplitAddress(peer.LastAddress!);
            var link = NewLink(port);
            var identified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate) _pending[link] = identified;

            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(timeout);

                await link.ConnectAsync(host, connectTimeout.Token).ConfigureAwait(false);

                // Identification is the real success condition. Waiting for it here is what
                // stops the caller treating "something answered on that address" as "the device
                // I meant is there".
                string fingerprint = await identified.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(fingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    // Reached a paired device, just not the one dialled - so the link is kept
                    // and the caller told its peer is still missing.
                    Log.Write("Mesh",
                        $"{peer.LastAddress} answered as {DeviceIdentity.Shorten(fingerprint)}, not {DeviceIdentity.Shorten(peer.Fingerprint)}.");
                    return false;
                }

                return IsConnectedTo(peer.Fingerprint);
            }
            catch (Exception ex)
            {
                lock (_gate) _pending.Remove(link);

                // Logs the host actually dialled, not the stored value. They differ whenever a
                // registry written by an earlier build still holds an IPv4-mapped address, and
                // reporting the stored form sent me looking for a bug in the wrong place.
                bool timedOut = ex is OperationCanceledException or TimeoutException;
                Log.Write("Mesh", timedOut
                    ? $"Connecting to {host}:{port} timed out after {timeout.TotalSeconds:F0}s."
                    : $"Connecting to {host}:{port} failed: {ex.GetType().Name}: {ex.Message}");

                Retire(link);
                return false;
            }
        }

        /// <summary>Opens a link to every paired device that is not already connected.</summary>
        public async Task<int> ConnectToAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var targets = _security.Peers.Peers
                .Where(p => !IsConnectedTo(p.Fingerprint) && !string.IsNullOrWhiteSpace(p.LastAddress))
                .ToList();

            if (targets.Count == 0) return 0;

            // Dialled together rather than in turn: one unreachable device would otherwise
            // make every peer behind it wait out its whole timeout.
            var attempts = targets.Select(p => ConnectToAsync(p, timeout, cancellationToken));
            var results = await Task.WhenAll(attempts).ConfigureAwait(false);

            return results.Count(ok => ok);
        }

        // ──────────────────────────────── sending

        /// <summary>
        /// Sends to every connected device, encrypting separately for each.
        ///
        /// There is no such thing as one ciphertext for the whole mesh: the key is per pair, so
        /// a fan-out is genuinely N encryptions. That is the cost of a paired device being
        /// unable to read traffic meant for another pair.
        /// </summary>
        public async Task<int> BroadcastAsync(byte contentType, byte[] body, CancellationToken cancellationToken = default)
        {
            List<KeyValuePair<string, TcpTransportConnection>> targets;
            lock (_gate) targets = _links.Where(p => p.Value.IsConnected).ToList();

            if (targets.Count == 0) return 0;

            var sends = targets.Select(target => SendOneAsync(target.Key, target.Value, contentType, body, cancellationToken));
            var results = await Task.WhenAll(sends).ConfigureAwait(false);

            return results.Count(ok => ok);
        }

        public Task<bool> SendToAsync(string fingerprint, byte contentType, byte[] body, CancellationToken cancellationToken = default)
        {
            TcpTransportConnection? link;
            lock (_gate) _links.TryGetValue(fingerprint, out link);

            if (link?.IsConnected != true) return Task.FromResult(false);

            return SendOneAsync(fingerprint, link, contentType, body, cancellationToken);
        }

        private async Task<bool> SendOneAsync(string fingerprint, TcpTransportConnection link,
                                              byte contentType, byte[] body, CancellationToken cancellationToken)
        {
            // Sealed with the key this connection agreed, not one derived from the peer's
            // identity. A link that has not finished its handshake has no key and is skipped.
            var session = link.Peer;
            if (session == null) return false;

            byte[]? payload = session.Encrypt(contentType, body);
            if (payload == null) return false;

            if (payload.Length > TcpTransportConnection.MaxPayloadBytes)
            {
                Log.Write("Mesh", $"Refusing to send {payload.Length} bytes to {DeviceIdentity.Shorten(fingerprint)} (over the limit).");
                return false;
            }

            try
            {
                await link.SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                // One unreachable device must not stop the rest of the fan-out.
                Log.Write("Mesh", $"Sending to {DeviceIdentity.Shorten(fingerprint)} failed", ex);
                return false;
            }
        }

        // ──────────────────────────────── link lifecycle

        /// <summary>
        /// Splits a stored address into a host and the port to dial.
        ///
        /// Almost always a bare address, because every device listens on the same port and a
        /// peer's inbound socket has an ephemeral source port that would be useless to record.
        /// An explicit <c>host:port</c> is honoured for the case where a device is not on the
        /// default - and for tests, which run several devices on one machine.
        /// </summary>
        private (string Host, int Port) SplitAddress(string address)
        {
            if (IPEndPoint.TryParse(address, out var endpoint) && endpoint.Port != 0)
            {
                return (Unwrap(endpoint.Address), endpoint.Port);
            }

            // Unwrapped here too, not only where addresses are recorded: a registry written by
            // an earlier build still holds the mapped form, and it would otherwise keep timing
            // out until the peer happened to announce itself again.
            if (IPAddress.TryParse(address, out var parsed)) return (Unwrap(parsed), _port);

            return (address, _port);
        }

        private static string Unwrap(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

        private TcpTransportConnection NewLink(int? port = null)
        {
            var link = new TcpTransportConnection(port ?? _port)
            {
                LocalDeviceName = LocalDeviceName,
                LocalPublicKey = _security.Identity.PublicKey,
                LocalMeshName = _security.Peers.MeshName,

                // Authorising and agreeing a key are one step: a peer this device has not
                // paired with never reaches the point of having a session to encrypt with.
                OpenSession = (peerKey, peerEphemeral, localEphemeral) =>
                    _security.Authorise(peerKey)
                        ? _security.OpenSession(peerKey, localEphemeral, peerEphemeral)
                        : null
            };

            link.PayloadReceived += OnPayload;
            link.PeerIdentified += OnIdentified;
            link.ConnectionClosed += OnClosed;

            return link;
        }

        private void OnAccepted(TcpClient client)
        {
            var link = NewLink();

            // Registered as pending before adopting, so the hello cannot arrive and be
            // processed before there is anywhere to record it.
            lock (_gate) _pending[link] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                link.Adopt(client);
            }
            catch (Exception ex)
            {
                Log.Write("Mesh", "Adopting an accepted connection failed", ex);
                lock (_gate) _pending.Remove(link);
                Retire(link);
                try { client.Dispose(); } catch { }
            }
        }

        private void OnIdentified(object? sender, PeerIdentifiedEventArgs e)
        {
            if (sender is not TcpTransportConnection link) return;
            if (string.IsNullOrWhiteSpace(e.Fingerprint)) return;

            _security.Peers.NoteSeen(e.Fingerprint, e.Address, e.DeviceName);

            // Adopted only when this device has no name of its own, which is what stops two
            // devices that disagree from overwriting each other on every reconnect.
            _security.Peers.AdoptMeshName(e.MeshName);

            TcpTransportConnection? loser = null;
            TaskCompletionSource<string>? waiter;

            lock (_gate)
            {
                _pending.Remove(link, out waiter);

                if (_links.TryGetValue(e.Fingerprint, out var existing) && !ReferenceEquals(existing, link))
                {
                    if (existing.IsConnected)
                    {
                        var winner = ResolveCollision(existing, link, e.Fingerprint);
                        loser = ReferenceEquals(winner, existing) ? link : existing;
                        _links[e.Fingerprint] = winner;
                    }
                    else
                    {
                        loser = existing;
                        _links[e.Fingerprint] = link;
                    }
                }
                else
                {
                    _links[e.Fingerprint] = link;
                }
            }

            if (loser != null)
            {
                Log.Write("Mesh",
                    $"Two links to {DeviceIdentity.Shorten(e.Fingerprint)}; keeping the {(ReferenceEquals(loser, link) ? "existing" : "new")} one.");
                Retire(loser);
            }

            waiter?.TrySetResult(e.Fingerprint);

            var peer = _security.Peers.Find(e.Fingerprint);
            if (peer != null)
            {
                try { PeerConnected?.Invoke(peer); }
                catch (Exception ex) { Log.Write("Mesh", "PeerConnected handler threw", ex); }
            }
        }

        /// <summary>
        /// Decides which of two simultaneous links survives.
        ///
        /// Both devices listen and both dial, so each can open a socket to the other at the
        /// same moment and end up with two. The rule is that the link dialled by the device
        /// with the lower fingerprint is the one that lives. Both sides compute it from values
        /// they already exchanged, so they converge without a round trip and without either
        /// having to be in charge.
        /// </summary>
        private TcpTransportConnection ResolveCollision(TcpTransportConnection existing,
                                                        TcpTransportConnection incoming,
                                                        string peerFingerprint)
        {
            bool weShouldDial = string.CompareOrdinal(_security.Identity.Fingerprint, peerFingerprint) < 0;

            // If we are the dialler, the surviving link is the one we opened - which is the
            // outbound one. Otherwise it is the one the peer opened, which arrived inbound.
            bool keepInbound = !weShouldDial;

            if (existing.IsInbound == keepInbound && incoming.IsInbound != keepInbound) return existing;
            if (incoming.IsInbound == keepInbound && existing.IsInbound != keepInbound) return incoming;

            // Both the same direction, which means one is a stale link the peer has already
            // abandoned. The newer is the one to believe.
            return incoming;
        }

        private void OnPayload(object? sender, PayloadReceivedEventArgs e)
        {
            try
            {
                // There is exactly one key this could have been sealed with - the one this
                // connection agreed - so there is nothing to search. Failing to open it means
                // the payload did not come from the device on the other end of this socket.
                var session = (sender as TcpTransportConnection)?.Peer;
                if (session == null)
                {
                    Log.Write("Mesh", "Dropped a payload that arrived before a key was agreed.");
                    return;
                }

                if (!session.TryDecrypt(e.EncryptedPayload, out var decrypted))
                {
                    Log.Write("Mesh",
                        $"Dropped a payload from {DeviceIdentity.Shorten(session.Fingerprint)}: it does not authenticate under this session's key.");
                    return;
                }

                PayloadReceived?.Invoke(this, new MeshPayloadEventArgs
                {
                    Peer = decrypted.Peer,
                    ContentType = decrypted.ContentType,
                    Body = decrypted.Body
                });
            }
            catch (Exception ex)
            {
                // A handler that throws must never take the link down with it.
                Log.Write("Mesh", "Payload handling failed", ex);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (sender is not TcpTransportConnection link) return;

            string? fingerprint = null;

            lock (_gate)
            {
                _pending.Remove(link, out var waiter);
                waiter?.TrySetCanceled();

                foreach (var pair in _links)
                {
                    if (!ReferenceEquals(pair.Value, link)) continue;
                    fingerprint = pair.Key;
                    break;
                }

                if (fingerprint != null) _links.Remove(fingerprint);
            }

            if (fingerprint == null) return;

            Log.Write("Mesh", $"Link to {DeviceIdentity.Shorten(fingerprint)} closed.");

            try { PeerDisconnected?.Invoke(fingerprint); }
            catch (Exception ex) { Log.Write("Mesh", "PeerDisconnected handler threw", ex); }
        }

        private void Retire(TcpTransportConnection link)
        {
            link.PayloadReceived -= OnPayload;
            link.PeerIdentified -= OnIdentified;
            link.ConnectionClosed -= OnClosed;

            try { link.Dispose(); }
            catch (Exception ex) { Log.Write("Mesh", "Disposing a link failed", ex); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _acceptor.Accepted -= OnAccepted;
            _acceptor.Dispose();

            List<TcpTransportConnection> links;
            lock (_gate)
            {
                links = _links.Values.Concat(_pending.Keys).ToList();
                _links.Clear();
                _pending.Clear();
            }

            foreach (var link in links) Retire(link);

            PayloadReceived = null;
            PeerConnected = null;
            PeerDisconnected = null;
        }
    }
}
