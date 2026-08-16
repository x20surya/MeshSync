using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreLib.Transport
{
    public class PayloadReceivedEventArgs : EventArgs
    {
        public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();
    }

    public class PeerIdentifiedEventArgs : EventArgs
    {
        public string DeviceName { get; set; } = string.Empty;
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
        /// Starts listening for incoming payload connections from other devices.
        /// </summary>
        Task StartListeningAsync(CancellationToken cancellationToken = default);

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
