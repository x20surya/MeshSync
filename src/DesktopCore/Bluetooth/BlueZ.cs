using CoreLib.Diagnostics;
using Tmds.DBus.Protocol;

namespace DesktopCore.Bluetooth;

/// <summary>
/// Writes a method call's arguments.
///
/// <para>By reference, deliberately. <c>MessageWriter</c> is a struct, so handing it to an
/// ordinary <c>Action&lt;MessageWriter&gt;</c> passes a copy and every write lands in it and is
/// thrown away - leaving a message whose header promises a body it does not have, which
/// dbus-daemon answers by closing the connection without a word.</para>
/// </summary>
public delegate void MessageArgs(ref MessageWriter writer);

/// <summary>A property that changed on one BlueZ object.</summary>
public readonly record struct PropertyChange(string Path, string Interface,
                                             Dictionary<string, VariantValue> Changed);

/// <summary>One object BlueZ has told us about, and the interfaces it implements.</summary>
public sealed class BlueZObject
{
    public required string Path { get; init; }
    public required Dictionary<string, Dictionary<string, VariantValue>> Interfaces { get; init; }

    public bool Has(string iface) => Interfaces.ContainsKey(iface);

    public VariantValue? Property(string iface, string name) =>
        Interfaces.TryGetValue(iface, out var props) && props.TryGetValue(name, out var value)
            ? (VariantValue?)value
            : null;

    public string? String(string iface, string name)
    {
        var v = Property(iface, name);
        try { return v?.GetString(); } catch { return null; }
    }

    public bool Bool(string iface, string name)
    {
        var v = Property(iface, name);
        try { return v?.GetBool() ?? false; } catch { return false; }
    }

    public IReadOnlyList<string> Strings(string iface, string name)
    {
        var v = Property(iface, name);
        try { return v?.GetArray<string>() ?? []; } catch { return []; }
    }
}

/// <summary>
/// The thin layer over BlueZ's D-Bus API.
///
/// <para><b>Why D-Bus and not a library.</b> There is no .NET Bluetooth package that targets
/// Linux at all - the one the Windows and Android clients use ships for android, ios,
/// maccatalyst and windows and nothing else. BlueZ's whole interface is D-Bus, so this is the
/// API rather than a wrapper around one.</para>
///
/// <para>Everything here is the <em>central</em> half: finding devices, connecting, and talking
/// to their characteristics. Serving a GATT service is a different shape - it means exporting
/// objects rather than calling them - and lives in its own file.</para>
/// </summary>
public sealed class BlueZ : IDisposable
{
    public const string Service = "org.bluez";
    public const string AdapterInterface = "org.bluez.Adapter1";
    public const string DeviceInterface = "org.bluez.Device1";
    public const string ServiceInterface = "org.bluez.GattService1";
    public const string CharacteristicInterface = "org.bluez.GattCharacteristic1";
    public const string AdvertisingManagerInterface = "org.bluez.LEAdvertisingManager1";
    public const string GattManagerInterface = "org.bluez.GattManager1";

    private const string ObjectManager = "org.freedesktop.DBus.ObjectManager";
    private const string Properties = "org.freedesktop.DBus.Properties";

    private DBusConnection? _connection;
    private bool _disposed;

    public DBusConnection Connection => _connection ?? throw new InvalidOperationException("Not connected.");

    /// <summary>Connects to the system bus, or returns null when there is no BlueZ to talk to.</summary>
    public static async Task<BlueZ?> TryConnectAsync()
    {
        if (!OperatingSystem.IsLinux()) return null;

        var bluez = new BlueZ();

        try
        {
            bluez._connection = new DBusConnection(DBusAddress.System!);
            await bluez._connection.ConnectAsync().ConfigureAwait(false);

            // Proves BlueZ is actually there, rather than only that a bus is.
            var objects = await bluez.GetObjectsAsync().ConfigureAwait(false);
            if (objects.Count == 0)
            {
                Log.Write("Ble", "The system bus has no BlueZ objects; Bluetooth is off.");
                bluez.Dispose();
                return null;
            }

            return bluez;
        }
        catch (Exception ex)
        {
            Log.Write("Ble", "Could not reach BlueZ on the system bus", ex);
            bluez.Dispose();
            return null;
        }
    }

    /// <summary>Everything BlueZ currently knows about: adapters, devices, services, characteristics.</summary>
    public async Task<List<BlueZObject>> GetObjectsAsync()
    {
        return await Connection.CallMethodAsync(BuildGetObjects(), static (Message message, object? _) =>
        {
            var result = new List<BlueZObject>();
            var reader = message.GetBodyReader();

            // a{oa{sa{sv}}} - path, then interface, then property name to value.
            var outer = reader.ReadArrayStart(DBusType.DictEntry);
            while (reader.HasNext(outer))
            {
                string path = reader.ReadObjectPathAsString();
                var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>(StringComparer.Ordinal);

                var ifaceArray = reader.ReadArrayStart(DBusType.DictEntry);
                while (reader.HasNext(ifaceArray))
                {
                    string iface = reader.ReadString();
                    var props = new Dictionary<string, VariantValue>(StringComparer.Ordinal);

                    var propArray = reader.ReadArrayStart(DBusType.DictEntry);
                    while (reader.HasNext(propArray))
                    {
                        string name = reader.ReadString();
                        props[name] = reader.ReadVariantValue();
                    }

                    interfaces[iface] = props;
                }

                result.Add(new BlueZObject { Path = path, Interfaces = interfaces });
            }

            return result;
        }, null).ConfigureAwait(false);
    }

