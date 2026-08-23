using System.Text;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using Tmds.DBus.Protocol;

namespace DesktopCore.Ipc;

/// <summary>
/// The exported tree: the running device at <c>/dev/meshsync/Daemon</c>, one object per paired
/// device beneath it, and one per pairing request waiting to be answered.
///
/// <para><b>One handler, not one per object.</b> <c>HandlesChildPaths</c> means every call under
/// the root arrives here and is dispatched on its path. The alternative - adding and removing a
/// handler as devices come and go - has to be kept in step with the registry from a background
/// thread, and a device that is forgotten while a call to it is in flight is exactly the case
/// that gets it wrong.</para>
///
/// <para><b>What is deliberately not here.</b> Clipboard text, image bytes, notification titles
/// and bodies, and activity previews. Everything on the session bus is readable by every program
/// running as this user, and the mirrored notifications are documented as the most private thing
/// this app carries. <c>SendText</c> takes text from a caller; nothing hands text back.</para>
/// </summary>
internal sealed class MeshBusObject : IPathMethodHandler
{
    private readonly Daemon _daemon;
    private readonly Action<string>? _show;
    private readonly Action? _quit;

    public MeshBusObject(Daemon daemon, Action<string>? show, Action? quit)
    {
        _daemon = daemon;
        _show = show;
        _quit = quit;
    }

    public string Path => BusNames.Root;

    public bool HandlesChildPaths => true;

    // ──────────────────────────────── property snapshots

