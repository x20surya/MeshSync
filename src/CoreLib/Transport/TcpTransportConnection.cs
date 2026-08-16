using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>
    /// Length-prefixed, framed TCP transport with keepalive and application-level heartbeat.
    ///
    /// Wire format (little endian):
    ///   [magic u16 = 0x4D53 "MS"][version u8][kind u8][length u32][payload ...]
    ///
    /// The magic and version let a desynchronised stream be detected and torn down
    /// immediately instead of silently allocating a garbage-sized buffer, and the
    /// heartbeat surfaces half-open sockets (Wi-Fi sleep, AP roam, NAT idle timeout)
    /// that TCP alone will not report.
    /// </summary>
    public sealed class TcpTransportConnection : ITransportConnection
    {
        public const int DefaultPort = 45001;

        private const ushort Magic = 0x4D53;
        private const byte ProtocolVersion = 1;
        private const int HeaderSize = 8;

        private const byte KindData = 0;
        private const byte KindPing = 1;
        private const byte KindPong = 2;
        private const byte KindHello = 3;

        /// <summary>Guards against a hostile peer sending a huge name.</summary>
        private const int MaxDeviceNameBytes = 128;

        /// <summary>Hard ceiling on a single frame. Guards against OOM from a corrupt or hostile length prefix.</summary>
        public const int MaxPayloadBytes = 32 * 1024 * 1024;

        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30);

        private readonly object _gate = new();
        private readonly int _port;

        private Session? _session;
        private TcpListener? _server;
        private CancellationTokenSource? _listenerCts;
        private bool _disposed;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;
        public event EventHandler? ClientConnected;

        /// <summary>Raised once the peer has announced its name.</summary>
        public event EventHandler<PeerIdentifiedEventArgs>? PeerIdentified;

        /// <summary>
        /// Friendly name announced to the peer on connect. Set before listening or connecting.
        /// Dashboards show this instead of a bare IP address.
        /// </summary>
        public string LocalDeviceName { get; set; } = Environment.MachineName;

        /// <summary>Name the peer announced, or null if it has not said yet.</summary>
        public string? RemoteDeviceName { get; private set; }

        public TcpTransportConnection(int port = DefaultPort) => _port = port;

        /// <summary>
        /// True only while a session is live and the peer has been heard from recently.
        /// Deliberately does not use <see cref="TcpClient.Connected"/>, which merely reports
        /// the result of the last I/O and stays true on a half-open socket.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                var s = Volatile.Read(ref _session);
                return s != null && !s.Closed;
            }
        }

        /// <summary>Remote endpoint of the active session, or null.</summary>
        public string? RemoteEndPoint => Volatile.Read(ref _session)?.RemoteDescription;

        public Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            lock (_gate)
            {
                if (_server != null) return Task.CompletedTask;

                _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _server = new TcpListener(IPAddress.Any, _port);
                _server.Start();
            }

            _ = Task.Run(() => AcceptLoopAsync(_server!, _listenerCts!.Token));
            Log.Write("Transport", $"Listening on 0.0.0.0:{_port}");
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(TcpListener server, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await server.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Write("Transport", "Accept failed", ex);
                    // Transient accept errors (e.g. a peer that vanished mid-handshake) must not
                    // kill the listener, but do not spin hot either.
                    try { await Task.Delay(250, token).ConfigureAwait(false); } catch { break; }
                    continue;
                }

                AdoptSession(client, token);
                ClientConnected?.Invoke(this, EventArgs.Empty);
            }

            Log.Write("Transport", "Accept loop stopped.");
        }

        public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(deviceId, _port, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            AdoptSession(client, cancellationToken);
            Log.Write("Transport", $"Connected to {deviceId}:{_port}");
        }

        /// <summary>
        /// Installs <paramref name="client"/> as the active session, retiring any previous one.
        /// Each session owns its own stream, send lock and cancellation, so a retired session's
        /// receive loop can never read from - or tear down - the current connection.
        /// </summary>
        private void AdoptSession(TcpClient client, CancellationToken outerToken)
        {
            ConfigureKeepAlive(client);

            var session = new Session(client, outerToken);
            Session? previous;

            lock (_gate)
            {
                previous = _session;
                _session = session;
            }

            if (previous != null)
            {
                Log.Write("Transport", "Replacing previous session.");
                previous.Dispose();
            }

            _ = Task.Run(() => ReceiveLoopAsync(session));
            _ = Task.Run(() => HeartbeatLoopAsync(session));
            _ = Task.Run(() => SendHelloAsync(session));
        }

        /// <summary>Announces our friendly name so the peer's dashboard can show it.</summary>
        private async Task SendHelloAsync(Session session)
        {
            try
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(LocalDeviceName ?? "");
                if (name.Length > MaxDeviceNameBytes) name = name.AsSpan(0, MaxDeviceNameBytes).ToArray();
                await SendFrameAsync(session, KindHello, name, session.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Purely informational - a failed hello must never break the connection.
                Log.Write("Transport", "Hello send failed", ex);
            }
        }

        private static void ConfigureKeepAlive(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var socket = client.Client;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (Exception ex)
            {
                // Not every platform exposes every keepalive knob; the application-level
                // heartbeat is the real safety net, so this is advisory only.
                Log.Write("Transport", "Keepalive tuning unavailable", ex);
            }
        }

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            if (encryptedPayload.Length > MaxPayloadBytes)
                throw new ArgumentException($"Payload of {encryptedPayload.Length} bytes exceeds the {MaxPayloadBytes} byte limit.", nameof(encryptedPayload));

            var session = Volatile.Read(ref _session);
            if (session == null || session.Closed) throw new InvalidOperationException("Not connected.");

            try
            {
                await SendFrameAsync(session, KindData, encryptedPayload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("Transport", "Send failed, closing session", ex);
                CloseSession(session);
                throw;
            }
        }

        private async Task SendFrameAsync(Session session, byte kind, byte[] payload, CancellationToken cancellationToken)
        {
            // One buffer, one write: concurrent senders (a clipboard copy landing mid-screenshot)
            // previously interleaved their header and body and desynchronised the stream forever.
            byte[] frame = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), Magic);
            frame[2] = ProtocolVersion;
            frame[3] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);

            // Acquired outside the try so a cancelled wait never releases a lock we do not hold.
            await session.SendLock.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (session.Closed) throw new InvalidOperationException("Connection closed.");
                await session.Stream.WriteAsync(frame.AsMemory(), linked.Token).ConfigureAwait(false);
                await session.Stream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                session.SendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(Session session)
        {
            byte[] header = new byte[HeaderSize];

            try
            {
                while (!session.Token.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(session.Stream, header, HeaderSize, session.Token).ConfigureAwait(false))
                        break; // clean EOF

                    if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2)) != Magic)
                    {
                        Log.Write("Transport", "Frame magic mismatch - stream desynchronised, dropping connection.");
                        break;
                    }

                    if (header[2] != ProtocolVersion)
                    {
                        Log.Write("Transport", $"Unsupported protocol version {header[2]} (expected {ProtocolVersion}). Update both devices.");
                        break;
                    }

                    byte kind = header[3];
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

                    if (length < 0 || length > MaxPayloadBytes)
                    {
                        Log.Write("Transport", $"Rejecting frame with implausible length {length}.");
                        break;
                    }

                    byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                    if (length > 0 && !await ReadExactAsync(session.Stream, payload, length, session.Token).ConfigureAwait(false))
                    {
                        Log.Write("Transport", "Peer closed mid-frame.");
                        break;
                    }

                    session.MarkAlive();

                    switch (kind)
                    {
                        case KindData:
                            try
                            {
                                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs { EncryptedPayload = payload });
                            }
                            catch (Exception ex)
                            {
                                // A misbehaving consumer must not kill the connection.
                                Log.Write("Transport", "PayloadReceived handler threw", ex);
                            }
                            break;

                        case KindPing:
                            try { await SendFrameAsync(session, KindPong, Array.Empty<byte>(), session.Token).ConfigureAwait(false); }
                            catch { /* peer went away; the read will fail next */ }
                            break;

                        case KindPong:
                            break;

                        case KindHello:
                            HandleHello(payload);
                            break;

                        default:
                            Log.Write("Transport", $"Ignoring unknown frame kind {kind}.");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on teardown */ }
            catch (ObjectDisposedException) { /* expected on teardown */ }
            catch (Exception ex)
            {
                Log.Write("Transport", "Receive loop error", ex);
            }
            finally
            {
                CloseSession(session);
            }
        }

        private void HandleHello(byte[] payload)
        {
            string name;
            try { name = System.Text.Encoding.UTF8.GetString(payload).Trim(); }
            catch { return; }

            if (name.Length == 0) return;

            RemoteDeviceName = name;
            Log.Write("Transport", $"Peer identified as \"{name}\".");

            try { PeerIdentified?.Invoke(this, new PeerIdentifiedEventArgs { DeviceName = name }); }
            catch (Exception ex) { Log.Write("Transport", "PeerIdentified handler threw", ex); }
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes. TCP is a byte stream: a single
        /// ReadAsync may return fewer bytes than asked for, which previously caused the
        /// 4-byte length prefix to be parsed from a partial read and permanently
        /// desynchronise every subsequent frame.
        /// </summary>
        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private async Task HeartbeatLoopAsync(Session session)
        {
            try
            {
                while (!session.Token.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, session.Token).ConfigureAwait(false);
                    if (session.Closed) return;

                    if (session.SinceLastReceive > PeerTimeout)
                    {
                        Log.Write("Transport", $"No traffic from peer for {session.SinceLastReceive.TotalSeconds:F0}s - treating connection as dead.");
                        CloseSession(session);
                        return;
                    }

                    try
                    {
                        await SendFrameAsync(session, KindPing, Array.Empty<byte>(), session.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Transport", "Heartbeat send failed", ex);
                        CloseSession(session);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        }

        /// <summary>
        /// Tears down <paramref name="session"/> and, only if it is still the active one,
        /// clears it and raises <see cref="ConnectionClosed"/> exactly once.
        /// </summary>
        private void CloseSession(Session session)
        {
            if (!session.MarkClosed()) return;

            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = ReferenceEquals(_session, session);
                if (wasCurrent) _session = null;
            }

            session.Dispose();

            if (wasCurrent)
            {
                Log.Write("Transport", "Connection closed.");
                try { ConnectionClosed?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { Log.Write("Transport", "ConnectionClosed handler threw", ex); }
            }
        }

        public Task DisconnectAsync()
        {
            var session = Volatile.Read(ref _session);
            if (session != null) CloseSession(session);
            // The listener is intentionally left running so peers can reconnect.
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            var session = Volatile.Read(ref _session);
            if (session != null) CloseSession(session);

            try { _listenerCts?.Cancel(); } catch { }
            try { _server?.Stop(); } catch { }
            _listenerCts?.Dispose();

            lock (_gate)
            {
                _server = null;
                _listenerCts = null;
            }

            PayloadReceived = null;
            ConnectionClosed = null;
            ClientConnected = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpTransportConnection));
        }

        /// <summary>One live connection. All per-connection state lives here rather than on the
        /// transport, so a retired connection cannot touch its replacement.</summary>
        private sealed class Session : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            private long _lastReceiveTicks;
            private int _closed;

            public TcpClient Client { get; }
            public NetworkStream Stream { get; }
            public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);
            public CancellationToken Token => _cts.Token;
            public bool Closed => Volatile.Read(ref _closed) != 0;
            public string RemoteDescription { get; }

            public Session(TcpClient client, CancellationToken outerToken)
            {
                Client = client;
                Stream = client.GetStream();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
                _lastReceiveTicks = DateTime.UtcNow.Ticks;

                string remote;
                try { remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown"; }
                catch { remote = "unknown"; }
                RemoteDescription = remote;
            }

            public void MarkAlive() => Volatile.Write(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);

            public TimeSpan SinceLastReceive =>
                DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastReceiveTicks), DateTimeKind.Utc);

            /// <summary>Returns true for the caller that won the race to close this session.</summary>
            public bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;

            public void Dispose()
            {
                Volatile.Write(ref _closed, 1);
                try { _cts.Cancel(); } catch { }
                try { Stream.Dispose(); } catch { }
                try { Client.Dispose(); } catch { }
                try { _cts.Dispose(); } catch { }
                // SendLock is deliberately not disposed: a concurrent sender may still be
                // parked in WaitAsync, and disposing underneath it throws. SemaphoreSlim only
                // holds an unmanaged handle once AvailableWaitHandle is touched, which we never do.
            }
        }
    }
}
