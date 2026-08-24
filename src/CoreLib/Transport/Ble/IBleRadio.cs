using System;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Identity;
using CoreLib.Transport.Fabric;

namespace CoreLib.Transport.Ble
{
    /// <summary>
    /// A device seen in a scan round.
    ///
    /// <para><b>RSSI is not decoration.</b> BlueZ keeps a device object for every LE address it has
    /// ever seen, so most of them are ghosts still carrying the service UUID they advertised at the
    /// time, and dialling one connects to an address that stopped existing minutes ago. RSSI is
    /// published only while a device is being seen in the current discovery session, which makes it
    /// the discriminator between a device that is here and one that was.</para>
    /// </summary>
    public readonly record struct BleCandidate
    {
        /// <summary>Whatever the platform uses to reconnect: an address, a BlueZ path, a handle.</summary>
        public required string Address { get; init; }

        /// <summary>
        /// The advertised name, when there is one.
        ///
        /// <para>The only one of the three cooldown keys that survives an LE address rotation and
        /// is known before connecting. It decides who to <em>try</em>, never who is let in, so a
        /// device that spoofs a name gains nothing but its own exclusion.</para>
        /// </summary>
        public string? Name { get; init; }

        public int Rssi { get; init; }

        /// <summary>
        /// The manufacturer-data payload, when the advertisement carried one.
        ///
        /// This is where <see cref="MeshBeacon"/> lives, and checking it is what lets a scanner
        /// tell its own mesh from somebody else's before opening a connection.
        /// </summary>
        public byte[]? Beacon { get; init; }

        /// <summary>True when this device is being seen now rather than remembered.</summary>
        public bool IsPresent { get; init; }
    }

    /// <summary>What to publish, so a peer that cannot advertise can still find this device.</summary>
    public sealed record BleAdvertisement
    {
        /// <summary>The 6-byte mesh beacon, or empty while this device has no mesh key yet.</summary>
        public byte[] Beacon { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Never include the local name.
        ///
        /// A machine name in an advertisement is readable by anyone in the room, which is the leak
        /// the beacon exists to close. Android already sets <c>SetIncludeDeviceName(false)</c>;
        /// the other two have to be checked.
        /// </summary>
        public bool IncludeDeviceName { get; init; }
    }

    /// <summary>
    /// One Bluetooth adapter, and the four things a scheduler needs from it.
    ///
    /// <para><b>What a platform supplies below this line.</b> A scan window, a connect, an
    /// advertisement, and an honest answer about what the radio can do. Nothing about which peer to
    /// reach, when to scan, how long to wait or what to do about a refusal - all of which was
    /// written three times, differently, and was wrong in two of them.</para>
    ///
    /// <para><b>Capability must be reported from what actually started.</b> This box claims it can
    /// advertise and then BlueZ refuses the exported GATT tree. Answering
    /// <see cref="BleCapability.Both"/> anyway makes the arbiter say "you advertise", and the
    /// device then neither advertises nor scans - a deadlock rather than a degraded state.</para>
    /// </summary>
    public interface IBleRadio : IAsyncDisposable
    {
        /// <summary>What the radio can do, taken from what started rather than from what it claimed.</summary>
        BleCapability Capability { get; }

        /// <summary>False when the adapter is off, missing, or the preference forbids it.</summary>
        bool IsAvailable { get; }

        /// <summary>A short description for the health surface: "scanning", "off", "no adapter".</summary>
        string Status { get; }

        Task StartAdvertisingAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default);

        Task StopAdvertisingAsync();

        /// <summary>
        /// Runs one discovery window and returns what was seen.
        ///
        /// <para>A window rather than a subscription, because discovery has to be stopped between
        /// rounds. Started once and left running for the life of the process, an active scan
        /// contends with every live link for the same antenna - which was most of why an
        /// established link felt rough rather than merely duplicated.</para>
        /// </summary>
        Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan window, CancellationToken cancellationToken = default);

        /// <summary>
        /// Opens a link to one candidate, as the central.
        ///
        /// Returns a route in <see cref="RouteState.Connecting"/> or
        /// <see cref="RouteState.Handshaking"/>; the fabric holds it under the handshake deadline
        /// until it says who it is. Null when the connection could not be started at all.
        /// </summary>
        Task<IPeerRoute?> ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken = default);

        /// <summary>
        /// True when this radio is already holding a link to that address.
        ///
        /// <para>Asked before connecting, because a scan cannot tell which peer a candidate is
        /// until a link to it exists - so the same device is found again on every round while its
        /// link is up. Declining that inside the connect looked equivalent and was not: it came
        /// back as an ordinary failure and put a device this radio is <em>successfully talking
        /// to</em> into the five-minute refusal cooldown.</para>
        /// </summary>
        bool HasLinkTo(string address);

        /// <summary>A central subscribed to this device's GATT server.</summary>
        event Action<IPeerRoute>? InboundRoute;
    }
}
