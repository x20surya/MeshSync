using System.Net;
using System.Net.Sockets;
using CoreLib.Identity;
using CoreLib.Transport;
using CoreLib.Transport.Fabric;

namespace CoreLib.Tests;

/// <summary>
/// Three devices, real sockets, the fabric in front of them.
///
/// <para>This is the phase-one claim: a mesh of three, every pair connected directly, every device
/// both listening and dialling, and no head owning a connection field. It runs over loopback with
/// a port each, which is the same arrangement <c>--data</c> and <c>--port</c> give the Linux
/// daemon.</para>
/// </summary>
[Collection(LoopbackCollection.Name)]
public class MeshFabricLoopbackTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly List<Device> _devices = new();

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>One device: an identity, a fabric, a socket provider and a supervisor.</summary>
    private sealed class Device : IAsyncDisposable
    {
        public PeerSecurity Security { get; }
        public MeshFabric Fabric { get; }
        public WiFiRouteProvider WiFi { get; }
        public LinkSupervisor Supervisor { get; }
        public int Port { get; }
        public string Name { get; }
        public List<RoutePayload> Received { get; } = new();

        public string Fingerprint => Security.Identity.Fingerprint;
        public string PublicKey => Security.Identity.PublicKey;

        public Device(string name, int port)
        {
            Name = name;
            Port = port;
            Security = PeerSecurity.CreateEphemeral();

            Fabric = new MeshFabric(Security, () => BleCapability.None);

            // Every device listens somewhere different here, so the port to dial has to come from
            // the stored address rather than from this device's own listener.
            WiFi = new WiFiRouteProvider(Security, port, port) { LocalDeviceName = name };
            Fabric.AddProvider(WiFi);

            Fabric.PayloadReceived += (_, payload) => { lock (Received) Received.Add(payload); };

            Supervisor = new LinkSupervisor(Fabric, () => new LocalConditions
            {
                LocalFingerprint = Fingerprint,
                ScreenOn = true,
                HasUsableNetwork = true,

                // No radio in this rig, so the plan is sockets only and nothing is owed a scan.
                LocalCapability = BleCapability.None,
            });
        }

        public Task StartAsync() => WiFi.StartListeningAsync();

        /// <summary>Records another device as paired, at loopback on its own port.</summary>
        public void Pair(Device other) =>
            Security.Peers.Trust(other.PublicKey, other.Name, $"127.0.0.1:{other.Port}");

        public Task ReconcileAsync() => Supervisor.ReconcileAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            await Fabric.DisposeAsync();
        }
    }

    private Device NewDevice(string name)
    {
        var device = new Device(name, FreePort());
        _devices.Add(device);
        return device;
    }

    private static async Task<bool> WaitFor(Func<bool> condition) => await WaitFor(condition, Array.Empty<Device>());

    /// <summary>
    /// Waits for the mesh to settle, reconciling as it goes.
    ///
    /// <para>Re-driving the pass is not test scaffolding, it is what the supervisor does: a pass
    /// is a set comparison, it runs on an interval, and a dial that did not happen this time is
    /// simply retried. Waiting out a single attempt instead made this depend on the thread pool
    /// being free, which under a parallel run it is not.</para>
    /// </summary>
    private static async Task<bool> WaitFor(Func<bool> condition, IReadOnlyList<Device> reconcile)
    {
        var deadline = DateTime.UtcNow + Patience;
        var nextPass = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;

            if (reconcile.Count > 0 && DateTime.UtcNow >= nextPass)
            {
                nextPass = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                foreach (var device in reconcile) await device.ReconcileAsync();
            }

            await Task.Delay(50);
        }

        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        for (int i = _devices.Count - 1; i >= 0; i--)
        {
            try { await _devices[i].DisposeAsync(); } catch { }
        }
    }

    // ── the phase-one claim ──────────────────────────────────────────────────

    /// <summary>
    /// <b>Three devices, three links, every pair direct.</b>
    ///
    /// <para>Every device talks to every other and nobody forwards anything, so there is no routing
    /// and no loops to prevent. The trade is a complete graph: two devices that cannot reach each
    /// other simply do not sync rather than being bridged by a third.</para>
    /// </summary>
    [Fact]
    public async Task Three_devices_each_hold_a_link_to_both_of_the_others()
    {
        var a = NewDevice("A");
        var b = NewDevice("B");
        var c = NewDevice("C");

        foreach (var device in new[] { a, b, c }) await device.StartAsync();

        a.Pair(b); a.Pair(c);
        b.Pair(a); b.Pair(c);
        c.Pair(a); c.Pair(b);

        foreach (var device in new[] { a, b, c }) await device.ReconcileAsync();

        Assert.True(await WaitFor(() =>
                a.Fabric.ConnectedPeers.Count == 2 &&
                b.Fabric.ConnectedPeers.Count == 2 &&
                c.Fabric.ConnectedPeers.Count == 2,
                new[] { a, b, c }),
            $"A={a.Fabric.ConnectedPeers.Count} B={b.Fabric.ConnectedPeers.Count} C={c.Fabric.ConnectedPeers.Count}");

        Assert.True(a.Fabric.IsConnectedTo(b.Fingerprint));
        Assert.True(a.Fabric.IsConnectedTo(c.Fingerprint));
        Assert.True(b.Fabric.IsConnectedTo(c.Fingerprint));
    }

    /// <summary>
    /// A fan-out is genuinely N encryptions, because the key belongs to the connection. That is
    /// the cost of a paired device being unable to read traffic meant for another pair.
    /// </summary>
    [Fact]
    public async Task A_broadcast_from_one_device_reaches_both_others()
    {
        var a = NewDevice("A");
        var b = NewDevice("B");
        var c = NewDevice("C");

        foreach (var device in new[] { a, b, c }) await device.StartAsync();

        a.Pair(b); a.Pair(c);
        b.Pair(a); b.Pair(c);
        c.Pair(a); c.Pair(b);

        foreach (var device in new[] { a, b, c }) await device.ReconcileAsync();
        Assert.True(await WaitFor(() => a.Fabric.ConnectedPeers.Count == 2, new[] { a, b, c }));

        var body = "the clipboard"u8.ToArray();

        // Asserted on the durable outcome rather than on what one call returned. A link can be
        // replaced by collision resolution in the moment between the check and the send, and the
        // supervisor simply reconciles again - so "did that call reach two" is the wrong question
        // and "did it arrive" is the right one. HANDOFF records the same lesson about dialling.
        Assert.True(await WaitFor(() =>
        {
            _ = a.Fabric.BroadcastAsync(SyncContent.Text, body).GetAwaiter().GetResult();
            lock (b.Received) lock (c.Received) return b.Received.Count >= 1 && c.Received.Count >= 1;
        }, new[] { a, b, c }));

        lock (b.Received) Assert.Equal(body, b.Received[0].Body);
        lock (c.Received) Assert.Equal(body, c.Received[0].Body);
        lock (b.Received) Assert.Equal(RouteKind.WiFi, b.Received[0].Via);
    }

    /// <summary>
    /// A stranger reaches the socket, is refused at the hello, and is gone within the grace rather
    /// than sitting there looking connected.
    /// </summary>
    [Fact]
    public async Task An_unpaired_device_never_becomes_a_route()
    {
        var mine = NewDevice("mine");
        var stranger = NewDevice("stranger");

        await mine.StartAsync();
        await stranger.StartAsync();

        // One-sided: the stranger believes it is paired with us and we have never heard of it.
        stranger.Pair(mine);
        await stranger.ReconcileAsync();

        await Task.Delay(500);

        Assert.Empty(mine.Fabric.ConnectedPeers);
        Assert.Empty(stranger.Fabric.ConnectedPeers);
        Assert.Null(mine.Fabric.LinkTo(stranger.Fingerprint));
    }

    /// <summary>
    /// Both devices listen and both dial, so two can open a socket to each other in the same
    /// moment. Exactly one survives, and both ends agree which without a round trip.
    /// </summary>
    [Fact]
    public async Task Two_devices_dialling_each_other_end_up_with_one_link_each()
    {
        var a = NewDevice("A");
        var b = NewDevice("B");

        await a.StartAsync();
        await b.StartAsync();

        a.Pair(b);
        b.Pair(a);

        // Deliberately together, which is what produces the glare.
        await Task.WhenAll(a.ReconcileAsync(), b.ReconcileAsync());

        Assert.True(await WaitFor(() =>
            a.Fabric.IsConnectedTo(b.Fingerprint) && b.Fabric.IsConnectedTo(a.Fingerprint),
            new[] { a, b }));

        // Settle: the loser is closed asynchronously by whichever end noticed second.
        await Task.Delay(500);

        Assert.Single(a.Fabric.LinkTo(b.Fingerprint)!.LiveRoutes);
        Assert.Single(b.Fabric.LinkTo(a.Fingerprint)!.LiveRoutes);
    }

    // ── a stale address that now belongs to somebody else ────────────────────

    /// <summary>
    /// <b>A dial aimed at one peer, answered by another, must not cost the link to the one that
    /// answered.</b>
    ///
    /// <para>Found on hardware. A phone acting as a hotspot had handed one laptop an address, and
    /// days later handed the same address to a different machine; both records were still in the
    /// registry. Every reconcile pass dialled that address on behalf of the device that had moved
    /// away, the device that now held it answered, and the route was adopted under whoever
    /// answered - which made it a second link of the same kind and dropped the healthy one. The
    /// peer the dial was for still had no route, so the next pass dialled again. A working link
    /// was torn down and rebuilt every fifteen seconds for as long as both records existed.</para>
    ///
    /// <para>The intended peer is remembered across the handshake now, so the mismatch is noticed
    /// and its address forgotten rather than dialled again.</para>
    /// </summary>
    [Fact]
    public async Task A_dial_answered_by_a_different_peer_does_not_drop_the_link_to_that_peer()
    {
        var phone = NewDevice("phone");
        var laptop = NewDevice("laptop");
        await phone.StartAsync();
        await laptop.StartAsync();

        phone.Pair(laptop);
        laptop.Pair(phone);

        Assert.True(await WaitFor(() => phone.Fabric.LinkTo(laptop.Fingerprint)?.Has(RouteKind.WiFi) == true,
                                  new[] { phone, laptop }));

        var settled = phone.Fabric.LinkTo(laptop.Fingerprint)!.LiveRoutes.Single(r => r.Kind == RouteKind.WiFi);

        // The device that moved away, still paired, still holding the address the laptop now has.
        // Never started: nothing is listening as it, which is the whole point.
        var departed = PeerSecurity.CreateEphemeral();
        phone.Security.Peers.Trust(departed.Identity.PublicKey, "departed", $"127.0.0.1:{laptop.Port}");

        // The pass that used to be fatal.
        Assert.True(await WaitFor(() => AddressOf(phone, departed.Identity.Fingerprint) is null or "",
                                  new[] { phone }));

        var after = phone.Fabric.LinkTo(laptop.Fingerprint)!.LiveRoutes.SingleOrDefault(r => r.Kind == RouteKind.WiFi);

        Assert.NotNull(after);
        Assert.Same(settled, after);
        Assert.Equal(RouteState.Established, after!.State);
    }

    private static string? AddressOf(Device device, string fingerprint) =>
        device.Security.Peers.Peers.SingleOrDefault(p =>
            string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))?.LastAddress;
}
