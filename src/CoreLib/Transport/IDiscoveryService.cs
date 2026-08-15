using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CoreLib.Transport
{
    public class DeviceDiscoveredEventArgs : EventArgs
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public byte[] PublicIdentifer { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Abstract interface for finding and advertising to trusted devices over BLE or Wi-Fi Direct.
    /// </summary>
    public interface IDiscoveryService
    {
        event EventHandler<DeviceDiscoveredEventArgs> DeviceDiscovered;

        /// <summary>
        /// Starts advertising the device's presence to nearby trusted nodes.
        /// </summary>
        /// <param name="publicIdentifier">A short hash of the device's public key or pairing ID</param>
        Task StartAdvertisingAsync(byte[] publicIdentifier);

        /// <summary>
        /// Stops advertising.
        /// </summary>
        Task StopAdvertisingAsync();

        /// <summary>
        /// Starts scanning for nearby trusted nodes.
        /// </summary>
        Task StartScanningAsync();

        /// <summary>
        /// Stops scanning.
        /// </summary>
        Task StopScanningAsync();
    }
}
