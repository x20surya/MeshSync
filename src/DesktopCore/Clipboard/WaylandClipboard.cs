using System.Text;
using CoreLib.Diagnostics;

namespace DesktopCore.Clipboard;

/// <summary>
/// The clipboard, spoken to the compositor directly over <c>ext-data-control</c>.
///
/// <para><b>What this buys.</b> An ordinary Wayland client may only read the clipboard while it
/// has focus, which makes a background clipboard watcher impossible. <c>ext-data-control</c> is
/// the protocol that lifts that restriction, and it is what <c>wl-clipboard</c> uses. Speaking it
/// here means the feature works with nothing installed, and means the watcher is told about
/// changes rather than polling for them.</para>
///
/// <para><b>Why it is not universal.</b> The compositor has to offer the protocol. KDE Plasma 6.6
/// and wlroots do; some do not, and X11 sessions never will. <see cref="TryCreate"/> returns null
/// in that case and the caller falls back to a command-line helper.</para>
/// </summary>
public sealed class WaylandClipboard : IClipboardBridge, IDisposable
{
    // The one mime type worth asking for first. The rest are what older senders offer.
    private static readonly string[] TextMimes =
        ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING", "TEXT"];

    private const int MaxBytes = 4 * 1024 * 1024;

    private const uint DisplayId = 1;
    private const uint RegistryId = 2;
    private const uint SyncCallbackId = 3;
    private const uint ManagerId = 4;
    private const uint SeatId = 5;
    private const uint DeviceId = 6;

    /// <summary>
    /// The next client object id to hand out, counting up from just after the fixed ones.
    ///
    /// <para><b>Why it only ever counts up.</b> libwayland keeps client objects in a dense array
    /// and refuses any id that would leave a gap, so ids cannot be picked freely. Counting
    /// upwards by one is always dense. Destroying a source to reclaim its id is what a tidier
    /// implementation would do and is exactly what must not happen here: the compositor reissues
    /// freed ids immediately, and a destroy racing a reissue tears down an object that now
    /// belongs to something else. One id per clipboard write is a cost worth paying for that.
    /// </para>
    /// </summary>
    private uint _nextSourceId = 7;

    private readonly WaylandTransport _wire;
    private readonly Dictionary<uint, byte[]> _sources = new();
    private uint _currentOffer;

    private readonly object _gate = new();
    private readonly Dictionary<uint, List<string>> _offerMimes = new();
    private string? _current;
    private byte[]? _serving;
    private uint _sourceId;

    private Thread? _pump;
    private volatile bool _stopping;
    private bool _disposed;

    public string Name => "wayland";
    public bool IsAvailable => true;
    public bool SupportsWatching => true;

    /// <summary>The selection changed. Never raised for this process's own writes.</summary>
    public event Action<string>? SelectionChanged;

    private WaylandClipboard(WaylandTransport wire) => _wire = wire;

    /// <summary>
    /// Connects and binds, or returns null when this session cannot offer the protocol - no
    /// Wayland at all, or a compositor without <c>ext-data-control</c>.
    /// </summary>
    public static WaylandClipboard? TryCreate()
    {
        if (!OperatingSystem.IsLinux()) return null;

        var wire = WaylandTransport.TryConnect();
        if (wire == null) return null;

        var clipboard = new WaylandClipboard(wire);

        try
        {
            if (!clipboard.Bind()) { wire.Dispose(); return null; }
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard", "The Wayland handshake failed", ex);
            wire.Dispose();
            return null;
        }

        clipboard.Start();
        return clipboard;
    }

