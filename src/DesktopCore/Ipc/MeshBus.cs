using CoreLib.Diagnostics;
using Tmds.DBus.Protocol;

namespace DesktopCore.Ipc;

/// <summary>
/// Publishes the running device on the session bus as <c>dev.meshsync.Daemon</c>.
///
/// <para><b>Why this is in DesktopCore rather than in the shell.</b> The headless daemon has to
/// publish the same interface: a machine with a panel but no window still wants a widget and a
/// tray icon, and that is the whole reason the Linux head was built as a core plus two front
/// ends. Nothing here touches Avalonia, and nothing here is platform-specific beyond the session
/// bus itself.</para>
///
/// <para><b>A bus that is not there is not an error.</b> A user session without D-Bus is unusual
/// and entirely workable - the clipboard, the links and the pairing all carry on. This reports
/// it once and stands aside, exactly as the Bluetooth tier does on a machine with no radio.</para>
///
/// <para><b>Only one device per session can own the name.</b> Two daemons on one machine is a
/// supported arrangement - it is how the mesh is exercised without a second computer - so losing
/// the race is expected rather than fatal. The second device serves its objects on its unique
/// name and simply is not the one a widget finds.</para>
/// </summary>
public sealed class MeshBus : IDisposable
{
    /// <summary>Long enough to collapse a burst, short enough that a panel feels live.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly Daemon _daemon;
    private readonly DBusConnection _connection;
    private readonly MeshBusObject _objects;

    private Dictionary<string, object> _lastDaemon;
    private Dictionary<string, Dictionary<string, object>> _lastChildren;

    private readonly Lock _gate = new();
    private Timer? _coalesce;
    private bool _disposed;

    /// <summary>True when the well-known name is held, so a widget can find this device.</summary>
    public bool IsPrimary { get; private set; }

    private MeshBus(Daemon daemon, DBusConnection connection, MeshBusObject objects)
    {
        _daemon = daemon;
        _connection = connection;
        _objects = objects;
        _lastDaemon = objects.DaemonProperties();
        _lastChildren = SnapshotChildren();
    }

