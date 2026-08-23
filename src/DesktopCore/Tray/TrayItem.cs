using CoreLib.Identity;
using CoreLib.Diagnostics;
using DesktopCore.Ipc;
using Tmds.DBus.Protocol;

namespace DesktopCore.Tray;

/// <summary>
/// Mesh Sync's own StatusNotifierItem: the tray icon, its tooltip, its states and its menu.
///
/// <para><b>Why this replaces Avalonia's TrayIcon rather than configuring it.</b> Avalonia's
/// tray API offers an icon, a tooltip string and a menu, and maps them onto an SNI that - read
/// live from the running app - had an empty <c>IconName</c> with a 128px bitmap in its place, an
/// empty <c>ToolTip</c> struct despite the text being set, and <c>Status</c> pinned to
/// <c>Active</c> for ever. None of those are settings it does not expose; they are things the
/// interface has and the toolkit does not carry.</para>
///
/// <para>So the item is owned outright. That costs a dbusmenu implementation, and buys a themed
/// icon that follows the colour scheme, an honest tooltip, a middle-click that sends the
/// clipboard, and - the one that matters - an icon that turns to <c>NeedsAttention</c> when a
/// device is asking to join, which is the failure this whole feature exists to remove.</para>
///
/// <para><b>It lives in DesktopCore, so the headless daemon has one too.</b> A machine with a
/// panel but no Avalonia window is a supported arrangement and now looks like one.</para>
/// </summary>
public sealed class TrayItem : IPathMethodHandler, IDisposable
{
    private readonly Daemon _daemon;
    private DBusConnection _connection;
    private readonly TrayMenu _menu;
    private readonly Action<string>? _show;
    private readonly Action? _quit;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Cancelled when the tray setting changes, to wake the registration loop.</summary>
    private CancellationTokenSource _wanted = new();

    private string _iconName = "meshsync-tray-symbolic";
    private string _status = "Passive";
    private string _tooltip = "";
    private bool _registered;
    private bool _disposed;

    private TrayItem(Daemon daemon, DBusConnection connection, Action<string>? show, Action? quit)
    {
        _daemon = daemon;
        _connection = connection;
        _show = show;
        _quit = quit;
        _menu = new TrayMenu(BuildMenu, reason => Log.Write("Tray", reason));
    }

    string IPathMethodHandler.Path => BusNamesTray.ItemPath;

    public bool HandlesChildPaths => false;

    /// <summary>
    /// Exports the item and registers it with whatever is hosting the tray.
    ///
    /// <para>Returns null on a desktop with no status area, which is an ordinary desktop rather
    /// than a broken one - the window and the widget are both still there.</para>
    /// </summary>
    public static async Task<TrayItem?> TryStartAsync(Daemon daemon,
                                                      Action<string>? show = null,
                                                      Action? quit = null)
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (string.IsNullOrEmpty(DBusAddress.Session)) return null;

        DBusConnection? connection = null;

        try
        {
            // Its own connection, so the tray item has its own unique name. That is what the
            // watcher registers, and it is what lets two devices on one machine each own a tray
            // icon instead of fighting over one.
            connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);

            var item = new TrayItem(daemon, connection, show, quit);
            connection.AddMethodHandler(item);
            connection.AddMethodHandler(item._menu);

            item.Refresh();
            item.Subscribe();

            // Turned off, this stays exported and simply never registers with the watcher -
            // which is the only way to not appear, since the tray has no "hide" and an item is
            // gone only when its bus name is. Turning it back on registers without a restart.
            _ = Task.Run(() => item.KeepRegisteredAsync(item._stopping.Token))
                    .ContinueWith(t =>
                    {
                        if (t.Exception != null)
                            Log.Write("Tray", "The tray registration loop stopped", t.Exception.GetBaseException());
                    }, TaskContinuationOptions.OnlyOnFaulted);

