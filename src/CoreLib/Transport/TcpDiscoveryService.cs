using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreLib.Transport
{
    /// <summary>
    /// A local-network implementation of IDiscoveryService using UDP Broadcasts.
    /// This is used for Phase 3 testing before implementing BLE.
    /// </summary>
    public class TcpDiscoveryService : IDiscoveryService
    {
        private const int DiscoveryPort = 45000;
        private UdpClient? _broadcaster;
        private UdpClient? _listener;
        private CancellationTokenSource? _cts;

        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

        public async Task StartAdvertisingAsync(byte[] publicIdentifier)
        {
            _broadcaster = new UdpClient();
            _broadcaster.EnableBroadcast = true;
            _cts = new CancellationTokenSource();

            // The payload we broadcast: "SYNC_ME|Base64Identifier"
            string idString = Convert.ToBase64String(publicIdentifier);
            byte[] broadcastBytes = Encoding.UTF8.GetBytes($"SYNC_ME|{idString}");
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        await _broadcaster.SendAsync(broadcastBytes, broadcastBytes.Length, endPoint);
                        await Task.Delay(2000, _cts.Token); // Ping every 2 seconds
                    }
                }
                catch (TaskCanceledException) { /* Expected */ }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Discovery] Broadcast error: {ex.Message}");
                }
            });
        }

        public Task StopAdvertisingAsync()
        {
            _cts?.Cancel();
            _broadcaster?.Close();
            return Task.CompletedTask;
        }

        public Task StartScanningAsync()
        {
            _listener = new UdpClient(DiscoveryPort);
            _listener.EnableBroadcast = true;
            _cts ??= new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var result = await _listener.ReceiveAsync();
                        string message = Encoding.UTF8.GetString(result.Buffer);

                        if (message.StartsWith("SYNC_ME|"))
                        {
                            string b64Id = message.Substring(8);
                            byte[] idBytes = Convert.FromBase64String(b64Id);

                            DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs
                            {
                                DeviceId = result.RemoteEndPoint.Address.ToString(), // The IP address
                                DeviceName = "Local TCP Device",
                                PublicIdentifer = idBytes
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_cts.Token.IsCancellationRequested)
                        Console.WriteLine($"[Discovery] Scan error: {ex.Message}");
                }
            });

            return Task.CompletedTask;
        }

        public Task StopScanningAsync()
        {
            _cts?.Cancel();
            _listener?.Close();
            return Task.CompletedTask;
        }
    }
}
