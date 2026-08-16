using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    public static class NetworkUtil
    {
        /// <summary>
        /// Returns the IPv4 address a peer on the local network can actually reach us on.
        ///
        /// Enumerating <c>Dns.GetHostEntry</c> and taking the first IPv4 - the previous
        /// approach - reliably picks a Hyper-V, WSL, Docker or VPN virtual adapter on a
        /// developer machine, so the address printed into the pairing QR code was one the
        /// phone could never connect to. Interfaces are scored on whether they have a
        /// default gateway and what type they are.
        /// </summary>
        public static string? GetLocalLanAddress()
        {
            string? best = null;
            int bestScore = int.MinValue;

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }

                    bool hasGateway = false;
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address != null &&
                            gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !gw.Address.Equals(IPAddress.Any))
                        {
                            hasGateway = true;
                            break;
                        }
                    }

                    foreach (var unicast in props.UnicastAddresses)
                    {
                        var addr = unicast.Address;
                        if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(addr)) continue;

                        var bytes = addr.GetAddressBytes();
                        // 169.254.x.x means DHCP failed - never advertise it.
                        if (bytes[0] == 169 && bytes[1] == 254) continue;

                        int score = 0;
                        if (hasGateway) score += 100;

                        score += nic.NetworkInterfaceType switch
                        {
                            NetworkInterfaceType.Wireless80211 => 40,
                            NetworkInterfaceType.Ethernet => 30,
                            NetworkInterfaceType.GigabitEthernet => 30,
                            _ => 0
                        };

                        // Virtual adapters advertise themselves fairly clearly by name.
                        string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                        if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") ||
                            desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("docker") ||
                            desc.Contains("vpn") || desc.Contains("tap") || desc.Contains("loopback"))
                        {
                            score -= 200;
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = addr.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Network", "Interface enumeration failed", ex);
            }

            if (best == null) Log.Write("Network", "No usable LAN address found.");
            return best;
        }
    }
}
