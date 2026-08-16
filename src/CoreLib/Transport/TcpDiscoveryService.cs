using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>
    /// Local-network peer discovery over UDP broadcast.
    /// Advertising and scanning are independently cancellable and each announcement
    /// is sent per-interface, because a single send to 255.255.255.255 only leaves
    /// one adapter on machines that also have VPN, Hyper-V, WSL or Docker interfaces.
    /// </summary>
    public sealed class TcpDiscoveryService : IDiscoveryService, IDisposable
    {
        private const int DiscoveryPort = 45000;
        private const string Preamble = "SYNC_ME|";
        private static readonly TimeSpan AdvertiseInterval = TimeSpan.FromSeconds(2);

        /// <summary>How long a peer stays "already seen" before it is reported again.</summary>
        private static readonly TimeSpan RediscoverAfter = TimeSpan.FromSeconds(30);

        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.Ordinal);

        private UdpClient? _broadcaster;
        private UdpClient? _listener;
        private CancellationTokenSource? _advertiseCts;
        private CancellationTokenSource? _scanCts;
        private bool _disposed;

        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

        public Task StartAdvertisingAsync(byte[] publicIdentifier)
        {
            if (publicIdentifier == null) throw new ArgumentNullException(nameof(publicIdentifier));
            ThrowIfDisposed();

            UdpClient broadcaster;
            CancellationTokenSource cts;

            lock (_gate)
            {
                if (_advertiseCts != null) return Task.CompletedTask; // already advertising

                broadcaster = new UdpClient { EnableBroadcast = true };
                cts = new CancellationTokenSource();
                _broadcaster = broadcaster;
                _advertiseCts = cts;
            }

            byte[] message = Encoding.UTF8.GetBytes(Preamble + Convert.ToBase64String(publicIdentifier));
            _ = Task.Run(() => AdvertiseLoopAsync(broadcaster, message, cts.Token));
            Log.Write("Discovery", $"Advertising on UDP {DiscoveryPort}.");
            return Task.CompletedTask;
        }

        private async Task AdvertiseLoopAsync(UdpClient broadcaster, byte[] message, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var target in GetBroadcastTargets())
                    {
                        try
                        {
                            await broadcaster.SendAsync(message, message.Length, target).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // One dead adapter (disconnected VPN, disabled NIC) must not stop the rest.
                            Log.Write("Discovery", $"Broadcast to {target} failed", ex);
                        }
                    }

                    await Task.Delay(AdvertiseInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (ObjectDisposedException) { /* expected on teardown */ }
            catch (Exception ex)
            {
                Log.Write("Discovery", "Advertise loop error", ex);
            }
        }

        /// <summary>
        /// Global broadcast plus each interface's directed broadcast address, so the
        /// announcement reaches the Wi-Fi LAN and not just whichever adapter the
        /// routing table happens to prefer.
        /// </summary>
        private static IEnumerable<IPEndPoint> GetBroadcastTargets()
        {
            var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, DiscoveryPort) };

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (unicast.IPv4Mask == null) continue;

                        var addr = unicast.Address.GetAddressBytes();
                        var mask = unicast.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4) continue;

                        var directed = new byte[4];
                        for (int i = 0; i < 4; i++) directed[i] = (byte)(addr[i] | ~mask[i]);

                        targets.Add(new IPEndPoint(new IPAddress(directed), DiscoveryPort));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Discovery", "Enumerating broadcast targets failed", ex);
            }

            return targets;
        }

        public Task StopAdvertisingAsync()
        {
            CancellationTokenSource? cts;
            UdpClient? broadcaster;

            lock (_gate)
            {
                cts = _advertiseCts;
                broadcaster = _broadcaster;
                _advertiseCts = null;
                _broadcaster = null;
            }

            try { cts?.Cancel(); } catch { }
            cts?.Dispose();
            broadcaster?.Dispose();
            return Task.CompletedTask;
        }

        public Task StartScanningAsync()
        {
            ThrowIfDisposed();

            UdpClient listener;
            CancellationTokenSource cts;

            lock (_gate)
            {
                if (_scanCts != null) return Task.CompletedTask; // already scanning

                // ReuseAddress lets the daemon scan on a port another local process
                // (or a second instance) already holds, instead of throwing at startup.
                listener = new UdpClient();
                listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                listener.EnableBroadcast = true;

                cts = new CancellationTokenSource();
                _listener = listener;
                _scanCts = cts;
            }

            _ = Task.Run(() => ScanLoopAsync(listener, cts.Token));
            Log.Write("Discovery", $"Scanning UDP {DiscoveryPort}.");
            return Task.CompletedTask;
        }

        private async Task ScanLoopAsync(UdpClient listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await listener.ReceiveAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException ex)
                    {
                        // A malformed datagram or an ICMP port-unreachable must not end the scan.
                        Log.Write("Discovery", "Receive failed", ex);
                        continue;
                    }

                    string message;
                    try { message = Encoding.UTF8.GetString(result.Buffer); }
                    catch { continue; }

                    if (!message.StartsWith(Preamble, StringComparison.Ordinal)) continue;

                    byte[] idBytes;
                    try { idBytes = Convert.FromBase64String(message.Substring(Preamble.Length)); }
                    catch (FormatException) { continue; }

                    string deviceId = result.RemoteEndPoint.Address.ToString();

                    // Peers announce every 2s forever. Without this gate every consumer
                    // that reacts to discovery would be re-triggered 30 times a minute.
                    var now = DateTime.UtcNow;
                    if (_lastSeen.TryGetValue(deviceId, out var seen) && now - seen < RediscoverAfter) continue;
                    _lastSeen[deviceId] = now;

                    try
                    {
                        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs
                        {
                            DeviceId = deviceId,
                            DeviceName = "Local TCP Device",
                            PublicIdentifer = idBytes
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Discovery", "DeviceDiscovered handler threw", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Discovery", "Scan loop error", ex);
            }
        }

        public Task StopScanningAsync()
        {
            CancellationTokenSource? cts;
            UdpClient? listener;

            lock (_gate)
            {
                cts = _scanCts;
                listener = _listener;
                _scanCts = null;
                _listener = null;
            }

            try { cts?.Cancel(); } catch { }
            cts?.Dispose();
            listener?.Dispose();
            _lastSeen.Clear();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            StopAdvertisingAsync().GetAwaiter().GetResult();
            StopScanningAsync().GetAwaiter().GetResult();
            DeviceDiscovered = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpDiscoveryService));
        }
    }
}