    /// <summary>
    /// Connects, exports the tree and takes the name, or returns null when there is no session
    /// bus to do it on.
    /// </summary>
    /// <param name="show">Raises the window on a named page, where there is a window.</param>
    /// <param name="quit">Stops the app, for the tray menu's Quit.</param>
    public static async Task<MeshBus?> TryStartAsync(Daemon daemon,
                                                     Action<string>? show = null,
                                                     Action? quit = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;
        if (string.IsNullOrEmpty(DBusAddress.Session)) { Log.Write("Bus", "No session bus; not publishing."); return null; }

        DBusConnection? connection = null;

        try
        {
            connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);

            var objects = new MeshBusObject(daemon, show, quit);
            connection.AddMethodHandler(objects);

            var bus = new MeshBus(daemon, connection, objects);

            // TryRequestNameAsync never queues - it answers false rather than waiting - which is
            // what is wanted here. Queuing would make this device silently become the one every
            // widget talks to the moment the other exits, long after anybody asked for that.
            bus.IsPrimary = await connection
                .TryRequestNameAsync(BusNames.Service, RequestNameOptions.None)
                .ConfigureAwait(false);

            Log.Write("Bus", bus.IsPrimary
                ? $"Publishing on the session bus as {BusNames.Service}{BusNames.Root}."
                : $"{BusNames.Service} is already owned by another device on this machine; serving on {connection.UniqueName} only.");

            bus.Subscribe();
            return bus;
        }
        catch (Exception ex)
        {
            Log.Write("Bus", "Could not publish on the session bus", ex);
            connection?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Hands over to an instance that is already running, if there is one.
    ///
    /// <para>Returns true when another Mesh Sync took the request, meaning this process should
    /// stop rather than start a second device that would fail to bind port 45001 and then sit
    /// there looking like it was working.</para>
    ///
    /// <para><b>The owner is checked before anything is called.</b> Calling a method on a name
    /// nobody owns is exactly what triggers D-Bus activation, and with
    /// <c>dev.meshsync.Daemon.service</c> installed that would start a fresh Mesh Sync - which
    /// would then find no owner either, and start another. <c>GetNameOwner</c> does not
    /// activate, which is the whole reason it is asked first.</para>
    /// </summary>
    public static async Task<bool> TryHandOverAsync(string page = "home")
    {
        if (string.IsNullOrEmpty(DBusAddress.Session)) return false;

        DBusConnection? connection = null;

        try
        {
            connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);

            var probe = connection.GetMessageWriter();
            probe.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus", "GetNameOwner", "s");
            probe.WriteString(BusNames.Service);

            string owner;
            try
            {
                owner = await connection.CallMethodAsync(probe.CreateMessage(),
                    static (Message message, object? _) => message.GetBodyReader().ReadString(), null)
                    .ConfigureAwait(false);
            }
            catch
            {
                return false;   // NameHasNoOwner: nothing is running, so this instance is it.
            }

            if (string.IsNullOrEmpty(owner)) return false;

            var show = connection.GetMessageWriter();
            show.WriteMethodCallHeader(BusNames.Service, BusNames.Root, BusNames.DaemonInterface, "Show", "s");
            show.WriteString(page);

            await connection.CallMethodAsync(show.CreateMessage()).ConfigureAwait(false);

            Log.Write("Bus", "Mesh Sync is already running; raised its window and stopped.");
            return true;
        }
        catch (Exception ex)
        {
            // Better to start a second one than to refuse to start at all over a bus hiccup.
            Log.Write("Bus", "Could not check for a running instance", ex);
            return false;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    // ──────────────────────────────── change tracking

    /// <summary>
    /// Every event that can move something a client is showing.
    ///
    /// <para>They all land in the same place and the same diff decides what actually changed,
    /// rather than each one announcing what it thinks it touched. That is what stops a dial
    /// round emitting a property change on every tick when nothing about it is different.</para>
    /// </summary>
    private void Subscribe()
    {
        _daemon.Links.Changed += Nudge;
        _daemon.Security.Peers.Changed += Nudge;
        _daemon.Transports.Changed += _ => Nudge();
        _daemon.Ringer.StateChanged += _ => Nudge();
        _daemon.Security.PairingRequested += _ => Nudge();
        _daemon.TrayIconVisibleChanged += _ => Nudge();

        _daemon.Notifications.Changed += () => { Nudge(); Signal("NotificationsChanged"); };
        _daemon.Activity.Changed += (_, _) => { Nudge(); Signal("ActivityChanged"); };
        _daemon.Files.FileReceived += _ => Signal("FilesChanged");
    }

    private void Nudge()
    {
        if (_disposed) return;

        lock (_gate)
        {
            _coalesce ??= new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
            _coalesce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Works out what actually changed and says only that.
    ///
    /// <para>Three announcements come out of here: the root's own properties, and devices and
    /// pairing requests arriving or leaving. A client that binds a list to the object manager
    /// then never has to poll, and a pairing request appearing is the signal that turns a tray
    /// icon to NeedsAttention.</para>
    /// </summary>
    private void Publish()
    {
        if (_disposed) return;

        try
        {
            var daemon = _objects.DaemonProperties();
            var changed = daemon.Where(pair =>
                !_lastDaemon.TryGetValue(pair.Key, out object? was) || !Equals(was, pair.Value)).ToList();

            if (changed.Count > 0)
            {
                EmitPropertiesChanged(BusNames.Root, BusNames.DaemonInterface, changed);
                _lastDaemon = daemon;
            }

            var children = SnapshotChildren();

            foreach (var (path, values) in children)
            {
                if (!_lastChildren.TryGetValue(path, out var was))
                {
                    EmitInterfacesAdded(path, InterfaceFor(path), values);
                    continue;
                }

                var moved = values.Where(pair =>
                    !was.TryGetValue(pair.Key, out object? old) || !Equals(old, pair.Value)).ToList();

                if (moved.Count > 0) EmitPropertiesChanged(path, InterfaceFor(path), moved);
            }

            foreach (string path in _lastChildren.Keys.Where(p => !children.ContainsKey(p)))
                EmitInterfacesRemoved(path, InterfaceFor(path));

            _lastChildren = children;
        }
        catch (Exception ex)
        {
            Log.Write("Bus", "Could not publish a change", ex);
        }
    }

    private Dictionary<string, Dictionary<string, object>> SnapshotChildren()
    {
        var all = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

        foreach (var peer in _daemon.Security.Peers.Peers)
        {
            var values = _objects.DeviceProperties(peer.Fingerprint);
            if (values != null) all[BusNames.DevicePath(peer.Fingerprint)] = values;
        }

        foreach (var pending in _daemon.Pending)
        {
            var values = _objects.PairingProperties(pending.Fingerprint);
            if (values != null) all[BusNames.PendingPath(pending.Fingerprint)] = values;
        }

        return all;
    }

    private static string InterfaceFor(string path) =>
        path.StartsWith(BusNames.PendingPrefix, StringComparison.Ordinal)
            ? BusNames.PairingInterface
            : BusNames.DeviceInterface;

    // ──────────────────────────────── signals

    private void EmitPropertiesChanged(string path, string iface, List<KeyValuePair<string, object>> changed)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, path, BusNames.PropertiesInterface, "PropertiesChanged", "sa{sv}as");
        writer.WriteString(iface);

        var dictionary = writer.WriteDictionaryStart();
        foreach (var pair in changed) BusWrite.Entry(ref writer, pair.Key, pair.Value);
        writer.WriteDictionaryEnd(dictionary);

        var invalidated = writer.WriteArrayStart(DBusType.String);
        writer.WriteArrayEnd(invalidated);

        _connection.TrySendMessage(writer.CreateMessage());
    }

    private void EmitInterfacesAdded(string path, string iface, Dictionary<string, object> values)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, BusNames.Root, BusNames.ObjectManagerInterface, "InterfacesAdded", "oa{sa{sv}}");
        writer.WriteObjectPath(path);

        var interfaces = writer.WriteDictionaryStart();
        BusWrite.InterfaceEntry(ref writer, iface, values);
        writer.WriteDictionaryEnd(interfaces);

        _connection.TrySendMessage(writer.CreateMessage());
    }

    private void EmitInterfacesRemoved(string path, string iface)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, BusNames.Root, BusNames.ObjectManagerInterface, "InterfacesRemoved", "oas");
        writer.WriteObjectPath(path);
        writer.WriteArray(new[] { iface });

        _connection.TrySendMessage(writer.CreateMessage());
    }

    /// <summary>
    /// An argument-free signal for a list that churns.
    ///
    /// Notifications, activity and received files are unbounded and change often, so they are
    /// fetched rather than published: this says only that the answer moved.
    /// </summary>
    private void Signal(string member)
    {
        if (_disposed) return;

        try
        {
            var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, BusNames.Root, BusNames.DaemonInterface, member, "");
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            Log.Write("Bus", $"Could not emit {member}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _coalesce?.Dispose();
            _coalesce = null;
        }

        try { _connection.Dispose(); } catch { /* Shutting down; nothing left to tell. */ }
    }
}
