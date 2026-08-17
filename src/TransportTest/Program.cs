using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreLib;
using CoreLib.Transport;

namespace TransportTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Local UDP/TCP Transport & Crypto Test ===\n");

            // Shared Master Password and Salt
            string password = "MySuperSecretPassword123!";
            byte[] salt = Encoding.UTF8.GetBytes("UniqueDeviceSalt_Win11");

            Console.WriteLine("[System] Deriving shared AES-256 keys (Simulating previously paired devices)...");
            byte[] deviceAKey = CryptoEngine.DeriveKey(password, salt);
            byte[] deviceBKey = CryptoEngine.DeriveKey(password, salt);
            
            // Generate public identifiers (hash of public key in real scenario, random bytes here)
            byte[] publicIdA = Encoding.UTF8.GetBytes("DEVICE_A_IDENTIFIER");
            byte[] publicIdB = Encoding.UTF8.GetBytes("DEVICE_B_IDENTIFIER");

            var cts = new CancellationTokenSource();

            // Run Device A and Device B simultaneously
            var taskA = RunDeviceA(deviceAKey, publicIdA, cts.Token);
            var taskB = RunDeviceB(deviceBKey, publicIdB, cts.Token);

            // Wait a few seconds for the simulation to complete, then cancel
            await Task.Delay(5000);
            cts.Cancel();
            Console.WriteLine("\n[System] Simulation finished.");
        }

        static async Task RunDeviceA(byte[] aesKey, byte[] publicId, CancellationToken token)
        {
            try
            {
                var discovery = new TcpDiscoveryService();
                var connection = new TcpTransportConnection();

                // Listening moved to TcpAcceptor when a session-per-peer became possible, so
                // this demo accepts through one and hands the socket to a connection.
                var acceptor = new TcpAcceptor();
                acceptor.Accepted += client => connection.Adopt(client, token);
                await acceptor.StartAsync(token);
                Console.WriteLine("[Device A] 📡 Listening for TCP connections...");

                connection.PayloadReceived += (s, e) =>
                {
                    Console.WriteLine("[Device A] 📦 Received encrypted payload from TCP stream!");
                    try
                    {
                        byte[] decrypted = CryptoEngine.Decrypt(e.EncryptedPayload, aesKey);
                        string text = Encoding.UTF8.GetString(decrypted);
                        Console.WriteLine($"[Device A] ✅ Decrypted Clipboard text: \"{text}\"");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Device A] ❌ Decryption failed: {ex.Message}");
                    }
                };

                // Start advertising presence via UDP
                Console.WriteLine("[Device A] 📢 Advertising presence via UDP Broadcast...");
                await discovery.StartAdvertisingAsync(publicId);

                // Wait until canceled
                await Task.Delay(-1, token);
            }
            catch (TaskCanceledException) { }
        }

        static async Task RunDeviceB(byte[] aesKey, byte[] publicId, CancellationToken token)
        {
            try
            {
                var discovery = new TcpDiscoveryService();
                var connection = new TcpTransportConnection();

                // Device B scans for devices
                Console.WriteLine("[Device B] 🔍 Scanning for UDP Broadcasts...");
                
                bool found = false;
                discovery.DeviceDiscovered += async (s, e) =>
                {
                    if (found) return;
                    found = true;

                    string discoveredId = Encoding.UTF8.GetString(e.PublicIdentifer);
                    Console.WriteLine($"[Device B] 🎯 Discovered {discoveredId} at IP {e.DeviceId}");

                    Console.WriteLine($"[Device B] 🔌 Establishing TCP connection to {e.DeviceId}...");
                    
                    // In UDP localhost testing, the sender might report 127.0.0.1 or the local LAN IP.
                    // To be safe in loopback tests, we connect to 127.0.0.1
                    string targetIp = e.DeviceId == IPAddress.Broadcast.ToString() ? "127.0.0.1" : "127.0.0.1"; // Hardcoded loopback for test

                    await connection.ConnectAsync("127.0.0.1", token);
                    Console.WriteLine("[Device B] 🔒 Connected! Encrypting payload...");

                    string myClipboard = "Hello from Device B! Here is my top secret copied text.";
                    byte[] payload = CryptoEngine.Encrypt(Encoding.UTF8.GetBytes(myClipboard), aesKey);

                    Console.WriteLine("[Device B] 🚀 Sending encrypted AES-256-GCM payload over TCP...");
                    await connection.SendPayloadAsync(payload, token);
                };

                await discovery.StartScanningAsync();

                // Wait until canceled
                await Task.Delay(-1, token);
            }
            catch (TaskCanceledException) { }
        }
    }
}
