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

                // There is no advertising step any more. UDP discovery was built on both sides
                // and consumed by neither, and address handover over an existing link does the
                // job better - no multicast, and it works on networks with client isolation.
                _ = publicId;

                // Wait until canceled
                await Task.Delay(-1, token);
            }
            catch (TaskCanceledException) { }
        }

        static async Task RunDeviceB(byte[] aesKey, byte[] publicId, CancellationToken token)
        {
            try
            {
                var connection = new TcpTransportConnection();

                // Dialled straight at loopback. The discovery half of this demo went with
                // TcpDiscoveryService; in the real mesh a peer's address arrives over whichever
                // link is already up, as content type Address.
                _ = publicId;
                await Task.Delay(500, token);

                Console.WriteLine("[Device B] 🔌 Establishing TCP connection to 127.0.0.1...");
                await connection.ConnectAsync("127.0.0.1", token);
                Console.WriteLine("[Device B] 🔒 Connected! Encrypting payload...");

                string myClipboard = "Hello from Device B! Here is my top secret copied text.";
                byte[] payload = CryptoEngine.Encrypt(Encoding.UTF8.GetBytes(myClipboard), aesKey);

                Console.WriteLine("[Device B] 🚀 Sending encrypted AES-256-GCM payload over TCP...");
                await connection.SendPayloadAsync(payload, token);

                // Wait until canceled
                await Task.Delay(-1, token);
            }
            catch (TaskCanceledException) { }
        }
    }
}