    /// <summary>
    /// Everything <c>dev.meshsync.Daemon1</c> publishes, as one snapshot.
    ///
    /// <para>One list rather than four: <c>Get</c>, <c>GetAll</c>, <c>GetManagedObjects</c> and
    /// the change detection behind <c>PropertiesChanged</c> all read this, so a property cannot
    /// be added to one of them and forgotten in the others.</para>
    /// </summary>
    public Dictionary<string, object> DaemonProperties()
    {
        var security = _daemon.Security;

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DeviceName"] = _daemon.DeviceName,
            ["MeshName"] = security.Peers.MeshNameOrDefault,
            ["Fingerprint"] = security.Identity.Fingerprint,
            ["IsConnected"] = _daemon.Links.IsConnected,
            ["ActiveLink"] = LinkName(_daemon.Links.ActiveLink),
            ["IsDialling"] = _daemon.IsDialling,
            ["PeerCount"] = (uint)security.Peers.Count,
            ["ConnectedCount"] = (uint)security.Peers.Peers.Count(p => _daemon.IsConnectedTo(p.Fingerprint)),
            ["PendingCount"] = (uint)_daemon.Pending.Count,
            ["BluetoothStatus"] = _daemon.BluetoothStatus,
            ["Transport"] = TransportName(_daemon.Transports.Current),
            ["IsRinging"] = _daemon.Ringer.IsRinging,
            ["NotificationCount"] = (uint)_daemon.Notifications.Count,
            ["SentCount"] = (uint)_daemon.Activity.SentCount,
            ["ReceivedCount"] = (uint)_daemon.Activity.ReceivedCount,
            ["PairingUri"] = _daemon.PairingUri,
            ["TrayIconVisible"] = _daemon.TrayIconVisible,
            ["ShowNotificationContent"] = _daemon.ShowNotificationContent,
        };
    }

    /// <summary>
    /// One paired device.
    ///
    /// <para><c>IsConnected</c> is answered per peer here, which is more than the window can say
    /// today - <c>LinkState</c> knows only whether <em>anything</em> is reachable. A device list
    /// on a panel makes that difference visible, because otherwise every row shows the same
    /// dot.</para>
    /// </summary>
    public Dictionary<string, object>? DeviceProperties(string fingerprint)
    {
        var peer = _daemon.Security.Peers.Find(fingerprint);
        if (peer == null) return null;

        bool connected = _daemon.IsConnectedTo(peer.Fingerprint);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = peer.Name ?? "",
            ["Fingerprint"] = peer.Fingerprint,
            ["ShortFingerprint"] = CoreLib.Identity.DeviceIdentity.Shorten(peer.Fingerprint),
            ["IsConnected"] = connected,
            ["ActiveLink"] = !connected ? "none"
                : _daemon.IsWiFiConnectedTo(peer.Fingerprint) ? "wifi" : "ble",
            ["LastSeen"] = peer.LastSeenUtc.ToUnixTimeSeconds(),
            ["LastAddress"] = peer.LastAddress ?? "",
        };
    }

    /// <summary>One device waiting for a human to compare fingerprints.</summary>
    public Dictionary<string, object>? PairingProperties(string fingerprint)
    {
        var pending = _daemon.Pending.FirstOrDefault(p =>
            string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

        if (pending == null) return null;

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = pending.Name ?? "",
            ["Fingerprint"] = pending.Fingerprint,
            ["ShortFingerprint"] = pending.ShortFingerprint,
            ["Address"] = pending.Address ?? "",
            ["SeenAt"] = pending.SeenUtc.ToUnixTimeSeconds(),
        };
    }

    private static string LinkName(LinkKind kind) => kind switch
    {
        LinkKind.WiFi => "wifi",
        LinkKind.Ble => "ble",
        _ => "none",
    };

    private static string TransportName(TransportPreference preference) => preference switch
    {
        TransportPreference.WiFi => "wifi",
        TransportPreference.Ble => "ble",
        _ => "both",
    };

    private static TransportPreference? ParseTransport(string name) => name.ToLowerInvariant() switch
    {
        "both" => TransportPreference.Both,
        "wifi" => TransportPreference.WiFi,
        "ble" => TransportPreference.Ble,
        _ => null,
    };

    // ──────────────────────────────── dispatch

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        string path = request.PathAsString ?? "";
        string iface = request.InterfaceAsString ?? "";
        string member = request.MemberAsString ?? "";

        try
        {
            if (context.IsDBusIntrospectRequest) { Introspect(context, path); return default; }

            if (iface == BusNames.PropertiesInterface) { HandleProperties(context, path, member); return default; }

            if (path == BusNames.Root)
            {
                if (iface == BusNames.ObjectManagerInterface && member == "GetManagedObjects")
                {
                    ReplyManagedObjects(context);
                    return default;
                }

                if (iface == BusNames.DaemonInterface) return HandleDaemon(context, member);
            }

            string? device = BusNames.FingerprintIn(path, BusNames.DevicesPrefix);
            if (device != null && iface == BusNames.DeviceInterface) return HandleDevice(context, device, member);

            string? pairing = BusNames.FingerprintIn(path, BusNames.PendingPrefix);
            if (pairing != null && iface == BusNames.PairingInterface) { HandlePairing(context, pairing, member); return default; }

            context.ReplyUnknownMethodError();
        }
        catch (Exception ex)
        {
            // A handler that throws must not take the connection down with it: the widget would
            // lose the daemon and report it as not running, which is a lie about a process that
            // is still syncing perfectly well.
            Log.Write("Bus", $"{iface}.{member} on {path} failed", ex);
            if (!context.ReplySent) context.ReplyError("dev.meshsync.Error.Failed", ex.Message);
        }

        return default;
    }

    // ──────────────────────────────── properties interface

    private void HandleProperties(MethodContext context, string path, string member)
    {
        var reader = context.Request.GetBodyReader();
        string iface = reader.ReadString();

        var values = PropertiesFor(path, iface);
        if (values == null) { context.ReplyError("dev.meshsync.Error.UnknownInterface", $"{path} has no {iface}."); return; }

        switch (member)
        {
            case "GetAll":
            {
                var writer = context.CreateReplyWriter("a{sv}");
                BusWrite.Dictionary(ref writer, values);
                context.Reply(writer.CreateMessage());
                return;
            }

            case "Get":
            {
                string name = reader.ReadString();
                if (!values.TryGetValue(name, out object? value))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                    return;
                }

                var writer = context.CreateReplyWriter("v");
                BusWrite.Variant(ref writer, value);
                context.Reply(writer.CreateMessage());
                return;
            }

            case "Set":
            {
                string name = reader.ReadString();
                Set(context, path, name, ref reader);
                return;
            }
        }

        context.ReplyUnknownMethodError();
    }

    /// <summary>
    /// The two properties that can be written, and nothing else.
    ///
    /// A writable property is a way for any program running as this user to change how the mesh
    /// behaves, so the list is deliberately short: what this device is called within the mesh,
    /// and which links it offers. Both already have a control in the window.
    /// </summary>
    private void Set(MethodContext context, string path, string name, ref Reader reader)
    {
        if (path != BusNames.Root)
        {
            context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", name);
            return;
        }

        switch (name)
        {
            case "MeshName":
            {
                string value = reader.ReadVariantValue().GetString();
                _daemon.Security.Peers.MeshName = value;
                Log.Write("Bus", $"The mesh was renamed to \"{value}\" over the bus.");
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                return;
            }

            case "TrayIconVisible":
            {
                bool visible = reader.ReadVariantValue().GetBool();
                _daemon.TrayIconVisible = visible;
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                return;
            }

            case "ShowNotificationContent":
            {
                bool show = reader.ReadVariantValue().GetBool();
                _daemon.ShowNotificationContent = show;
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                return;
            }

            case "Transport":
            {
                var preference = ParseTransport(reader.ReadVariantValue().GetString());
                if (preference == null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Transport is both, wifi or ble.");
                    return;
                }

                _daemon.Transports.Set(preference.Value);
                Log.Write("Bus", $"The transport preference was set to {preference} over the bus.");
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                return;
            }
        }

        context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", name);
    }

    private Dictionary<string, object>? PropertiesFor(string path, string iface)
    {
        if (path == BusNames.Root) return iface == BusNames.DaemonInterface ? DaemonProperties() : null;

        string? device = BusNames.FingerprintIn(path, BusNames.DevicesPrefix);
        if (device != null) return iface == BusNames.DeviceInterface ? DeviceProperties(device) : null;

        string? pairing = BusNames.FingerprintIn(path, BusNames.PendingPrefix);
        if (pairing != null) return iface == BusNames.PairingInterface ? PairingProperties(pairing) : null;

        return null;
    }

    // ──────────────────────────────── object manager

    private void ReplyManagedObjects(MethodContext context)
    {
        var writer = context.CreateReplyWriter("a{oa{sa{sv}}}");

        var objects = writer.WriteDictionaryStart();

        foreach (var peer in _daemon.Security.Peers.Peers)
        {
            var values = DeviceProperties(peer.Fingerprint);
            if (values == null) continue;

            writer.WriteDictionaryEntryStart();
            writer.WriteObjectPath(BusNames.DevicePath(peer.Fingerprint));

            var interfaces = writer.WriteDictionaryStart();
            BusWrite.InterfaceEntry(ref writer, BusNames.DeviceInterface, values);
            writer.WriteDictionaryEnd(interfaces);
        }

        foreach (var pending in _daemon.Pending)
        {
            var values = PairingProperties(pending.Fingerprint);
            if (values == null) continue;

            writer.WriteDictionaryEntryStart();
            writer.WriteObjectPath(BusNames.PendingPath(pending.Fingerprint));

            var interfaces = writer.WriteDictionaryStart();
            BusWrite.InterfaceEntry(ref writer, BusNames.PairingInterface, values);
            writer.WriteDictionaryEnd(interfaces);
        }

        writer.WriteDictionaryEnd(objects);
        context.Reply(writer.CreateMessage());
    }

    // ──────────────────────────────── dev.meshsync.Daemon1

    private ValueTask HandleDaemon(MethodContext context, string member)
    {
        switch (member)
        {
            case "SendText":
            {
                string text = context.Request.GetBodyReader().ReadString();
                Defer(context, "u", () => _daemon.SendTextAsync(text),
                    static (ref MessageWriter writer, int sent) => writer.WriteUInt32((uint)sent));
                return default;
            }

            case "SendFile":
            {
                var reader = context.Request.GetBodyReader();
                string fingerprint = reader.ReadString();
                string file = reader.ReadString();
                DeferFileSend(context, fingerprint, file);
                return default;
            }

            case "SendClipboard":
                Defer(context, "(bs)", () => _daemon.SendClipboardAsync(),
                    static (ref MessageWriter writer, (bool Ok, string Message) result) =>
                    {
                        writer.WriteStructureStart();
                        writer.WriteBool(result.Ok);
                        writer.WriteString(result.Message);
                    });
                return default;

            case "Dial":
                _daemon.NudgeDial();
                Ok(context);
                return default;

            case "Join":
            {
                var (ok, message) = _daemon.Join(context.Request.GetBodyReader().ReadString());
                ReplyOutcome(context, ok, message);
                return default;
            }

            case "StopRinging":
                _daemon.Ringer.Stop();
                Ok(context);
                return default;

            case "Notifications":
                ReplyNotifications(context);
                return default;

            case "DismissNotification":
            {
                string key = context.Request.GetBodyReader().ReadString();
                Defer(context, () => _daemon.DismissNotificationAsync(key));
                return default;
            }

            case "ReplyToNotification":
            {
                var reader = context.Request.GetBodyReader();
                string key = reader.ReadString();
                string text = reader.ReadString();

                Defer(context, "(bs)", () => _daemon.ReplyToNotificationAsync(key, text),
                    static (ref MessageWriter writer, (bool Ok, string Message) result) =>
                    {
                        writer.WriteStructureStart();
                        writer.WriteBool(result.Ok);
                        writer.WriteString(result.Message);
                    });
                return default;
            }

            case "DismissAllNotifications":
                Defer(context, () => _daemon.DismissAllNotificationsAsync());
                return default;

            case "Activity":
                ReplyActivity(context);
                return default;

            case "Show":
            {
                string page = context.Request.GetBodyReader().ReadString();
                _show?.Invoke(page);
                Ok(context);
                return default;
            }

            case "Quit":
                Ok(context);
                _quit?.Invoke();
                return default;
        }

        context.ReplyUnknownMethodError();
        return default;
    }

    /// <summary>
    /// Which notifications are showing, and from where - never what they say.
    ///
    /// <para>Enough for a widget to badge "3 from S21 FE" and offer to dismiss one. The title and
    /// body stay in the process, which is the rule <c>MirroredNotifications</c> was written
    /// under and does not stop being true because the reader is on the same machine.</para>
    /// </summary>
    /// <summary>
    /// What is mirrored, and - only if the owner has asked for it - what it says.
    ///
    /// <para>The sender and the text are empty unless <c>ShowNotificationContent</c> is on. With
    /// it off a panel can group by app, badge a count and draw a reply box, which is most of
    /// what a panel is for; with it on it can group by conversation and show a preview, which is
    /// what a phone's own shade does. The strict answer is the default because everything on this
    /// bus is readable by every program running as this user.</para>
    /// </summary>
    private void ReplyNotifications(MethodContext context)
    {
        var writer = context.CreateReplyWriter("a(sssxbsss)");

        bool content = _daemon.ShowNotificationContent;
        var entries = _daemon.Notifications.Snapshot();
        var array = writer.WriteArrayStart(DBusType.Struct);

        foreach (var entry in entries)
        {
            writer.WriteStructureStart();
            writer.WriteString(entry.Key);
            writer.WriteString(entry.AppName ?? "");
            writer.WriteString(entry.From ?? "");
            writer.WriteInt64(new DateTimeOffset(entry.AtUtc, TimeSpan.Zero).ToUnixTimeSeconds());
            writer.WriteBool(entry.CanReply);
            writer.WriteString(entry.ReplyLabel ?? "");

            // The conversation and the message. Empty strings rather than an absent field, so
            // the signature does not change with the setting and a client written against one
            // answer keeps working against the other.
            writer.WriteString(content ? entry.Title ?? "" : "");
            writer.WriteString(content ? entry.Text ?? "" : "");
        }

        writer.WriteArrayEnd(array);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>
    /// What has crossed this session, with the preview left out for the same reason.
    ///
    /// A clipboard preview is the clipboard. Direction, kind, size and time say enough for a
    /// panel to show that something is happening without putting what you copied on a bus every
    /// program can read.
    /// </summary>
    private void ReplyActivity(MethodContext context)
    {
        var writer = context.CreateReplyWriter("a(ssxx)");

        var array = writer.WriteArrayStart(DBusType.Struct);

        foreach (var entry in _daemon.Activity.Snapshot())
        {
            writer.WriteStructureStart();
            writer.WriteString(entry.Direction == SyncDirection.Sent ? "sent" : "received");
            writer.WriteString(entry.Kind.ToString().ToLowerInvariant());
            writer.WriteInt64(entry.SizeBytes);
            writer.WriteInt64(new DateTimeOffset(entry.AtUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        }

        writer.WriteArrayEnd(array);
        context.Reply(writer.CreateMessage());
    }

    // ──────────────────────────────── dev.meshsync.Device1

    private ValueTask HandleDevice(MethodContext context, string fingerprint, string member)
    {
        if (_daemon.Security.Peers.Find(fingerprint) == null)
        {
            context.ReplyError("dev.meshsync.Error.NoSuchDevice", fingerprint);
            return default;
        }

        switch (member)
        {
            case "Ring":
            {
                bool on = context.Request.GetBodyReader().ReadBool();
                Defer(context, "b", () => _daemon.RingAsync(fingerprint, on),
                    static (ref MessageWriter writer, bool ok) => writer.WriteBool(ok));
                return default;
            }

            case "SendFile":
                DeferFileSend(context, fingerprint, context.Request.GetBodyReader().ReadString());
                return default;

            case "EnsureWiFi":
                Defer(context, "b", () => _daemon.EnsureWiFiToAsync(fingerprint),
                    static (ref MessageWriter writer, bool ok) => writer.WriteBool(ok));
                return default;

            case "Forget":
                _daemon.Security.Peers.Forget(fingerprint);
                Log.Write("Bus", $"{CoreLib.Identity.DeviceIdentity.Shorten(fingerprint)} was forgotten over the bus.");
                Ok(context);
                return default;
        }

        context.ReplyUnknownMethodError();
        return default;
    }

    // ──────────────────────────────── dev.meshsync.Pairing1

    private void HandlePairing(MethodContext context, string fingerprint, string member)
    {
        switch (member)
        {
            case "Confirm":
            {
                var (ok, message) = _daemon.Confirm(fingerprint);
                ReplyOutcome(context, ok, message);
                return;
            }

            case "Reject":
            {
                var (ok, message) = _daemon.Reject(fingerprint);
                ReplyOutcome(context, ok, message);
                return;
            }
        }

        context.ReplyUnknownMethodError();
    }

    // ──────────────────────────────── replying

    private static void Ok(MethodContext context) =>
        context.Reply(context.CreateReplyWriter("").CreateMessage());

    private static void ReplyOutcome(MethodContext context, bool ok, string message)
    {
        var writer = context.CreateReplyWriter("(bs)");
        writer.WriteStructureStart();
        writer.WriteBool(ok);
        writer.WriteString(message);
        context.Reply(writer.CreateMessage());
    }

    private void DeferFileSend(MethodContext context, string fingerprint, string file)
    {
        string name = System.IO.Path.GetFileName(file);

        Defer(context, "(bs)", () => _daemon.SendFileAsync(fingerprint, file),
            (ref MessageWriter writer, FileSendResult result) =>
            {
                writer.WriteStructureStart();
                writer.WriteBool(result == FileSendResult.Sent);
                writer.WriteString(result == FileSendResult.Sent
                    ? $"{name} sent."
                    : $"{name} did not send: {result}.");
            });
    }

    /// <summary>Writes one deferred result. Separate because a writer cannot cross an await.</summary>
    private delegate void WriteResult<in T>(ref MessageWriter writer, T value);

    /// <summary>
    /// Answers from a background task, after this handler has already returned.
    ///
    /// <para>Dialling a peer takes seconds and sending a file takes as long as the file. Awaiting
    /// either inside the handler holds the dispatch loop for that whole time, so every other
    /// call - the property read a widget makes to draw itself - waits behind it. Verified: with
    /// this, a call issued during a two-and-a-half second one comes back in 18 ms.</para>
    ///
    /// <para><b>Why the work and the writing are two delegates.</b> <c>MessageWriter</c> is a ref
    /// struct, so it cannot be a local or a parameter anywhere inside an async method - the
    /// compiler refuses outright. The await therefore happens in one method that returns a plain
    /// value, and the writing happens in a synchronous one that never sees an await. The same
    /// ref-struct rule that makes an <c>Action&lt;MessageWriter&gt;</c> silently write into a
    /// discarded copy is refusing to compile here instead, which is the better half of it.</para>
    ///
    /// <para><c>DisposesAsynchronously</c> is what keeps the context alive past the return, so it
    /// is disposed here rather than by the caller.</para>
    /// </summary>
    private static void Defer<T>(MethodContext context, string signature,
                                 Func<Task<T>> work, WriteResult<T> write)
    {
        context.DisposesAsynchronously = true;
        _ = Task.Run(() => RunDeferredAsync(context, signature, work, write));
    }

    /// <summary>The same, for a call whose answer is only that it finished.</summary>
    private static void Defer(MethodContext context, Func<Task> work)
    {
        context.DisposesAsynchronously = true;

        _ = Task.Run(() => RunDeferredAsync<bool>(context, "", async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }, static (ref MessageWriter _, bool _) => { }));
    }

    private static async Task RunDeferredAsync<T>(MethodContext context, string signature,
                                                  Func<Task<T>> work, WriteResult<T> write)
    {
        try
        {
            T value = await work().ConfigureAwait(false);
            Send(context, signature, value, write);
        }
        catch (Exception ex)
        {
            Log.Write("Bus", "A deferred bus call failed", ex);
            try { if (!context.ReplySent) context.ReplyError("dev.meshsync.Error.Failed", ex.Message); } catch { }
        }
        finally
        {
            context.Dispose();
        }
    }

    /// <summary>Synchronous by necessity: the writer may not exist inside an async method.</summary>
    private static void Send<T>(MethodContext context, string signature, T value, WriteResult<T> write)
    {
        var writer = context.CreateReplyWriter(signature);
        write(ref writer, value);
        context.Reply(writer.CreateMessage());
    }

    // ──────────────────────────────── introspection

    private void Introspect(MethodContext context, string path)
    {
        if (path == BusNames.Root)
        {
            ReadOnlyMemory<byte>[] interfaces = [Utf8(DaemonXml), Utf8(ObjectManagerXml)];
            context.ReplyIntrospectXml(interfaces, ["devices", "pending"]);
            return;
        }

        if (path == BusNames.DevicesRoot)
        {
            context.ReplyIntrospectXml([],
                _daemon.Security.Peers.Peers.Select(p => BusNames.ToElement(p.Fingerprint)).ToList());
            return;
        }

        if (path == BusNames.PendingRoot)
        {
            context.ReplyIntrospectXml([],
                _daemon.Pending.Select(p => BusNames.ToElement(p.Fingerprint)).ToList());
            return;
        }

        if (BusNames.FingerprintIn(path, BusNames.DevicesPrefix) != null)
        {
            context.ReplyIntrospectXml([Utf8(DeviceXml)], Array.Empty<string>());
            return;
        }

        if (BusNames.FingerprintIn(path, BusNames.PendingPrefix) != null)
        {
            context.ReplyIntrospectXml([Utf8(PairingXml)], Array.Empty<string>());
            return;
        }

        context.ReplyIntrospectXml([], Array.Empty<string>());
    }

    private static ReadOnlyMemory<byte> Utf8(string xml) => Encoding.UTF8.GetBytes(xml);

    private const string ObjectManagerXml = """
        <interface name="org.freedesktop.DBus.ObjectManager">
          <method name="GetManagedObjects">
            <arg name="objects" type="a{oa{sa{sv}}}" direction="out"/>
          </method>
          <signal name="InterfacesAdded">
            <arg name="object" type="o"/><arg name="interfaces" type="a{sa{sv}}"/>
          </signal>
          <signal name="InterfacesRemoved">
            <arg name="object" type="o"/><arg name="interfaces" type="as"/>
          </signal>
        </interface>
        """;

    private const string DaemonXml = """
        <interface name="dev.meshsync.Daemon1">
          <property name="DeviceName" type="s" access="read"/>
          <property name="MeshName" type="s" access="readwrite"/>
          <property name="Fingerprint" type="s" access="read"/>
          <property name="IsConnected" type="b" access="read"/>
          <property name="ActiveLink" type="s" access="read"/>
          <property name="IsDialling" type="b" access="read"/>
          <property name="PeerCount" type="u" access="read"/>
          <property name="ConnectedCount" type="u" access="read"/>
          <property name="PendingCount" type="u" access="read"/>
          <property name="BluetoothStatus" type="s" access="read"/>
          <property name="Transport" type="s" access="readwrite"/>
          <property name="IsRinging" type="b" access="read"/>
          <property name="NotificationCount" type="u" access="read"/>
          <property name="SentCount" type="u" access="read"/>
          <property name="ReceivedCount" type="u" access="read"/>
          <property name="PairingUri" type="s" access="read"/>
          <property name="TrayIconVisible" type="b" access="readwrite"/>
          <property name="ShowNotificationContent" type="b" access="readwrite"/>
          <method name="SendText">
            <arg name="text" type="s" direction="in"/><arg name="sent" type="u" direction="out"/>
          </method>
          <method name="SendFile">
            <arg name="fingerprint" type="s" direction="in"/><arg name="path" type="s" direction="in"/>
            <arg name="result" type="(bs)" direction="out"/>
          </method>
          <method name="SendClipboard"><arg name="result" type="(bs)" direction="out"/></method>
          <method name="Dial"/>
          <method name="Join">
            <arg name="uri" type="s" direction="in"/><arg name="result" type="(bs)" direction="out"/>
          </method>
          <method name="StopRinging"/>
          <method name="Notifications">
            <arg name="notifications" type="a(sssxbsss)" direction="out"/>
          </method>
          <method name="ReplyToNotification">
            <arg name="key" type="s" direction="in"/><arg name="text" type="s" direction="in"/>
            <arg name="result" type="(bs)" direction="out"/>
          </method>
          <method name="DismissNotification"><arg name="key" type="s" direction="in"/></method>
          <method name="DismissAllNotifications"/>
          <method name="Activity"><arg name="activity" type="a(ssxx)" direction="out"/></method>
          <method name="Show"><arg name="page" type="s" direction="in"/></method>
          <method name="Quit"/>
          <signal name="NotificationsChanged"/>
          <signal name="ActivityChanged"/>
          <signal name="FilesChanged"/>
        </interface>
        """;

    private const string DeviceXml = """
        <interface name="dev.meshsync.Device1">
          <property name="Name" type="s" access="read"/>
          <property name="Fingerprint" type="s" access="read"/>
          <property name="ShortFingerprint" type="s" access="read"/>
          <property name="IsConnected" type="b" access="read"/>
          <property name="ActiveLink" type="s" access="read"/>
          <property name="LastSeen" type="x" access="read"/>
          <property name="LastAddress" type="s" access="read"/>
          <method name="Ring">
            <arg name="on" type="b" direction="in"/><arg name="ok" type="b" direction="out"/>
          </method>
          <method name="SendFile">
            <arg name="path" type="s" direction="in"/><arg name="result" type="(bs)" direction="out"/>
          </method>
          <method name="EnsureWiFi"><arg name="ok" type="b" direction="out"/></method>
          <method name="Forget"/>
        </interface>
        """;

    private const string PairingXml = """
        <interface name="dev.meshsync.Pairing1">
          <property name="Name" type="s" access="read"/>
          <property name="Fingerprint" type="s" access="read"/>
          <property name="ShortFingerprint" type="s" access="read"/>
          <property name="Address" type="s" access="read"/>
          <property name="SeenAt" type="x" access="read"/>
          <method name="Confirm"><arg name="result" type="(bs)" direction="out"/></method>
          <method name="Reject"><arg name="result" type="(bs)" direction="out"/></method>
        </interface>
        """;
}
