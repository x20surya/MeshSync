using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;
using CoreLib.Identity;

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

        /// <summary>
        /// Bumped to 2 when the hello frame grew a public key, and to 3 when it grew an
        /// ephemeral one for forward secrecy. An older build reading the new shape would take
        /// the length prefixes for the start of a device name, so the version byte is what
        /// turns that into "update both devices" instead of a mystery.
        ///
        /// <para>There is no mixed-version mesh at 3: a peer that cannot offer an ephemeral key
        /// cannot agree a session key at all, so there is nothing to negotiate down to.</para>
        ///
        /// <para>Internal rather than private so the tests that hand-build frames read it from
        /// here. A copy of this number in a test file goes stale the moment it is bumped, and a
        /// version mismatch drops a connection in exactly the way most of those tests are trying
        /// to provoke - so they carry on passing for entirely the wrong reason.</para>
        /// </summary>
        internal const byte ProtocolVersion = 3;

        private const int HeaderSize = 8;

        private const byte KindData = 0;
        private const byte KindPing = 1;
        private const byte KindPong = 2;
        private const byte KindHello = 3;

        /// <summary>Guards against a hostile peer sending a huge name.</summary>
        private const int MaxDeviceNameBytes = 128;

        /// <summary>
        /// Generous room for a base64 P-256 SubjectPublicKeyInfo, which is about 120 bytes.
        /// Anything beyond this is not a key, so it is refused rather than parsed.
        /// </summary>
        private const int MaxPublicKeyBytes = 512;

        /// <summary>
        /// How long a freshly accepted socket has to say who it is before it is dropped.
        ///
        /// Generous, because the hello races the peer's own startup, and dropping a device that
        /// was merely slow would look exactly like a pairing failure.
        /// </summary>
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Hard ceiling on a single frame. Guards against OOM from a corrupt or hostile length prefix.</summary>
        public const int MaxPayloadBytes = 32 * 1024 * 1024;

        /// <summary>
        /// How often a live socket proves itself, and how long silence is tolerated.
        ///
        /// These were 10s and 30s, chosen for fast drop detection before anything weighed the
        /// cost. An idle TCP socket is free, but a heartbeat is not: every one of them pulls
        /// the Wi-Fi chip out of power save. For comparison, the push service every app on the
        /// phone shares heartbeats about every 15 minutes, and most of that is holding a NAT
        /// mapping open across the internet - which does not apply here, because both devices
        /// are on the same subnet with no NAT between them.
        ///
        /// With Bluetooth held as the standing link it also carries presence, and it notices a
        /// vanished peer in 24s regardless. So this only has to catch a socket that died
        /// without a clean close, and 30s of extra latency on that is imperceptible for
        /// clipboard sync. Do not shorten these again without a reason that survives the above.
        /// </summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(90);

        private readonly object _gate = new();
        private readonly int _port;

        private Session? _session;
        private bool _disposed;

        /// <summary>
        /// True when this link was accepted rather than dialled.
        ///
        /// Both devices listen and both dial, so they can collide - each opening a socket to
        /// the other at the same moment. This is what tells the two apart so the collision can
        /// be settled the same way on both sides.
        /// </summary>
        public bool IsInbound { get; private set; }

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

        /// <summary>Base64 public key the peer announced, or null if it has not said yet.</summary>
        public string? RemotePublicKey { get; private set; }

        /// <summary>
        /// This device's base64 public key, announced in the hello so the peer can authorise
        /// us. Left null only by tests and by a build that has no identity yet.
        /// </summary>
        public string? LocalPublicKey { get; set; }

        /// <summary>
        /// What this device calls the mesh, announced so a peer that joined before the name
        /// existed can adopt it rather than showing a placeholder for ever.
        /// </summary>
        public string? LocalMeshName { get; set; }

        /// <summary>
        /// Authorises a peer and agrees the key this connection is encrypted with, given the
        /// static and ephemeral public keys it announced and this connection's own ephemeral.
        ///
        /// <para>Returning null closes the session, which covers both "not a paired device" and
        /// "the key agreement failed". Leaving it null accepts anyone and agrees nothing, which
        /// is what the listener used to do unconditionally - only appropriate for tests and for
        /// a transport with no registry behind it.</para>
        /// </summary>
        public Func<string, string, string, EphemeralKeyPair, PeerSession?>? OpenSession { get; set; }

        /// <summary>
        /// The agreed key for the live connection, or null before the peer's hello arrives.
        ///
        /// Owned by the session and destroyed with it, which is what makes the traffic
        /// unrecoverable once the connection is gone.
        /// </summary>
        public PeerSession? Peer => Volatile.Read(ref _session)?.Peer;

        public TcpTransportConnection(int port = DefaultPort) => _port = port;

        /// <summary>
        /// True only while a session is live, the peer has been heard from recently, and - when
        /// there is a registry behind this transport - a key has actually been agreed.
        ///
        /// <para>That last clause is the "key ready" gate. A socket used to be usable the moment
        /// it opened, because the key was derived from the peer's identity alone and was known
        /// before the connection existed. An ephemeral agreement is not complete until both
        /// hellos have crossed, so reporting the link as connected any earlier would let a
        /// caller hand it a payload there is no key to seal.</para>
        ///
        /// <para>Deliberately does not use <see cref="TcpClient.Connected"/>, which merely
        /// reports the result of the last I/O and stays true on a half-open socket.</para>
        /// </summary>
        public bool IsConnected
        {
            get
            {
                var s = Volatile.Read(ref _session);
                if (s == null || s.Closed) return false;

                return OpenSession == null || s.Peer != null;
            }
        }

        /// <summary>Remote endpoint of the active session, or null.</summary>
        public string? RemoteEndPoint => Volatile.Read(ref _session)?.RemoteDescription;

        /// <summary>
        /// Takes over a socket accepted by <see cref="TcpAcceptor"/>.
        ///
        /// This is the inbound half of a symmetric link. Which side accepted and which dialled
        /// is remembered in <see cref="IsInbound"/>, because that is what settles a collision
        /// when both devices dial each other at the same moment.
        /// </summary>
        public void Adopt(TcpClient client, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (client == null) throw new ArgumentNullException(nameof(client));

            IsInbound = true;
            AdoptSession(client, cancellationToken);
            ClientConnected?.Invoke(this, EventArgs.Empty);
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

            IsInbound = false;
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

            // Only when there is something to authorise against. Without a registry behind it
            // there is no such thing as an unidentified peer, and a test harness that never
            // sends a hello would be torn down ten seconds in.
            if (OpenSession != null) _ = Task.Run(() => EnforceHelloAsync(session));
        }

        /// <summary>
        /// Announces who we are: a friendly name for the peer's dashboard, the public key it
        /// will authorise us by, and this connection's ephemeral key.
        ///
        /// <para>Sent unprompted by both ends the moment a socket exists, so the two ephemeral
        /// keys are in flight immediately and neither side has to ask. That is what keeps the
        /// agreement to zero extra round trips.</para>
        /// </summary>
        private async Task SendHelloAsync(Session session)
        {
            try
            {
                await SendFrameAsync(session, KindHello,
                    BuildHello(LocalDeviceName, LocalPublicKey ?? "", LocalMeshName ?? "",
                               session.Ephemeral.PublicKey), session.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Transport", "Hello send failed", ex);
            }
        }

        /// <summary>
        /// Drops a session whose peer never introduced itself.
        ///
        /// Without this, refusing unknown peers would be trivially bypassed by simply not
        /// sending a hello: the socket would stay open, every payload would fail to decrypt,
        /// and the link would sit there looking connected forever.
        /// </summary>
        private async Task EnforceHelloAsync(Session session)
        {
            try
            {
                await Task.Delay(HelloTimeout, session.Token).ConfigureAwait(false);

                if (session.Closed || session.PeerFingerprint != null) return;

                Log.Write("Transport",
                    $"No hello within {HelloTimeout.TotalSeconds:F0}s - dropping an unidentified connection.");
                CloseSession(session);
            }
            catch (OperationCanceledException) { /* expected on teardown */ }
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
                                PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs
                                {
                                    EncryptedPayload = payload,
                                    Fingerprint = session.PeerFingerprint ?? ""
                                });
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
                            HandleHello(session, payload);
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

        private void HandleHello(Session session, byte[] payload)
        {
            if (!TryParseHello(payload, out string name, out string publicKey, out string meshName, out string ephemeralKey))
            {
                Log.Write("Transport", "Malformed hello - dropping the connection.");
                CloseSession(session);
                return;
            }

            string fingerprint = "";
            if (publicKey.Length > 0)
            {
                try { fingerprint = Identity.DeviceIdentity.FingerprintOf(publicKey); }
                catch (Exception ex)
                {
                    Log.Write("Transport", "A peer announced a public key that will not parse - dropping the connection.", ex);
                    CloseSession(session);
                    return;
                }
            }

            // The listener used to accept anything that could reach the port, because every
            // install shared one key and there was nothing to check against. Now a peer has to
            // be one this device has paired with, and agreeing a key is part of the same step -
            // a stranger is dropped here rather than left to fail every decryption for the life
            // of the socket.
            var open = OpenSession;
            if (open != null)
            {
                var agreed = open(publicKey, name, ephemeralKey, session.Ephemeral);
                if (agreed == null)
                {
                    Log.Write("Transport",
                        $"Refusing \"{name}\": {(publicKey.Length == 0 ? "it announced no identity" : $"no session could be agreed with {Identity.DeviceIdentity.Shorten(fingerprint)}")}.");
                    CloseSession(session);
                    return;
                }

                // A second hello on one connection would otherwise leak the first key. There is
                // no legitimate reason for one, so the replacement is logged rather than silent.
                var previous = Interlocked.Exchange(ref session.PeerRef, agreed);
                if (previous != null)
                {
                    Log.Write("Transport", "A second hello arrived on one connection; the earlier session key was discarded.");
                    previous.Dispose();
                }
            }

            session.PeerFingerprint = fingerprint;

            if (name.Length > 0)
            {
                RemoteDeviceName = name;
                RemotePublicKey = publicKey;
                Log.Write("Transport", $"Peer identified as \"{name}\" ({Identity.DeviceIdentity.Shorten(fingerprint)}).");
            }

            try
            {
                PeerIdentified?.Invoke(this, new PeerIdentifiedEventArgs
                {
                    DeviceName = name,
                    PublicKey = publicKey,
                    Fingerprint = fingerprint,
                    Address = session.RemoteAddress,
                    MeshName = meshName
                });
            }
            catch (Exception ex) { Log.Write("Transport", "PeerIdentified handler threw", ex); }
        }

        /// <summary>
        /// Hello payload:
        /// <c>[nameLen u8][name][keyLen u16][static key][meshLen u8][mesh][ephLen u16][ephemeral key]</c>.
        ///
        /// It used to be the raw name and nothing else. The length prefixes are what let each
        /// later field be added at all, and the protocol version above is what stops an older
        /// build reading the new shape as a very strangely spelled device name.
        /// </summary>
        /// <summary>
        /// A complete, framed hello, for a test that needs to control <em>when</em> one arrives.
        ///
        /// <para><c>internal</c> for the same reason <see cref="ProtocolVersion"/> is: a copy of a
        /// wire format in a test file goes stale the moment the real one moves, and then the test
        /// carries on passing for the wrong reason. That has happened twice here already.</para>
        ///
        /// <para>It exists because a real transport sends its hello the instant a socket exists,
        /// which makes "an accepted socket whose hello has not arrived yet" - the window a link
        /// used to survive <c>DisconnectAll</c> in - impossible to reach from the outside.</para>
        /// </summary>
        internal static byte[] BuildHelloFrame(string name, string publicKey, string meshName, string ephemeralKey)
        {
            byte[] payload = BuildHello(name, publicKey, meshName, ephemeralKey);
            byte[] frame = new byte[HeaderSize + payload.Length];

            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), Magic);
            frame[2] = ProtocolVersion;
            frame[3] = KindHello;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)payload.Length);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);

            return frame;
        }

        private static byte[] BuildHello(string name, string publicKey, string meshName, string ephemeralKey)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            if (nameBytes.Length > MaxDeviceNameBytes) nameBytes = nameBytes.AsSpan(0, MaxDeviceNameBytes).ToArray();

            byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(publicKey ?? "");
            if (keyBytes.Length > MaxPublicKeyBytes) keyBytes = Array.Empty<byte>();

            byte[] meshBytes = System.Text.Encoding.UTF8.GetBytes(meshName ?? "");
            if (meshBytes.Length > MaxDeviceNameBytes) meshBytes = meshBytes.AsSpan(0, MaxDeviceNameBytes).ToArray();

            byte[] ephBytes = System.Text.Encoding.UTF8.GetBytes(ephemeralKey ?? "");
            if (ephBytes.Length > MaxPublicKeyBytes) ephBytes = Array.Empty<byte>();

            var payload = new byte[1 + nameBytes.Length + 2 + keyBytes.Length + 1 + meshBytes.Length + 2 + ephBytes.Length];

            payload[0] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);

            int keyOffset = 1 + nameBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(keyOffset, 2), (ushort)keyBytes.Length);
            Buffer.BlockCopy(keyBytes, 0, payload, keyOffset + 2, keyBytes.Length);

            int meshOffset = keyOffset + 2 + keyBytes.Length;
            payload[meshOffset] = (byte)meshBytes.Length;
            Buffer.BlockCopy(meshBytes, 0, payload, meshOffset + 1, meshBytes.Length);

            int ephOffset = meshOffset + 1 + meshBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ephOffset, 2), (ushort)ephBytes.Length);
            Buffer.BlockCopy(ephBytes, 0, payload, ephOffset + 2, ephBytes.Length);

            return payload;
        }

        /// <summary>
        /// Reads a hello. The trailing fields are optional so a shorter one still parses, which
        /// matters for the tests that hand-build frames rather than for the wire - the version
        /// byte already refuses a peer old enough to omit them.
        ///
        /// The mesh name is carried here as well as in the pairing code because the code only
        /// reaches a device at the moment it joins. Devices paired before the mesh had a name
        /// would otherwise never learn one, which is exactly what happened the first time this
        /// shipped - the phone sat there calling it "your mesh" for ever.
        /// </summary>
        internal static bool TryParseHello(byte[] payload, out string name, out string publicKey,
                                           out string meshName, out string ephemeralKey)
        {
            name = "";
            publicKey = "";
            meshName = "";
            ephemeralKey = "";

            if (payload.Length < 1) return false;

            int nameLength = payload[0];
            if (payload.Length < 1 + nameLength + 2) return false;

            try { name = System.Text.Encoding.UTF8.GetString(payload, 1, nameLength).Trim(); }
            catch { return false; }

            int keyOffset = 1 + nameLength;
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(keyOffset, 2));
            if (keyLength > MaxPublicKeyBytes) return false;
            if (payload.Length < keyOffset + 2 + keyLength) return false;

            if (keyLength > 0)
            {
                try { publicKey = System.Text.Encoding.UTF8.GetString(payload, keyOffset + 2, keyLength).Trim(); }
                catch { return false; }
            }

            int meshOffset = keyOffset + 2 + keyLength;
            if (payload.Length <= meshOffset) return true;

            int meshLength = payload[meshOffset];
            if (payload.Length < meshOffset + 1 + meshLength) return true;

            try { meshName = System.Text.Encoding.UTF8.GetString(payload, meshOffset + 1, meshLength).Trim(); }
            catch { meshName = ""; }

            int ephOffset = meshOffset + 1 + meshLength;
            if (payload.Length < ephOffset + 2) return true;

            int ephLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(ephOffset, 2));
            if (ephLength > MaxPublicKeyBytes) return false;
            if (payload.Length < ephOffset + 2 + ephLength) return true;

            if (ephLength > 0)
            {
                try { ephemeralKey = System.Text.Encoding.UTF8.GetString(payload, ephOffset + 2, ephLength).Trim(); }
                catch { ephemeralKey = ""; }
            }

            return true;
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

            /// <summary>
            /// This connection's ephemeral keypair, minted with the session and destroyed with
            /// it. Announced in the hello; never persisted anywhere.
            /// </summary>
            public EphemeralKeyPair Ephemeral { get; } = EphemeralKeyPair.Create();

            /// <summary>
            /// The agreed key, once the peer's hello has arrived. Exposed as a field so the
            /// handshake can install it with an interlocked exchange - two hellos on one
            /// connection would otherwise leak the first key rather than disposing it.
            /// </summary>
            public PeerSession? PeerRef;

            public PeerSession? Peer => Volatile.Read(ref PeerRef);

            /// <summary>
            /// Set once the peer has introduced itself and been authorised. Null means it has
            /// not yet, which is what the hello deadline watches for.
            /// </summary>
            public string? PeerFingerprint { get; set; }

            /// <summary>
            /// The peer's address without the port, for recording where it was reachable.
            ///
            /// Unwrapped from the IPv4-mapped IPv6 form first. The listener binds
            /// <see cref="IPAddress.Any"/> in dual-stack mode, so a peer that connected over
            /// IPv4 is reported as <c>::ffff:192.168.0.103</c>. That parses as an address and
            /// looks entirely reasonable in a log, and dialling it back fails every time -
            /// observed as a connect timeout against a device that was plainly right there.
            /// </summary>
            public string RemoteAddress
            {
                get
                {
                    try
                    {
                        if (Client.Client.RemoteEndPoint is not IPEndPoint endpoint) return RemoteDescription;

                        var address = endpoint.Address;
                        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

                        return address.ToString();
                    }
                    catch { return RemoteDescription; }
                }
            }

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

                // The whole point of the ephemeral agreement: once these two are gone, the key
                // this connection used cannot be recomputed by anyone, including us.
                try { Interlocked.Exchange(ref PeerRef, null)?.Dispose(); } catch { }
                try { Ephemeral.Dispose(); } catch { }

                // SendLock is deliberately not disposed: a concurrent sender may still be
                // parked in WaitAsync, and disposing underneath it throws. SemaphoreSlim only
                // holds an unmanaged handle once AvailableWaitHandle is touched, which we never do.
            }
        }
    }
}
