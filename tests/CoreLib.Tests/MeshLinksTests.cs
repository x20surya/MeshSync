using System.Net;
using System.Net.Sockets;
using CoreLib.Identity;
using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// The link accounting: several peers at once, symmetric roles, and what happens when two
/// devices dial each other at the same moment. None of this could be tested before, because
/// the transport held one session on a device whose role was fixed at compile time.
/// </summary>
public class MeshLinksTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try { _disposables[i].Dispose(); } catch { }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>One device: an identity, a registry, and its links.</summary>
    private sealed class Device : IDisposable
    {
        public PeerSecurity Security { get; }
        public MeshLinks Links { get; }
        public int Port { get; }
        public string Fingerprint => Security.Identity.Fingerprint;
        public string PublicKey => Security.Identity.PublicKey;

        public Device(string name, int port)
        {
            Port = port;
            Security = PeerSecurity.CreateEphemeral();
            Links = new MeshLinks(Security, port) { LocalDeviceName = name };
        }

        /// <summary>Records another device as paired, at loopback on its own port.</summary>
        public void Pair(Device other) =>
            Security.Peers.Trust(other.PublicKey, other.Links.LocalDeviceName, "127.0.0.1");

        public PeerRecord PeerFor(Device other) => Security.Peers.Find(other.Fingerprint)!;

        public void Dispose()
        {
            Links.Dispose();
            Security.Dispose();
        }
    }

    private Device NewDevice(string name, int? port = null)
    {
        var device = new Device(name, port ?? FreePort());
        _disposables.Add(device);
        return device;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>
    /// The connection has to be refused by identity, not merely fail to decrypt afterwards.
    /// The listener used to accept anything that could reach the port.
    ///
    /// <para>The assertion is on the durable outcome rather than on the return of the dial.
    /// Refusal happens when the host reads the hello, which is after the socket is open, so a
    /// rejected dialler can briefly believe it succeeded - it only learns otherwise when the
    /// connection closes underneath it. What matters is that the link does not survive and
    /// that the host never records the device.</para>
    /// </summary>
    [Fact]
    public async Task An_unpaired_device_is_refused()
    {
        var host = NewDevice("Host");
        var stranger = NewDevice("Stranger");

        await host.Links.StartListeningAsync();

        // The stranger knows where the host is; the host has never heard of it.
        stranger.Pair(host);

        await stranger.Links.ConnectToAsync(PeerAt(stranger, host), TimeSpan.FromSeconds(3));

        Assert.True(await WaitFor(() => !host.Links.IsConnectedToAny && !stranger.Links.IsConnectedToAny),
            "A device that was never paired kept a live link.");

        Assert.True(host.Security.Peers.IsEmpty);
    }

    /// <summary>
    /// The listener binds in dual-stack mode, so a peer that connected over IPv4 is reported
    /// as <c>::ffff:192.168.0.103</c>. That parses as an address and reads perfectly well in a
    /// log, and dialling it back fails every time - seen in the field as a connect timeout
    /// against a device that was plainly right there.
    /// </summary>
    [Fact]
    public async Task An_ipv4_mapped_address_is_dialled_as_ipv4()
    {
        var laptop = NewDevice("Laptop");
        var phone = NewDevice("Phone");

        laptop.Pair(phone);
        phone.Pair(laptop);

        await laptop.Links.StartListeningAsync();

        var record = phone.Security.Peers.Find(laptop.Fingerprint)!;

        // Bracketed, because that is how an IPv6 endpoint carries a port. The form the daemon
        // actually recorded had no port at all, which the bare-address case below covers.
        record.LastAddress = $"[::ffff:127.0.0.1]:{laptop.Port}";

        Assert.True(await phone.Links.ConnectToAsync(record, TimeSpan.FromSeconds(10)),
            "A peer recorded in the IPv4-mapped form could not be dialled.");
    }

    /// <summary>
    /// The exact value the daemon wrote into its registry: mapped, and with no port, because
    /// the port a peer connected from is ephemeral and never worth recording.
    /// </summary>
    [Fact]
    public void An_ipv4_mapped_address_without_a_port_unwraps()
    {
        Assert.True(System.Net.IPAddress.TryParse("::ffff:192.168.0.103", out var parsed));
        Assert.True(parsed!.IsIPv4MappedToIPv6);
        Assert.Equal("192.168.0.103", parsed.MapToIPv4().ToString());
    }

    [Fact]
    public async Task Two_paired_devices_connect_and_exchange_a_payload()
    {
        var laptop = NewDevice("Laptop");
        var phone = NewDevice("Phone");

        laptop.Pair(phone);
        phone.Pair(laptop);

        await laptop.Links.StartListeningAsync();

        var received = new TaskCompletionSource<MeshPayloadEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        laptop.Links.PayloadReceived += (_, e) => received.TrySetResult(e);

        Assert.True(await phone.Links.ConnectToAsync(PeerAt(phone, laptop), TimeSpan.FromSeconds(10)));

        int sent = await phone.Links.BroadcastAsync(SyncContent.Text, "hello laptop"u8.ToArray());
        Assert.Equal(1, sent);

        var payload = await received.Task.WaitAsync(Timeout);
        Assert.Equal("hello laptop"u8.ToArray(), payload.Body);
        Assert.Equal(SyncContent.Text, payload.ContentType);
        Assert.Equal(phone.Fingerprint, payload.Peer.Fingerprint);
    }

    /// <summary>
    /// The one-to-many property. A second peer used to evict the first, because there was one
    /// session field to hold it in.
    /// </summary>
    [Fact]
    public async Task A_hub_holds_links_to_several_peers_at_once()
    {
        var hub = NewDevice("Hub");
        var phone = NewDevice("Phone");
        var tablet = NewDevice("Tablet");

        foreach (var peer in new[] { phone, tablet })
        {
            hub.Pair(peer);
            peer.Pair(hub);
        }

        await hub.Links.StartListeningAsync();

        Assert.True(await phone.Links.ConnectToAsync(PeerAt(phone, hub), TimeSpan.FromSeconds(10)));
        Assert.True(await tablet.Links.ConnectToAsync(PeerAt(tablet, hub), TimeSpan.FromSeconds(10)));

        Assert.True(await WaitFor(() => hub.Links.ConnectedCount == 2));

        Assert.True(hub.Links.IsConnectedTo(phone.Fingerprint));
        Assert.True(hub.Links.IsConnectedTo(tablet.Fingerprint));
    }

    /// <summary>
    /// Fan-out, and the reason it is N encryptions rather than one broadcast: each peer gets a
    /// payload sealed with its own key.
    /// </summary>
    [Fact]
    public async Task A_broadcast_reaches_every_peer_and_each_can_read_it()
    {
        var hub = NewDevice("Hub");
        var phone = NewDevice("Phone");
        var tablet = NewDevice("Tablet");

        foreach (var peer in new[] { phone, tablet })
        {
            hub.Pair(peer);
            peer.Pair(hub);
            await peer.Links.StartListeningAsync();
        }

        Assert.True(await hub.Links.ConnectToAsync(PeerAt(hub, phone), TimeSpan.FromSeconds(10)));
        Assert.True(await hub.Links.ConnectToAsync(PeerAt(hub, tablet), TimeSpan.FromSeconds(10)));

        var atPhone = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atTablet = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        phone.Links.PayloadReceived += (_, e) => atPhone.TrySetResult(e.Body);
        tablet.Links.PayloadReceived += (_, e) => atTablet.TrySetResult(e.Body);

        byte[] body = "shared clipboard"u8.ToArray();
        int sent = await hub.Links.BroadcastAsync(SyncContent.Text, body);

        Assert.Equal(2, sent);
        Assert.Equal(body, await atPhone.Task.WaitAsync(Timeout));
        Assert.Equal(body, await atTablet.Task.WaitAsync(Timeout));
    }

    /// <summary>
    /// Both devices listen and both dial, so both can open a socket to the other at the same
    /// moment. They must converge on one link without negotiating, and neither may be left
    /// believing it is disconnected.
    /// </summary>
    [Fact]
    public async Task Simultaneous_dialling_converges_on_a_single_link()
    {
        var a = NewDevice("A");
        var b = NewDevice("B");

        a.Pair(b);
        b.Pair(a);

        await a.Links.StartListeningAsync();
        await b.Links.StartListeningAsync();

        // Deliberately at the same moment, which is the case the tiebreak exists for.
        var dialA = a.Links.ConnectToAsync(PeerAt(a, b), TimeSpan.FromSeconds(10));
        var dialB = b.Links.ConnectToAsync(PeerAt(b, a), TimeSpan.FromSeconds(10));
        await Task.WhenAll(dialA, dialB);

        Assert.True(await WaitFor(() => a.Links.ConnectedCount == 1 && b.Links.ConnectedCount == 1));

        // And the surviving link still carries traffic in both directions.
        var atA = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atB = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Links.PayloadReceived += (_, e) => atA.TrySetResult(e.Body);
        b.Links.PayloadReceived += (_, e) => atB.TrySetResult(e.Body);

        Assert.Equal(1, await a.Links.BroadcastAsync(SyncContent.Text, "a to b"u8.ToArray()));
        Assert.Equal(1, await b.Links.BroadcastAsync(SyncContent.Text, "b to a"u8.ToArray()));

        Assert.Equal("a to b"u8.ToArray(), await atB.Task.WaitAsync(Timeout));
        Assert.Equal("b to a"u8.ToArray(), await atA.Task.WaitAsync(Timeout));
    }

    /// <summary>
    /// Dropping the links must not stop the device being reachable: under Bluetooth standby
    /// this happens every time the screen goes off, and a peer may still dial in.
    /// </summary>
    [Fact]
    public async Task Dropping_links_leaves_the_device_listening()
    {
        var laptop = NewDevice("Laptop");
        var phone = NewDevice("Phone");

        laptop.Pair(phone);
        phone.Pair(laptop);

        await laptop.Links.StartListeningAsync();
        Assert.True(await phone.Links.ConnectToAsync(PeerAt(phone, laptop), TimeSpan.FromSeconds(10)));

        laptop.Links.DisconnectAll();
        Assert.True(await WaitFor(() => !laptop.Links.IsConnectedToAny));

        // Still reachable, so the peer can come back without the laptop having to dial.
        phone.Links.DisconnectAll();
        Assert.True(await phone.Links.ConnectToAsync(PeerAt(phone, laptop), TimeSpan.FromSeconds(10)));
        Assert.True(await WaitFor(() => laptop.Links.IsConnectedToAny));
    }

    [Fact]
    public async Task A_disconnecting_peer_raises_the_event_once()
    {
        var laptop = NewDevice("Laptop");
        var phone = NewDevice("Phone");

        laptop.Pair(phone);
        phone.Pair(laptop);

        await laptop.Links.StartListeningAsync();
        Assert.True(await phone.Links.ConnectToAsync(PeerAt(phone, laptop), TimeSpan.FromSeconds(10)));

        int disconnects = 0;
        laptop.Links.PeerDisconnected += _ => Interlocked.Increment(ref disconnects);

        phone.Links.DisconnectAll();

        Assert.True(await WaitFor(() => Volatile.Read(ref disconnects) >= 1));
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref disconnects));
    }

    /// <summary>
    /// Points a stored record at the target's actual listening port.
    ///
    /// In the field every device listens on the same port and a bare address is all that is
    /// needed. Several devices on one machine cannot, so these tests use the <c>host:port</c>
    /// form. Re-applied on every dial because a hello overwrites the record with the bare
    /// address it saw the peer arrive from.
    /// </summary>
    private static PeerRecord PeerAt(Device dialler, Device target)
    {
        var record = dialler.Security.Peers.Find(target.Fingerprint)!;
        record.LastAddress = $"127.0.0.1:{target.Port}";
        return record;
    }
}
