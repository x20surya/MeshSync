using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoreLib.Diagnostics;

namespace CoreLib.Transport
{
    /// <summary>
    /// Accepts incoming connections and hands each one on, without holding any itself.
    ///
    /// <para>Split out of <see cref="TcpTransportConnection"/>, which used to own both the
    /// listener and the single live session. That pairing is what made the transport strictly
    /// one-to-one: a second peer connecting replaced the first rather than joining it, because
    /// there was only ever one session field to put it in. Separating the two is what lets a
    /// session exist per peer.</para>
    ///
    /// <para>It also makes the roles symmetric. Both devices run one of these <em>and</em> dial
    /// out, so neither is a server by nature - which device ends up listening for which link is
    /// settled per connection rather than baked into the platform.</para>
    /// </summary>
    public sealed class TcpAcceptor : IDisposable
    {
        private readonly object _gate = new();
        private readonly int _port;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        /// <summary>
        /// Raised for every accepted socket. The handler takes ownership: if it does not adopt
        /// the client, it must dispose it.
        /// </summary>
        public event Action<TcpClient>? Accepted;

        public TcpAcceptor(int port = TcpTransportConnection.DefaultPort) => _port = port;

        /// <summary>The port actually bound, which differs from the requested one when it was 0.</summary>
        public int Port
        {
            get
            {
                lock (_gate)
                {
                    return _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : _port;
                }
            }
        }

        public bool IsListening
        {
            get { lock (_gate) return _listener != null; }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            TcpListener listener;
            CancellationTokenSource cts;

            lock (_gate)
            {
                if (_listener != null) return Task.CompletedTask;

                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                listener = new TcpListener(IPAddress.Any, _port);
                listener.Start();

                _cts = cts;
                _listener = listener;
            }

            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
            Log.Write("Transport", $"Listening on 0.0.0.0:{Port}");
            return Task.CompletedTask;
        }

        public void Stop()
        {
            TcpListener? listener;
            CancellationTokenSource? cts;

            lock (_gate)
            {
                listener = _listener;
                cts = _cts;
                _listener = null;
                _cts = null;
            }

            if (listener == null) return;

            try { cts?.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            cts?.Dispose();

            Log.Write("Transport", "Stopped listening.");
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Write("Transport", "Accept failed", ex);
                    // Transient accept errors (a peer that vanished mid-handshake, say) must
                    // not kill the listener, but must not spin hot either.
                    try { await Task.Delay(250, token).ConfigureAwait(false); } catch { break; }
                    continue;
                }

                try
                {
                    var handler = Accepted;
                    if (handler == null)
                    {
                        // Nobody to take it. Closing beats leaking the socket and leaving the
                        // peer believing it is connected.
                        client.Dispose();
                        continue;
                    }

                    handler(client);
                }
                catch (Exception ex)
                {
                    Log.Write("Transport", "Handling an accepted connection threw", ex);
                    try { client.Dispose(); } catch { }
                }
            }

            Log.Write("Transport", "Accept loop stopped.");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Stop();
            Accepted = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpAcceptor));
        }
    }
}