    /// <summary>
    /// Asks for the globals, then binds the two we need.
    ///
    /// The roundtrip matters: globals arrive as a burst of events after <c>get_registry</c> and
    /// there is no "that is all of them" among them. <c>wl_display.sync</c> is the way to know
    /// the burst is over - its callback cannot arrive before everything queued ahead of it.
    /// </summary>
    private bool Bind()
    {
        _wire.Send(DisplayId, 1, WaylandTransport.UInt(RegistryId));      // get_registry
        _wire.Send(DisplayId, 0, WaylandTransport.UInt(SyncCallbackId));  // sync

        uint managerName = 0, managerVersion = 0, seatName = 0, seatVersion = 0;

        while (_wire.TryReadEvent(out uint id, out ushort opcode, out byte[] body))
        {
            if (id == RegistryId && opcode == 0)   // global
            {
                int at = 0;
                uint name = WaylandTransport.ReadUInt(body, ref at);
                string iface = WaylandTransport.ReadString(body, ref at);
                uint version = WaylandTransport.ReadUInt(body, ref at);

                if (iface == "ext_data_control_manager_v1") { managerName = name; managerVersion = version; }
                else if (iface == "wl_seat" && seatName == 0) { seatName = name; seatVersion = Math.Min(version, 7); }
            }
            else if (id == SyncCallbackId && opcode == 0)   // done
            {
                break;
            }
            else if (id == DisplayId && opcode == 0)        // error
            {
                Log.Write("Clipboard", "The compositor rejected the connection.");
                return false;
            }
        }

        if (managerName == 0)
        {
            Log.Write("Clipboard", "This compositor does not offer ext-data-control; falling back.");
            return false;
        }

        if (seatName == 0) return false;

        Bind(managerName, "ext_data_control_manager_v1", managerVersion, ManagerId);
        Bind(seatName, "wl_seat", seatVersion, SeatId);

        // manager.get_data_device(new_id, seat)
        _wire.Send(ManagerId, 1, WaylandTransport.Concat(
            WaylandTransport.UInt(DeviceId), WaylandTransport.UInt(SeatId)));

        Log.Write("Clipboard", "Watching the clipboard over ext-data-control, with no helper needed.");
        return true;
    }

    private void Bind(uint name, string iface, uint version, uint newId) =>
        _wire.Send(RegistryId, 0, WaylandTransport.Concat(
            WaylandTransport.UInt(name),
            WaylandTransport.String(iface),
            WaylandTransport.UInt(version),
            WaylandTransport.UInt(newId)));

