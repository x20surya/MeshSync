using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreLib.Transport
{
    public class PayloadReceivedEventArgs : EventArgs
    {
        public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Fingerprint of the peer this arrived from, when the transport knows. TCP does, from
        /// the hello; Bluetooth carries no such exchange and leaves it empty, in which case the
        /// receiver identifies the sender by which key authenticates the payload.
        /// </summary>
        public string Fingerprint { get; set; } = string.Empty;
    }

    public class PeerIdentifiedEventArgs : EventArgs
    {
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// The peer's base64 public key, as announced in its hello. Empty from a transport
        /// that carries no hello frame.
        /// </summary>
        public string PublicKey { get; set; } = string.Empty;

        /// <summary>Fingerprint of <see cref="PublicKey"/>, or empty when there is none.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>
        /// Address the peer connected from or was reached at. Recorded so a device can be
        /// dialled again after a DHCP lease moves it, and never used to decide identity.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// What the peer calls the mesh. Adopted only by a device that has no name of its own,
        /// so it cannot ping-pong between two devices that disagree.
        /// </summary>
        public string MeshName { get; set; } = string.Empty;

        /// <summary>
        /// Which halves of a GATT link the peer's radio can take.
        ///
        /// <para>Announced rather than assumed since wire version 4. Every call site used to pass
        /// <c>BleCapability.Both</c> for the peer, which is documented as "the optimistic reading"
        /// and is the reason two devices that both cannot advertise sit waiting for each other -
        /// it resolves by luck, and only once a link already exists.</para>
        ///
        /// <para>A peer that predates version 4 sends nothing and is read as
        /// <see cref="BleCapability.Both"/>, which is exactly the behaviour it had before.</para>
        /// </summary>
        public BleCapability Capability { get; set; } = BleCapability.Both;
    }

    /// <summary>
    /// Abstract interface for a secure socket connection between two trusted mesh devices.
    /// </summary>
    public interface ITransportConnection : IDisposable
    {
        event EventHandler<PayloadReceivedEventArgs> PayloadReceived;
        event EventHandler ConnectionClosed;

        bool IsConnected { get; }

        /// <summary>
        /// Connects to a specific device discovered by IDiscoveryService.
        /// </summary>
        Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends an encrypted byte array to the connected device.
        /// </summary>
        Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disconnects the socket.
        /// </summary>
        Task DisconnectAsync();
    }
}
