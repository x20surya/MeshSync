using CoreLib.Diagnostics;
using DesktopCore.Ipc;
using Tmds.DBus.Protocol;

namespace DesktopCore.Tray;

/// <summary>One row of the tray menu, and what pressing it does.</summary>
internal sealed record TrayMenuItem(
    int Id,
    string Label,
    bool Enabled = true,
    bool IsSeparator = false,
    string IconName = "",
    Action? Invoke = null);

/// <summary>
/// The tray icon's menu, served over <c>com.canonical.dbusmenu</c>.
///
/// <para><b>Why this is hand-written.</b> A StatusNotifierItem does not carry its menu: it
/// carries the object path of one, and every desktop then fetches the layout over dbusmenu. So
/// owning the tray item means owning this too. Avalonia's <c>TrayIcon</c> supplied both and could
/// express neither an icon name, a tooltip, nor an attention state - see
/// <c>DesktopCore/Tray/TrayItem.cs</c>.</para>
///
/// <para><b>The shape that makes it hard.</b> <c>GetLayout</c> returns
/// <c>(u(ia{sv}av))</c>: a revision and a node, where a node is an id, a dictionary of
/// properties, and an array of variants each holding another node. It is the deepest thing
/// written anywhere in this project, and it is only tractable because every dictionary here goes
/// through <c>BusWrite</c> - the same alignment that a hand-rolled
/// <c>WriteArrayStart(DBusType.DictEntry)</c> gets wrong, silently, by having the bus close the
/// connection.</para>
///
/// <para><b>Flat, deliberately.</b> No submenus in this version. A submenu is another level of
/// that recursion for a menu whose longest branch is "ring one device", and the layout is
/// rebuilt on every open anyway.</para>
/// </summary>
internal sealed class TrayMenu : IPathMethodHandler
{
    public const string Path = "/MenuBar";
    private const string Interface = "com.canonical.dbusmenu";

    /// <summary>dbusmenu revision 4. Anything older lacks the properties used here.</summary>
    private const uint DbusMenuVersion = 4;

    private readonly Func<IReadOnlyList<TrayMenuItem>> _build;
    private readonly Action<string> _notifyLayoutChanged;

    private IReadOnlyList<TrayMenuItem> _items = Array.Empty<TrayMenuItem>();
    private uint _revision = 1;

    public TrayMenu(Func<IReadOnlyList<TrayMenuItem>> build, Action<string> notifyLayoutChanged)
    {
        _build = build;
        _notifyLayoutChanged = notifyLayoutChanged;
    }

    string IPathMethodHandler.Path => Path;

    public bool HandlesChildPaths => false;

    public uint Revision => _revision;

    /// <summary>
    /// Rebuilds the menu and says whether it actually differs.
    ///
    /// <para>Called from <c>AboutToShow</c> rather than kept in step continuously: the contents
    /// depend on which devices are reachable, and the only moment that has to be right is the
    /// moment the menu opens.</para>
    /// </summary>
    public bool Rebuild()
    {
        var next = _build();
        string before = Describe(_items);
        _items = next;

        if (Describe(next) == before) return false;

        _revision++;
        return true;
    }

    private static string Describe(IReadOnlyList<TrayMenuItem> items) =>
        string.Join("|", items.Select(i => $"{i.Id}:{i.Label}:{i.Enabled}:{i.IsSeparator}"));

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
            if (iface != Interface) { context.ReplyUnknownMethodError(); return default; }

