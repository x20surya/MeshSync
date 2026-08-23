using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CoreLib.Transport;

namespace CoreLib.Tests;

[Collection(LoopbackCollection.Name)]
public class TcpTransportConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The wire version these hand-built frames claim, read from the transport rather than
    /// copied. A copy went stale the last two times the version moved, and a stale one makes
    /// every such test pass for the wrong reason: a version mismatch drops the connection in
    /// exactly the way the test is trying to provoke.
    /// </summary>
    private const byte WireVersion = TcpTransportConnection.ProtocolVersion;

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A listener plus the single session it adopts - the shape the transport used to have in
    /// one object, before listening moved to <see cref="TcpAcceptor"/> so a session could exist
    /// per peer. Keeps these tests about framing rather than about link accounting, which
    /// <see cref="MeshLinksTests"/> covers instead.
    /// </summary>
    private sealed class Endpoint : IDisposable
    {
        private readonly TcpAcceptor _acceptor;

        public TcpTransportConnection Connection { get; }

        private Endpoint(int port)
        {
            Connection = new TcpTransportConnection(port);
            _acceptor = new TcpAcceptor(port);
            _acceptor.Accepted += client => Connection.Adopt(client);
        }

        public static async Task<Endpoint> ListenAsync(int port)
        {
            var endpoint = new Endpoint(port);
            await endpoint._acceptor.StartAsync();
            return endpoint;
        }

        public void Dispose()
        {
            _acceptor.Dispose();
            Connection.Dispose();
        }
    }

    private static async Task<(Endpoint listener, TcpTransportConnection server, TcpTransportConnection client)>
        ConnectPairAsync(int port)
    {
        var listener = await Endpoint.ListenAsync(port);

        var client = new TcpTransportConnection(port);
        await client.ConnectAsync("127.0.0.1");

        // Let the accept side finish adopting the session.
        for (int i = 0; i < 100 && !listener.Connection.IsConnected; i++) await Task.Delay(20);

        return (listener, listener.Connection, client);
    }

    /// <summary>
    /// A payload far larger than one TCP segment. The previous implementation parsed its
    /// 4-byte length prefix from whatever a single ReadAsync happened to return, so a
    /// partially delivered prefix permanently desynchronised the stream. This is the
    /// screenshot-beaming case that made the connection stop working.
    /// </summary>
    [Fact]
    public async Task Large_payload_arrives_intact()
    {
        int port = FreePort();
        var (listener, server, client) = await ConnectPairAsync(port);
        using var _l = listener;
        using var _s = server;
        using var _c = client;

        var payload = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PayloadReceived += (_, e) => received.TrySetResult(e.EncryptedPayload);

        await client.SendPayloadAsync(payload);

        var result = await received.Task.WaitAsync(Timeout);
        Assert.Equal(payload.Length, result.Length);
        Assert.True(payload.AsSpan().SequenceEqual(result), "Payload bytes were corrupted in transit.");
    }

    /// <summary>
    /// Concurrent senders previously interleaved their length prefix and body, because the
    /// header and the payload went out as two unsynchronised writes. Copying text while a
    /// screenshot was still uploading was enough to corrupt the stream for good.
    /// </summary>
    [Fact]
    public async Task Concurrent_sends_are_not_interleaved()
    {
        int port = FreePort();
        var (listener, server, client) = await ConnectPairAsync(port);
        using var _l = listener;
        using var _s = server;
        using var _c = client;

        const int messageCount = 40;
        var expected = new Dictionary<int, byte[]>();
        for (int i = 0; i < messageCount; i++)
        {
            // Varied sizes so a misframed read cannot accidentally line up.
            var body = new byte[1024 + i * 4096];
            RandomNumberGenerator.Fill(body);
            BinaryPrimitives.WriteInt32LittleEndian(body, i);
            expected[i] = body;
        }

        var received = new ConcurrentBag<byte[]>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PayloadReceived += (_, e) =>
        {
            received.Add(e.EncryptedPayload);
            if (received.Count == messageCount) all.TrySetResult();
        };

        await Task.WhenAll(expected.Values.Select(body => Task.Run(() => client.SendPayloadAsync(body))));
        await all.Task.WaitAsync(Timeout);

        Assert.Equal(messageCount, received.Count);
        foreach (var body in received)
        {
            int index = BinaryPrimitives.ReadInt32LittleEndian(body);
            Assert.True(expected.ContainsKey(index), $"Received an unrecognised frame tagged {index}.");
            Assert.True(expected[index].AsSpan().SequenceEqual(body), $"Frame {index} was corrupted.");
        }
    }

    /// <summary>
    /// A corrupt or hostile length prefix used to be trusted verbatim and passed straight to
    /// <c>new byte[length]</c>, which is an out-of-memory kill on a phone.
    /// </summary>
    [Fact]
    public async Task Implausible_length_prefix_closes_the_connection_without_allocating()
    {
        int port = FreePort();
        using var listener = await Endpoint.ListenAsync(port);
        var server = listener.Connection;

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionClosed += (_, _) => closed.TrySetResult();
        server.PayloadReceived += (_, _) => Assert.Fail("A frame with an implausible length must never be delivered.");

        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);

        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), 0x4D53); // valid magic
        header[2] = WireVersion;                                                // valid version
        header[3] = 0;                                                          // data frame
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), int.MaxValue);

        await raw.GetStream().WriteAsync(header);
        await raw.GetStream().FlushAsync();

        await closed.Task.WaitAsync(Timeout);
        Assert.False(server.IsConnected);
    }

    /// <summary>A desynchronised stream is detected by the frame magic rather than acted on.</summary>
    [Fact]
    public async Task Garbage_frame_header_closes_the_connection()
    {
        int port = FreePort();
        using var listener = await Endpoint.ListenAsync(port);
        var server = listener.Connection;

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionClosed += (_, _) => closed.TrySetResult();

        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        await raw.GetStream().WriteAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
        await raw.GetStream().FlushAsync();

        await closed.Task.WaitAsync(Timeout);
    }

    /// <summary>
    /// Accepting a second connection used to overwrite the shared stream field while the
    /// first receive loop was still running against it, so two loops consumed one socket.
    /// </summary>
    [Fact]
    public async Task Reconnecting_replaces_the_session_cleanly()
    {
        int port = FreePort();
        using var listener = await Endpoint.ListenAsync(port);
        var server = listener.Connection;

        var firstClient = new TcpTransportConnection(port);
        await firstClient.ConnectAsync("127.0.0.1");
        for (int i = 0; i < 100 && !server.IsConnected; i++) await Task.Delay(20);
        firstClient.Dispose();

        using var secondClient = new TcpTransportConnection(port);
        await secondClient.ConnectAsync("127.0.0.1");
        for (int i = 0; i < 100 && !server.IsConnected; i++) await Task.Delay(20);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PayloadReceived += (_, e) => received.TrySetResult(e.EncryptedPayload);

        var payload = new byte[64_000];
        RandomNumberGenerator.Fill(payload);
        await secondClient.SendPayloadAsync(payload);

        var result = await received.Task.WaitAsync(Timeout);
        Assert.True(payload.AsSpan().SequenceEqual(result), "The replacement session delivered corrupted data.");
    }

    [Fact]
    public async Task Disconnect_raises_ConnectionClosed_on_the_peer()
    {
        int port = FreePort();
        var (listener, server, client) = await ConnectPairAsync(port);
        using var _l = listener;
        using var _s = server;

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionClosed += (_, _) => closed.TrySetResult();

        client.Dispose();

        await closed.Task.WaitAsync(Timeout);
        Assert.False(server.IsConnected);
    }

    [Fact]
    public async Task Sending_while_disconnected_throws_rather_than_silently_dropping()
    {
        using var client = new TcpTransportConnection(FreePort());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendPayloadAsync(new byte[8]));
    }

    [Fact]
    public async Task Payload_over_the_size_limit_is_rejected_before_sending()
    {
        int port = FreePort();
        var (listener, server, client) = await ConnectPairAsync(port);
        using var _l = listener;
        using var _s = server;
        using var _c = client;

        var oversized = new byte[TcpTransportConnection.MaxPayloadBytes + 1];

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendPayloadAsync(oversized));
    }

    [Fact]
    public async Task Empty_payload_round_trips()
    {
        int port = FreePort();
        var (listener, server, client) = await ConnectPairAsync(port);
        using var _l = listener;
        using var _s = server;
        using var _c = client;

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PayloadReceived += (_, e) => received.TrySetResult(e.EncryptedPayload);

        await client.SendPayloadAsync(Array.Empty<byte>());

        var result = await received.Task.WaitAsync(Timeout);
        Assert.Empty(result);
    }

    /// <summary>
    /// Delivers one frame in several TCP segments, with the 8-byte header itself split.
    /// TCP gives no guarantee that a read returns everything asked for, and the old receive
    /// loop parsed the length from whatever the first read produced - so a split header
    /// meant every following frame was misaligned.
    /// </summary>
    [Fact]
    public async Task Frame_header_split_across_segments_is_reassembled()
    {
        int port = FreePort();
        using var listener = await Endpoint.ListenAsync(port);
        var server = listener.Connection;

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PayloadReceived += (_, e) => received.TrySetResult(e.EncryptedPayload);

        var body = new byte[200_000];
        RandomNumberGenerator.Fill(body);

        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), 0x4D53);
        header[2] = WireVersion;
        header[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), body.Length);

        using var raw = new TcpClient { NoDelay = true };
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var stream = raw.GetStream();

        // Header arrives in three pieces, straddling the length field.
        await stream.WriteAsync(header.AsMemory(0, 3));
        await stream.FlushAsync();
        await Task.Delay(40);
        await stream.WriteAsync(header.AsMemory(3, 3));
        await stream.FlushAsync();
        await Task.Delay(40);
        await stream.WriteAsync(header.AsMemory(6, 2));
        await stream.FlushAsync();
        await Task.Delay(40);

        // Body in small chunks too.
        for (int offset = 0; offset < body.Length; offset += 8192)
        {
            int count = Math.Min(8192, body.Length - offset);
            await stream.WriteAsync(body.AsMemory(offset, count));
        }
        await stream.FlushAsync();

        var result = await received.Task.WaitAsync(Timeout);
        Assert.True(body.AsSpan().SequenceEqual(result), "A frame split across segments was reassembled incorrectly.");
    }

    [Fact]
    public async Task Dispose_releases_the_listening_port()
    {
        int port = FreePort();

        var first = new TcpAcceptor(port);
        await first.StartAsync();
        first.Dispose();

        // Binding again would throw if the listener socket had been leaked.
        using var second = new TcpAcceptor(port);
        await second.StartAsync();
    }
}
