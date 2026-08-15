using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CoreLib.Transport
{
    public class TcpTransportConnection : ITransportConnection
    {
        private const int Port = 45001;
        private TcpClient? _client;
        private TcpListener? _server;
        private NetworkStream? _stream;

        public event EventHandler<PayloadReceivedEventArgs>? PayloadReceived;
        public event EventHandler? ConnectionClosed;
        public event EventHandler? ClientConnected;

        public bool IsConnected => _client?.Connected == true;

        public async Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            _server = new TcpListener(IPAddress.Any, Port);
            _server.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var client = await _server.AcceptTcpClientAsync();
                        _client = client;
                        _stream = _client.GetStream();
                        
                        // Fire event when a device successfully connects to us
                        ClientConnected?.Invoke(this, EventArgs.Empty);

                        _ = ReceiveLoopAsync(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Transport] Listener error: {ex.Message}");
                }
            });
        }

        public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(deviceId, Port, cancellationToken);
            _stream = _client.GetStream();
            
            _ = ReceiveLoopAsync(cancellationToken);
        }

        public async Task SendPayloadAsync(byte[] encryptedPayload, CancellationToken cancellationToken = default)
        {
            if (_stream == null || !IsConnected) throw new InvalidOperationException("Not connected.");

            // 1. Send the length of the payload (4 bytes)
            byte[] lengthPrefix = BitConverter.GetBytes(encryptedPayload.Length);
            await _stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length, cancellationToken);
            
            // 2. Send the actual encrypted payload
            await _stream.WriteAsync(encryptedPayload, 0, encryptedPayload.Length, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) return;

            try
            {
                byte[] lengthBuffer = new byte[4];
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    // 1. Read the length prefix
                    int bytesRead = await _stream.ReadAsync(lengthBuffer, 0, 4, cancellationToken);
                    if (bytesRead == 0) break; // Connection closed

                    int payloadLength = BitConverter.ToInt32(lengthBuffer, 0);
                    byte[] payloadBuffer = new byte[payloadLength];
                    
                    // 2. Read the actual payload
                    int totalRead = 0;
                    while (totalRead < payloadLength)
                    {
                        int read = await _stream.ReadAsync(payloadBuffer, totalRead, payloadLength - totalRead, cancellationToken);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead == payloadLength)
                    {
                        PayloadReceived?.Invoke(this, new PayloadReceivedEventArgs { EncryptedPayload = payloadBuffer });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Transport] Receive error: {ex.Message}");
            }
            finally
            {
                ConnectionClosed?.Invoke(this, EventArgs.Empty);
                await DisconnectAsync();
            }
        }

        public Task DisconnectAsync()
        {
            _stream?.Close();
            _client?.Close();
            // Do NOT stop the server listener, so other devices can reconnect!
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisconnectAsync().Wait();
        }
    }
}