    private void Start()
    {
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "wayland-clipboard",
        };
        _pump.Start();
    }

    /// <summary>The event loop. Everything the compositor says arrives here.</summary>
    private void Pump()
    {
        try
        {
            while (!_stopping && _wire.TryReadEvent(out uint id, out ushort opcode, out byte[] body))
            {
                if (id == DisplayId && opcode == 0)
                {
                    // wl_display.error: the object at fault, a code, and something readable.
                    // Silently dropping this is how a rejected request looks like nothing
                    // happening at all.
                    int at = 0;
                    uint faulty = WaylandTransport.ReadUInt(body, ref at);
                    uint code = WaylandTransport.ReadUInt(body, ref at);
                    string message = WaylandTransport.ReadString(body, ref at);

                    Log.Write("Clipboard", $"The compositor refused object {faulty} (code {code}): {message}");
                    _stopping = true;
                    break;
                }

                bool isOffer, isSource;
                lock (_gate)
                {
                    isOffer = _offerMimes.ContainsKey(id);
                    isSource = _sources.ContainsKey(id);
                }

                if (id == DeviceId) HandleDevice(opcode, body);
                else if (isSource) HandleSource(id, opcode, body);
                else if (isOffer) HandleOffer(id, opcode, body);
            }
        }
        catch (Exception ex)
        {
            // Worth saying loudly. A pump that dies looks exactly like a clipboard that has
            // stopped changing, which is the least diagnosable failure this component has.
            if (!_stopping) Log.Write("Clipboard", "The Wayland clipboard event loop stopped", ex);
        }
    }

    private void HandleDevice(ushort opcode, byte[] body)
    {
        switch (opcode)
        {
            case 0:   // data_offer(new_id)
            {
                int at = 0;
                uint offer = WaylandTransport.ReadUInt(body, ref at);
                lock (_gate) _offerMimes[offer] = new List<string>();
                break;
            }

            case 1:   // selection(offer)
            {
                int at = 0;
                uint offer = WaylandTransport.ReadUInt(body, ref at);

                lock (_gate) _currentOffer = offer;

                if (offer == 0)
                {
                    // The selection was cleared, which is a real state and not a change worth
                    // syncing - there is nothing to send.
                    lock (_gate) _current = null;
                    break;
                }

                // Read on its own thread: the compositor only writes into the pipe once it has
                // processed the receive request, and it cannot do that while this loop is
                // blocked waiting for the bytes.
                _ = Task.Run(() => ReadOffer(offer));
                break;
            }

            case 2:   // finished
                _stopping = true;
                break;
        }
    }

    private void HandleOffer(uint offer, ushort opcode, byte[] body)
    {
        if (opcode != 0) return;   // offer(mime_type)

        int at = 0;
        string mime = WaylandTransport.ReadString(body, ref at);

        lock (_gate)
        {
            if (_offerMimes.TryGetValue(offer, out var list)) list.Add(mime);
        }
    }

    private void HandleSource(uint source, ushort opcode, byte[] body)
    {
        switch (opcode)
        {
            case 0:   // send(mime_type, fd)
            {
                int at = 0;
                WaylandTransport.ReadString(body, ref at);   // the mime is not consulted: every
                                                            // type we offer carries the same text
                int fd = _wire.TakeFd();
                if (fd < 0) return;

                // Answered from the source that was asked, not from the newest one. An older
                // source is still live until the compositor cancels it, and a paste against it
                // must get what it was offered.
                byte[]? payload;
                lock (_gate) _sources.TryGetValue(source, out payload);

                // Written on another thread, because a reader that is slow to drain the pipe
                // would otherwise stall every other clipboard event.
                _ = Task.Run(() =>
                {
                    try { if (payload != null) WaylandTransport.WriteAll(fd, payload); }
                    finally { WaylandTransport.CloseFd(fd); }
                });
                break;
            }

            case 1:   // cancelled - something else owns the clipboard now
                // Deliberately not destroyed. See the note on _nextSourceId: reclaiming the id
                // races the compositor reissuing it.
                lock (_gate)
                {
                    _sources.Remove(source);
                    if (_sourceId == source) { _serving = null; _sourceId = 0; }
                }
                break;
        }
    }

    /// <summary>Pulls the bytes behind one offer through a pipe, then hands them on.</summary>
    private void ReadOffer(uint offer)
    {
        List<string>? mimes;
        lock (_gate) _offerMimes.TryGetValue(offer, out mimes);

        string? mime = TextMimes.FirstOrDefault(m => mimes?.Contains(m) == true);
        if (mime == null)
        {
            // An image or a file list. Not something this bridge carries.
            lock (_gate) _offerMimes.Remove(offer);
            return;
        }

        var (readFd, writeFd) = WaylandTransport.CreatePipe();
        if (readFd < 0) return;

        try
        {
            _wire.Send(offer, 0, WaylandTransport.String(mime), writeFd);   // receive(mime, fd)

            // Ours must go before the read, or the pipe never reaches end-of-file: the kernel
            // keeps it open while any copy of the write end survives, including this one.
            WaylandTransport.CloseFd(writeFd);

            byte[] bytes = WaylandTransport.ReadAll(readFd, MaxBytes);
            string text = Encoding.UTF8.GetString(bytes);

            bool changed;
            lock (_gate)
            {
                // A selection that has already moved on must not overwrite the newer one. The
                // read is asynchronous and the compositor reuses offer ids, so "the offer I was
                // reading is still the selection" is the only safe test.
                if (_currentOffer != offer) return;

                changed = text.Length > 0 && text != _current;
                if (text.Length > 0) _current = text;
                _offerMimes.Remove(offer);
            }

            if (changed) SelectionChanged?.Invoke(text);
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard", "Could not read the selection", ex);
        }
        finally
        {
            WaylandTransport.CloseFd(readFd);
        }
    }

    public Task<string?> GetTextAsync(CancellationToken cancellationToken)
    {
        // Answered from what the compositor last announced rather than by asking: the events
        // arrive whether or not anyone is looking, so the cached value is always current and
        // costs no round trip.
        lock (_gate) return Task.FromResult(_current);
    }

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        if (_disposed) return Task.FromResult(false);

        try
        {
            uint source;
            byte[] payload = Encoding.UTF8.GetBytes(text);

            lock (_gate)
            {
                source = _nextSourceId++;
                _sources[source] = payload;
                _serving = payload;
                _sourceId = source;

                // Recorded now rather than when the compositor echoes the selection back, so
                // this device does not read its own write and treat it as somebody's copy.
                _current = text;
            }

            _wire.Send(ManagerId, 0, WaylandTransport.UInt(source));      // create_data_source

            foreach (string mime in TextMimes)
            {
                _wire.Send(source, 0, WaylandTransport.String(mime));     // source.offer(mime)
            }

            _wire.Send(DeviceId, 0, WaylandTransport.UInt(source));       // device.set_selection

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard", "Could not put text on the clipboard", ex);
            return Task.FromResult(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;

        SelectionChanged = null;
        _wire.Dispose();
    }
}