    private MessageBuffer BuildGetObjects()
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, "/", ObjectManager, "GetManagedObjects", "", MessageFlags.None);
        return writer.CreateMessage();
    }

    private MessageBuffer BuildCall(string path, string iface, string member,
                                    string signature, MessageArgs? args)
    {
        // Not a using declaration: a using variable cannot be passed by ref, and by ref is the
        // whole point here.
        var writer = Connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, path, iface, member, signature, MessageFlags.None);
            args?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Calls a method that takes no arguments and returns nothing.</summary>
    public Task CallAsync(string path, string iface, string member) =>
        Connection.CallMethodAsync(BuildCall(path, iface, member, "", null));

    /// <summary>Calls a method, writing its arguments with <paramref name="args"/>.</summary>
    public Task CallAsync(string path, string iface, string member, string signature,
                          MessageArgs args) =>
        Connection.CallMethodAsync(BuildCall(path, iface, member, signature, args));

    /// <summary>Reads one property.</summary>
    public Task<VariantValue> GetPropertyAsync(string path, string iface, string name) =>
        Connection.CallMethodAsync(
            BuildCall(path, Properties, "Get", "ss", (ref MessageWriter w) => { w.WriteString(iface); w.WriteString(name); }),
            static (Message message, object? _) => message.GetBodyReader().ReadVariantValue(), null);

    /// <summary>
    /// Watches <c>PropertiesChanged</c> across every BlueZ object, which is how a characteristic
    /// announces a notification and how a device announces it has connected or gone away.
    /// </summary>
    public async Task WatchPropertiesAsync(Action<PropertyChange> onChanged)
    {
        var rule = new MatchRule
        {
            Sender = Service,
            Interface = Properties,
            Member = "PropertiesChanged",
        };

        await Connection.AddMatchAsync(rule,
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                string iface = reader.ReadString();

                var changed = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
                var array = reader.ReadArrayStart(DBusType.DictEntry);
                while (reader.HasNext(array))
                {
                    string name = reader.ReadString();
                    changed[name] = reader.ReadVariantValue();
                }

                return new PropertyChange(message.PathAsString ?? "", iface, changed);
            },
            (Notification<PropertyChange> notification) =>
            {
                // IsCompletion has to be checked first: reading Exception on an ordinary
                // notification throws, which turns every signal into a logged error.
                if (notification.IsCompletion || !notification.HasValue) return;

                try { onChanged(notification.Value); }
                catch (Exception ex) { Log.Write("Ble", "A property handler threw", ex); }
            },
            false, ObserverFlags.None, null).ConfigureAwait(false);
    }

    /// <summary>Watches for objects appearing, which is how a scan reports what it found.</summary>
    public async Task WatchInterfacesAddedAsync(Action<string> onAdded)
    {
        var rule = new MatchRule
        {
            Sender = Service,
            Interface = ObjectManager,
            Member = "InterfacesAdded",
        };

        await Connection.AddMatchAsync(rule,
            static (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString(),
            (Notification<string> notification) =>
            {
                if (notification.IsCompletion || !notification.HasValue) return;

                try { onAdded(notification.Value); }
                catch (Exception ex) { Log.Write("Ble", "An added-object handler threw", ex); }
            },
            false, ObserverFlags.None, null).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _connection?.Dispose(); } catch { }
        _connection = null;
    }
}

/// <summary>What this machine's radio can do, for <c>BleRoleRules</c>.</summary>
public static class BlueZCapability
{
    /// <summary>
    /// Reports whether an adapter is present and powered, and whether it can advertise.
    ///
    /// <para>Advertising is what decides the role. An adapter with only <c>org.bluez.Adapter1</c>
    /// can scan and connect out; one that also has <c>LEAdvertisingManager1</c> and
    /// <c>GattManager1</c> can serve a service and be connected to. Reporting this honestly is
    /// what lets <c>BleRoleRules</c> hand the peripheral half to whichever device can actually
    /// perform it, rather than agreeing an arrangement neither end can carry out.</para>
    /// </summary>
    public static async Task<(bool Present, bool CanAdvertise, string? AdapterPath, string Detail)>
        ProbeAsync(BlueZ bluez)
    {
        var objects = await bluez.GetObjectsAsync().ConfigureAwait(false);

        var adapter = objects.FirstOrDefault(o => o.Has(BlueZ.AdapterInterface));
        if (adapter == null) return (false, false, null, "no Bluetooth adapter");

        if (!adapter.Bool(BlueZ.AdapterInterface, "Powered"))
            return (false, false, adapter.Path, "the adapter is powered off");

        bool canAdvertise = adapter.Has(BlueZ.AdvertisingManagerInterface)
                         && adapter.Has(BlueZ.GattManagerInterface);

        string name = adapter.String(BlueZ.AdapterInterface, "Name") ?? adapter.Path;

        return (true, canAdvertise, adapter.Path,
            canAdvertise ? $"{name} can scan and advertise" : $"{name} can scan only");
    }
}