            return item;
        }
        catch (Exception ex)
        {
            Log.Write("Tray", "Could not create the tray icon", ex);
            connection?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Registers with the watcher, and registers again whenever the watcher comes back.
    ///
    /// <para>A tray host is a normal program that can be restarted - plasmashell is restarted
    /// routinely, by crashes and by people. An item that registers once and never again vanishes
    /// from the panel until the app is restarted, which reads as the app having stopped.</para>
    /// </summary>
    private async Task KeepRegisteredAsync(CancellationToken token)
    {
        // Two loops, because the watcher belongs to the connection. Turning the icon off drops
        // that connection - the only way to make the item go - and a watcher on a disposed
        // connection answers nothing for ever, so it has to be taken out again on the new one.
        // Without the outer loop, hiding the icon worked and bringing it back never did.
        while (!token.IsCancellationRequested)
        {
        using var watcher = await _connection.WatchNameOwnerAsync(BusNamesTray.Watcher).ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            string owner = watcher.GetCurrentOwner() ?? "";

            if (!string.IsNullOrEmpty(owner) && _daemon.TrayIconVisible)
            {
                await RegisterAsync().ConfigureAwait(false);
            }

            var changed = watcher.GetOwnerChangedCancellationToken(owner);

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    changed, token, _wanted.Token);

                await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The watcher moved, the setting changed, or we are shutting down.
            }

            if (token.IsCancellationRequested) return;

            if (_wanted.IsCancellationRequested)
            {
                var spent = _wanted;
                _wanted = new CancellationTokenSource();
                spent.Dispose();

                // Turning the icon off has to make the bus name go, because that is the only
                // thing a tray watches: there is no "unregister", and an item stays on the panel
                // for as long as its name exists. So the connection is dropped and a fresh one
                // opened, unregistered, ready to register again if the setting comes back.
                if (!_daemon.TrayIconVisible && _registered)
                {
                    await ResetConnectionAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        }
    }

    /// <summary>
    /// Drops the bus name, which is what removes the icon, and opens a fresh connection so it
    /// can come back without restarting the app.
    /// </summary>
    private async Task ResetConnectionAsync()
    {
        _registered = false;

        try { _connection.Dispose(); } catch { }

        try
        {
            var replacement = new DBusConnection(DBusAddress.Session!);
            await replacement.ConnectAsync().ConfigureAwait(false);

            replacement.AddMethodHandler(this);
            replacement.AddMethodHandler(_menu);

            _connection = replacement;
            Log.Write("Tray", "The tray icon was turned off.");
        }
        catch (Exception ex)
        {
            Log.Write("Tray", "Could not reopen the tray connection", ex);
        }
    }

    private async Task RegisterAsync()
    {
        try
        {
            var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BusNamesTray.Watcher, BusNamesTray.WatcherPath,
                BusNamesTray.Watcher, "RegisterStatusNotifierItem", "s");
            writer.WriteString(_connection.UniqueName ?? "");

            await _connection.CallMethodAsync(writer.CreateMessage()).ConfigureAwait(false);
            _registered = true;
            Log.Write("Tray", "Registered the tray icon.");
        }
        catch (Exception ex)
        {
            Log.Write("Tray", "The tray host would not take the icon", ex);
        }
    }

    // ──────────────────────────────── state

    private void Subscribe()
    {
        _daemon.Links.Changed += Refresh;
        _daemon.Security.Peers.Changed += Refresh;
        _daemon.Security.PairingRequested += _ => Refresh();
        _daemon.Notifications.Changed += Refresh;
        _daemon.Ringer.StateChanged += _ => Refresh();

        // Turning the icon off cannot un-register it - the tray removes an item only when its
        // bus name goes - so hiding drops this connection and showing brings a new one up. The
        // loop below is woken either way.
        _daemon.TrayIconVisibleChanged += _ => { try { _wanted.Cancel(); } catch { } };
    }

    /// <summary>
    /// Works out the icon, the status and the tooltip, and tells the host only what moved.
    ///
    /// <para>Signalling unconditionally would be cheaper to write and makes some panels redraw
    /// the icon several times a second while a dial round runs.</para>
    /// </summary>
    private void Refresh()
    {
        if (_disposed) return;

        try
        {
            int waiting = _daemon.Pending.Count;
            bool connected = _daemon.Links.IsConnected;
            bool anyPeers = !_daemon.Security.Peers.IsEmpty;

            string icon = waiting > 0 ? "meshsync-tray-attention-symbolic"
                : connected ? "meshsync-tray-active-symbolic"
                : "meshsync-tray-symbolic";

            string status = waiting > 0 ? "NeedsAttention"
                : connected || !anyPeers ? "Active"
                : "Passive";

            string tooltip = BuildTooltip(waiting, connected);

            if (icon != _iconName) { _iconName = icon; Signal("NewIcon", null); Signal("NewAttentionIcon", null); }
            if (status != _status) { _status = status; Signal("NewStatus", status); }
            if (tooltip != _tooltip) { _tooltip = tooltip; Signal("NewToolTip", null); }
        }
        catch (Exception ex)
        {
            Log.Write("Tray", "Could not refresh the tray icon", ex);
        }
    }

    private string BuildTooltip(int waiting, bool connected)
    {
        if (waiting > 0)
            return waiting == 1 ? "A device is asking to join" : $"{waiting} devices are asking to join";

        if (_daemon.Security.Peers.IsEmpty) return "No devices paired yet";

        if (!connected) return $"{_daemon.Security.Peers.Count} paired, none reachable";

        int up = _daemon.Security.Peers.Peers.Count(p => _daemon.IsConnectedTo(p.Fingerprint));
        string link = _daemon.Links.ActiveLink == CoreLib.Transport.LinkKind.Ble ? "Bluetooth" : "Wi-Fi";

        return up == 1 ? $"1 device over {link}" : $"{up} devices over {link}";
    }

    // ──────────────────────────────── the menu

    /// <summary>
    /// What the menu says right now.
    ///
    /// <para>Rebuilt each time it is opened, so it is about the mesh as it is rather than as it
    /// was when the app started. A pairing request is first because it is the only thing here
    /// that is waiting on a person.</para>
    /// </summary>
    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var items = new List<TrayMenuItem>();
        int id = 1;

        items.Add(new TrayMenuItem(id++, _daemon.Security.Peers.MeshNameOrDefault, Enabled: false));
        items.Add(new TrayMenuItem(id++, _tooltip, Enabled: false));
        items.Add(new TrayMenuItem(id++, "", IsSeparator: true));

        foreach (var pending in _daemon.Pending)
        {
            string who = string.IsNullOrWhiteSpace(pending.Name) ? pending.ShortFingerprint : pending.Name;
            string fingerprint = pending.Fingerprint;

            items.Add(new TrayMenuItem(id++, $"Allow {who} ({pending.ShortFingerprint})",
                IconName: "dialog-ok-apply", Invoke: () => _daemon.Confirm(fingerprint)));

            items.Add(new TrayMenuItem(id++, $"Refuse {who}",
                IconName: "dialog-cancel", Invoke: () => _daemon.Reject(fingerprint)));
        }

        if (_daemon.Pending.Count > 0) items.Add(new TrayMenuItem(id++, "", IsSeparator: true));

        items.Add(new TrayMenuItem(id++, "Send clipboard", IconName: "edit-paste",
            Invoke: () => _ = _daemon.SendClipboardAsync()));

        items.Add(new TrayMenuItem(id++, "Reconnect now", IconName: "view-refresh",
            Invoke: _daemon.NudgeDial));

        if (_daemon.Ringer.IsRinging)
        {
            items.Add(new TrayMenuItem(id++, "Stop ringing", IconName: "audio-volume-muted",
                Invoke: _daemon.Ringer.Stop));
        }

        var peers = _daemon.Security.Peers.Peers;
        if (peers.Count > 0)
        {
            items.Add(new TrayMenuItem(id++, "", IsSeparator: true));

            foreach (var peer in peers)
            {
                string name = string.IsNullOrWhiteSpace(peer.Name)
                    ? DeviceIdentity.Shorten(peer.Fingerprint) : peer.Name;

                bool reachable = _daemon.IsConnectedTo(peer.Fingerprint);
                string fingerprint = peer.Fingerprint;

                items.Add(new TrayMenuItem(id++, reachable ? $"Ring {name}" : $"{name} - not reachable",
                    Enabled: reachable, IconName: reachable ? "audio-volume-high" : "",
                    Invoke: reachable ? () => _ = _daemon.RingAsync(fingerprint, true) : null));
            }
        }

        items.Add(new TrayMenuItem(id++, "", IsSeparator: true));

        if (_show != null)
            items.Add(new TrayMenuItem(id++, "Open Mesh Sync", IconName: "meshsync", Invoke: () => _show("home")));

        items.Add(new TrayMenuItem(id++, "Quit", IconName: "application-exit", Invoke: () => _quit?.Invoke()));

        return items;
    }

    // ──────────────────────────────── serving

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        string iface = context.Request.InterfaceAsString ?? "";
        string member = context.Request.MemberAsString ?? "";

        try
        {
            if (context.IsDBusIntrospectRequest)
            {
                ReadOnlyMemory<byte>[] xml = [System.Text.Encoding.UTF8.GetBytes(IntrospectXml)];
                context.ReplyIntrospectXml(xml, Array.Empty<string>());
                return default;
            }

            if (iface == BusNamesTray.PropertiesInterface) { HandleProperties(context, member); return default; }

            if (iface == BusNamesTray.ItemInterface)
            {
                switch (member)
                {
                    case "Activate":
                        _show?.Invoke("home");
                        Ok(context);
                        return default;

                    // Middle-click. The one action worth a single gesture, because everything
                    // else needs a device chosen or a fingerprint compared.
                    case "SecondaryActivate":
                        _ = _daemon.SendClipboardAsync();
                        Ok(context);
                        return default;

                    case "ContextMenu":
                    case "Scroll":
                        Ok(context);
                        return default;
                }
            }

            context.ReplyUnknownMethodError();
        }
        catch (Exception ex)
        {
            Log.Write("Tray", $"{iface}.{member} failed", ex);
            if (!context.ReplySent) context.ReplyError("dev.meshsync.Error.Failed", ex.Message);
        }

        return default;
    }

    private static void Ok(MethodContext context) =>
        context.Reply(context.CreateReplyWriter("").CreateMessage());

    private Dictionary<string, object> Properties() => new(StringComparer.Ordinal)
    {
        // Communications rather than Hardware: this is a thing your other devices talk to you
        // through, and trays group by category.
        ["Category"] = "Communications",
        ["Id"] = "meshsync",
        ["Title"] = "Mesh Sync",
        ["Status"] = _status,
        ["IconName"] = _iconName,
        ["AttentionIconName"] = "meshsync-tray-attention-symbolic",
        ["OverlayIconName"] = "",
        ["IconThemePath"] = "",

        // False, so a left click reaches Activate rather than opening the menu. The menu is on
        // right click, where a tray menu belongs.
        ["ItemIsMenu"] = false,
        ["WindowId"] = 0,
    };

    private void HandleProperties(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        reader.ReadString();

        if (member == "GetAll")
        {
            var writer = context.CreateReplyWriter("a{sv}");
            var dictionary = writer.WriteDictionaryStart();

            foreach (var pair in Properties()) BusWrite.Entry(ref writer, pair.Key, pair.Value);

            WriteMenuEntry(ref writer);
            WriteToolTipEntry(ref writer);

            writer.WriteDictionaryEnd(dictionary);
            context.Reply(writer.CreateMessage());
            return;
        }

        if (member == "Get")
        {
            string name = reader.ReadString();
            var writer = context.CreateReplyWriter("v");

            if (name == "Menu") { writer.WriteVariantObjectPath(TrayMenu.Path); }
            else if (name == "ToolTip") { WriteToolTip(ref writer); }
            else if (Properties().TryGetValue(name, out object? value)) { BusWrite.Variant(ref writer, value); }
            else
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                return;
            }

            context.Reply(writer.CreateMessage());
            return;
        }

        context.ReplyUnknownMethodError();
    }

    private static void WriteMenuEntry(ref MessageWriter writer)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString("Menu");
        writer.WriteVariantObjectPath(TrayMenu.Path);
    }

    private void WriteToolTipEntry(ref MessageWriter writer)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString("ToolTip");
        WriteToolTip(ref writer);
    }

    /// <summary>
    /// The tooltip, which is a struct rather than a string: an icon name, a list of pixmaps, a
    /// title and a description. Avalonia's tray left this empty, which is why hovering the icon
    /// has said nothing at all until now.
    /// </summary>
    private void WriteToolTip(ref MessageWriter writer)
    {
        writer.WriteSignature("(sa(iiay)ss)");
        writer.WriteStructureStart();
        writer.WriteString(_iconName);

        var pixmaps = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteArrayEnd(pixmaps);

        writer.WriteString(_daemon.Security.Peers.MeshNameOrDefault);
        writer.WriteString(_tooltip);
    }

    private void Signal(string member, string? argument)
    {
        if (_disposed) return;

        try
        {
            var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, BusNamesTray.ItemPath, BusNamesTray.ItemInterface,
                member, argument == null ? "" : "s");

            if (argument != null) writer.WriteString(argument);

            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            Log.Write("Tray", $"Could not emit {member}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _stopping.Cancel(); } catch { }
        try { _wanted.Cancel(); } catch { }
        _stopping.Dispose();
        _wanted.Dispose();
        try { _connection.Dispose(); } catch { }
    }

    private const string IntrospectXml = """
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewToolTip"/>
          <signal name="NewTitle"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>
        """;
}