            switch (member)
            {
                case "GetLayout": ReplyLayout(context); return default;
                case "GetGroupProperties": ReplyGroupProperties(context); return default;
                case "GetProperty": ReplyProperty(context); return default;
                case "AboutToShow": ReplyAboutToShow(context); return default;
                case "AboutToShowGroup": ReplyAboutToShowGroup(context); return default;
                case "Event": HandleEvent(context); return default;
                case "EventGroup": HandleEventGroup(context); return default;
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

    private void HandleProperties(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        reader.ReadString();   // the interface, which is only ever ours

        if (member == "GetAll")
        {
            var writer = context.CreateReplyWriter("a{sv}");
            var dictionary = writer.WriteDictionaryStart();
            BusWrite.Entry(ref writer, "Version", DbusMenuVersion);
            BusWrite.Entry(ref writer, "Status", "normal");
            BusWrite.Entry(ref writer, "TextDirection", "ltr");
            writer.WriteDictionaryEnd(dictionary);
            context.Reply(writer.CreateMessage());
            return;
        }

        if (member == "Get")
        {
            string name = reader.ReadString();
            var writer = context.CreateReplyWriter("v");

            switch (name)
            {
                case "Version": writer.WriteVariantUInt32(DbusMenuVersion); break;
                case "Status": writer.WriteVariantString("normal"); break;
                case "TextDirection": writer.WriteVariantString("ltr"); break;
                default: context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name); return;
            }

            context.Reply(writer.CreateMessage());
            return;
        }

        context.ReplyUnknownMethodError();
    }

    /// <summary>
    /// The whole menu as one node with the items as its children.
    ///
    /// <para>Depth and the requested property list are both ignored on purpose: the menu is a
    /// dozen rows with no submenus, so answering in full is cheaper than working out what was
    /// asked for, and every client copes with being given more than it asked.</para>
    /// </summary>
    private void ReplyLayout(MethodContext context)
    {
        Rebuild();

        var writer = context.CreateReplyWriter("u(ia{sv}av)");
        writer.WriteUInt32(_revision);

        // The root: no label, and a marker saying its children are a submenu, which is what
        // makes a client render them at all.
        writer.WriteStructureStart();
        writer.WriteInt32(0);

        var rootProperties = writer.WriteDictionaryStart();
        BusWrite.Entry(ref writer, "children-display", "submenu");
        writer.WriteDictionaryEnd(rootProperties);

        var children = writer.WriteArrayStart(DBusType.Variant);

        foreach (var item in _items)
        {
            // A variant is a signature followed by the value. The struct inside then aligns
            // itself to eight bytes, which WriteStructureStart does.
            writer.WriteSignature("(ia{sv}av)");
            writer.WriteStructureStart();
            writer.WriteInt32(item.Id);

            var properties = writer.WriteDictionaryStart();
            WriteItemProperties(ref writer, item);
            writer.WriteDictionaryEnd(properties);

            // No submenus, so every item's child array is empty - but it still has to be there.
            var grandchildren = writer.WriteArrayStart(DBusType.Variant);
            writer.WriteArrayEnd(grandchildren);
        }

        writer.WriteArrayEnd(children);
        context.Reply(writer.CreateMessage());
    }

    private static void WriteItemProperties(ref MessageWriter writer, TrayMenuItem item)
    {
        if (item.IsSeparator)
        {
            BusWrite.Entry(ref writer, "type", "separator");
            return;
        }

        BusWrite.Entry(ref writer, "label", item.Label);
        BusWrite.Entry(ref writer, "enabled", item.Enabled);
        BusWrite.Entry(ref writer, "visible", true);

        if (item.IconName.Length > 0) BusWrite.Entry(ref writer, "icon-name", item.IconName);
    }

    private void ReplyGroupProperties(MethodContext context)
    {
        Rebuild();

        var writer = context.CreateReplyWriter("a(ia{sv})");
        var array = writer.WriteArrayStart(DBusType.Struct);

        foreach (var item in _items)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(item.Id);

            var properties = writer.WriteDictionaryStart();
            WriteItemProperties(ref writer, item);
            writer.WriteDictionaryEnd(properties);
        }

        writer.WriteArrayEnd(array);
        context.Reply(writer.CreateMessage());
    }

    private void ReplyProperty(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        int id = reader.ReadInt32();
        string name = reader.ReadString();

        var item = _items.FirstOrDefault(i => i.Id == id);
        var writer = context.CreateReplyWriter("v");

        switch (name)
        {
            case "label": writer.WriteVariantString(item?.Label ?? ""); break;
            case "enabled": writer.WriteVariantBool(item?.Enabled ?? false); break;
            case "visible": writer.WriteVariantBool(true); break;
            case "type": writer.WriteVariantString(item?.IsSeparator == true ? "separator" : "standard"); break;
            case "icon-name": writer.WriteVariantString(item?.IconName ?? ""); break;
            default: writer.WriteVariantString(""); break;
        }

        context.Reply(writer.CreateMessage());
    }

    /// <summary>
    /// Rebuilds before the menu is shown, and says whether the client should fetch it again.
    ///
    /// <para>This is the whole reason the menu can be honest about what is reachable: a tray
    /// menu is read the instant before it is drawn, so nothing has to be pushed.</para>
    /// </summary>
    private void ReplyAboutToShow(MethodContext context)
    {
        bool changed = Rebuild();

        var writer = context.CreateReplyWriter("b");
        writer.WriteBool(changed);
        context.Reply(writer.CreateMessage());

        if (changed) _notifyLayoutChanged("the menu was rebuilt before opening");
    }

    private void ReplyAboutToShowGroup(MethodContext context)
    {
        bool changed = Rebuild();

        var writer = context.CreateReplyWriter("aiai");

        var updated = writer.WriteArrayStart(DBusType.Int32);
        if (changed) writer.WriteInt32(0);
        writer.WriteArrayEnd(updated);

        var removed = writer.WriteArrayStart(DBusType.Int32);
        writer.WriteArrayEnd(removed);

        context.Reply(writer.CreateMessage());
    }

    private void HandleEvent(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        int id = reader.ReadInt32();
        string what = reader.ReadString();

        // Reply first. An action that shows a window or sends a file must not keep the menu on
        // screen while it happens, and the client is waiting for this return before it closes.
        context.Reply(context.CreateReplyWriter("").CreateMessage());

        if (what != "clicked") return;

        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item?.Invoke == null) return;

        _ = Task.Run(() =>
        {
            try { item.Invoke(); }
            catch (Exception ex) { Log.Write("Tray", $"The menu item \"{item.Label}\" failed", ex); }
        });
    }

    private void HandleEventGroup(MethodContext context)
    {
        // Nothing is ever refused, so the "could not handle" array is always empty. Answering
        // properly matters: a client that gets an error here stops sending events at all.
        var writer = context.CreateReplyWriter("ai");
        var array = writer.WriteArrayStart(DBusType.Int32);
        writer.WriteArrayEnd(array);
        context.Reply(writer.CreateMessage());
    }

    private const string IntrospectXml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <method name="GetLayout">
            <arg name="parentId" type="i" direction="in"/>
            <arg name="recursionDepth" type="i" direction="in"/>
            <arg name="propertyNames" type="as" direction="in"/>
            <arg name="revision" type="u" direction="out"/>
            <arg name="layout" type="(ia{sv}av)" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg name="ids" type="ai" direction="in"/>
            <arg name="propertyNames" type="as" direction="in"/>
            <arg name="properties" type="a(ia{sv})" direction="out"/>
          </method>
          <method name="GetProperty">
            <arg name="id" type="i" direction="in"/><arg name="name" type="s" direction="in"/>
            <arg name="value" type="v" direction="out"/>
          </method>
          <method name="Event">
            <arg name="id" type="i" direction="in"/><arg name="eventId" type="s" direction="in"/>
            <arg name="data" type="v" direction="in"/><arg name="timestamp" type="u" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg name="events" type="a(isvu)" direction="in"/>
            <arg name="idErrors" type="ai" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg name="id" type="i" direction="in"/><arg name="needUpdate" type="b" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg name="ids" type="ai" direction="in"/>
            <arg name="updatesNeeded" type="ai" direction="out"/>
            <arg name="idErrors" type="ai" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg name="updatedProps" type="a(ia{sv})"/><arg name="removedProps" type="a(ias)"/>
          </signal>
          <signal name="LayoutUpdated">
            <arg name="revision" type="u"/><arg name="parent" type="i"/>
          </signal>
          <signal name="ItemActivationRequested">
            <arg name="id" type="i"/><arg name="timestamp" type="u"/>
          </signal>
        </interface>
        """;
}

/// <summary>Names the tray half needs. Kept apart from the widget's, which are a different API.</summary>
internal static class BusNamesTray
{
    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    public const string Watcher = "org.kde.StatusNotifierWatcher";
    public const string WatcherPath = "/StatusNotifierWatcher";
    public const string ItemInterface = "org.kde.StatusNotifierItem";
    public const string ItemPath = "/StatusNotifierItem";
}
