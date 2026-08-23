using CoreLib.Diagnostics;
using CoreLib.Transport;
using Tmds.DBus.Protocol;

namespace DesktopCore.Bluetooth;

/// <summary>
/// The peripheral half on Linux: advertise the mesh service and serve it.
///
/// <para><b>Why this is a different shape from the central.</b> Being the central is D-Bus calls
/// outward. Being the peripheral means BlueZ calls <em>in</em>: it asks for the object tree, reads
/// properties off it, and invokes <c>WriteValue</c> and <c>StartNotify</c> on characteristics we
/// own. So this exports objects rather than consuming them, and answers a handful of standard
/// interfaces by hand.</para>
///
/// <para><b>Not yet accepted by BlueZ.</b> The tree is exported and BlueZ does call
/// <c>GetManagedObjects</c> on it, but it rejects the reply and closes the connection: the nested
/// <c>a{oa{sa{sv}}}</c> needs each dict entry aligned to eight bytes and the writer is not doing
/// that for successive entries. Until that is right this class registers, fails, and stands
/// aside - which costs nothing, because <c>BleRoleRules</c> then makes this device the central
/// and the peer advertises instead.</para>
///
/// <para>One handler serves the whole tree. <c>HandlesChildPaths</c> means BlueZ's calls to the
/// service and both characteristics all arrive here and are dispatched on the path, which is far
/// less machinery than an object per node for a tree that is four nodes big.</para>
/// </summary>
public sealed class LinuxBlePeripheral : IPathMethodHandler, IDisposable
{
    private const string Root = "/dev/meshsync";
    private const string AdvertPath = Root + "/advert0";
    private const string ServicePath = Root + "/service0";
    private const string InboxPath = ServicePath + "/char0";
    private const string OutboxPath = ServicePath + "/char1";

    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string AdvertisementInterface = "org.bluez.LEAdvertisement1";

    private readonly BlueZ _bluez;
    private readonly string _adapterPath;
    private readonly string _localName;
    private bool _notifying;
    private bool _disposed;

    /// <summary>A frame a connected central wrote to our inbox.</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>A central subscribed or unsubscribed, which is the closest thing to connect here.</summary>
    public event Action<bool>? SubscriptionChanged;

    public string Path => Root;
    public bool HandlesChildPaths => true;
    public bool IsSubscribed => _notifying;

    private LinuxBlePeripheral(BlueZ bluez, string adapterPath, string localName)
    {
        _bluez = bluez;
        _adapterPath = adapterPath;
        _localName = localName;
    }

    /// <summary>
    /// Exports the tree, registers it with BlueZ, and starts advertising. Returns null when the
    /// adapter cannot do either, which is a normal state rather than a failure.
    /// </summary>
    public static async Task<LinuxBlePeripheral?> TryStartAsync(BlueZ bluez, string adapterPath, string localName)
    {
        var peripheral = new LinuxBlePeripheral(bluez, adapterPath, localName);

        try
        {
            bluez.Connection.AddMethodHandler(peripheral);

            await bluez.CallAsync(adapterPath, BlueZ.GattManagerInterface, "RegisterApplication", "oa{sv}", (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(Root);
                var options = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(options);
            }).ConfigureAwait(false);

            await bluez.CallAsync(adapterPath, BlueZ.AdvertisingManagerInterface, "RegisterAdvertisement", "oa{sv}", (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(AdvertPath);
                var options = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(options);
            }).ConfigureAwait(false);

            Log.Write("BlePeripheral", "Advertising the mesh service; other devices can now connect to this one.");
            return peripheral;
        }
        catch (Exception ex)
        {
            // BlueZ closes the connection outright when it dislikes an exported tree, so this is
            // usually "connection closed by peer" with nothing more to say. Reported once, at one
            // line, because the central half carries the tier regardless.
            // The message matters now, and what it says has moved.
            //
            // Before the dictionaries were aligned this was DBusConnectionClosedException:
            // GetManagedObjects returned a malformed a{oa{sa{sv}}} and BlueZ hung up without a
            // word. With WriteDictionaryEntryStart in place, RegisterApplication *succeeds* -
            // BlueZ reads the whole tree and takes it - and the failure has moved on to
            // RegisterAdvertisement, which answers org.bluez.Error.Failed: Failed to register
            // advertisement.
            //
            // That is a different problem and not a marshalling one: the adapter reports
            // SupportedInstances 12 with ActiveInstances 0 and MaxAdvLen 251, so it is neither
            // full nor short of room, and dropping LocalName from the packet changes nothing.
            // Whatever it is, BlueZ is now talking rather than hanging up, which is the
            // difference between a diagnosable problem and a silent one.
            Log.Write("BlePeripheral",
                $"BlueZ would not accept the GATT application: {ex.GetType().Name}: {ex.Message}. This device will scan rather than advertise.");
            return null;
        }
    }

    // ──────────────────────────────── serving

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        string path = request.PathAsString ?? "";
        string iface = request.InterfaceAsString ?? "";
        string member = request.MemberAsString ?? "";

        try
        {
            if (context.IsDBusIntrospectRequest) { ReplyIntrospect(context, path); return default; }

            if (iface == ObjectManagerInterface && member == "GetManagedObjects")
            {
                ReplyManagedObjects(context);
                return default;
            }

            if (iface == PropertiesInterface)
            {
                HandleProperties(context, request, path, member);
                return default;
            }

            if (iface == BlueZ.CharacteristicInterface)
            {
                HandleCharacteristic(context, request, path, member);
                return default;
            }

            if (iface == AdvertisementInterface && member == "Release")
            {
                Log.Write("BlePeripheral", "BlueZ released the advertisement.");
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                return default;
            }

            context.ReplyUnknownMethodError();
        }
        catch (Exception ex)
        {
            Log.Write("BlePeripheral", $"{iface}.{member} failed", ex);
            try { context.ReplyError("org.bluez.Error.Failed", ex.Message); } catch { }
        }

        return default;
    }

    private void HandleCharacteristic(MethodContext context, Message request, string path, string member)
    {
        switch (member)
        {
            case "WriteValue" when path == InboxPath:
            {
                var reader = request.GetBodyReader();
                byte[] frame = reader.ReadArrayOfByte();

                context.Reply(context.CreateReplyWriter("").CreateMessage());

                try { FrameReceived?.Invoke(frame); }
                catch (Exception ex) { Log.Write("BlePeripheral", "A frame handler threw", ex); }
                break;
            }

            case "StartNotify" when path == OutboxPath:
                _notifying = true;
                Log.Write("BlePeripheral", "A device subscribed to this one's outbox.");
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                SubscriptionChanged?.Invoke(true);
                break;

            case "StopNotify" when path == OutboxPath:
                _notifying = false;
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                SubscriptionChanged?.Invoke(false);
                break;

            case "ReadValue":
            {
                // Nothing is ever read this way; the outbox pushes and the inbox is write-only.
                var writer = context.CreateReplyWriter("ay");
                writer.WriteArray(Array.Empty<byte>());
                context.Reply(writer.CreateMessage());
                break;
            }

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void HandleProperties(MethodContext context, Message request, string path, string member)
    {
        if (member == "GetAll")
        {
            var reader = request.GetBodyReader();
            string wanted = reader.ReadString();

            var writer = context.CreateReplyWriter("a{sv}");
            WriteProperties(ref writer, path, wanted);
            context.Reply(writer.CreateMessage());
            return;
        }

        if (member == "Get")
        {
            var reader = request.GetBodyReader();
            string wanted = reader.ReadString();
            string name = reader.ReadString();

            var writer = context.CreateReplyWriter("v");
            if (!WriteOneProperty(ref writer, path, wanted, name))
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                return;
            }

            context.Reply(writer.CreateMessage());
            return;
        }

        context.ReplyUnknownMethodError();
    }

    /// <summary>The whole exported tree, which is what BlueZ asks for when registering the app.</summary>
    private void ReplyManagedObjects(MethodContext context)
    {
        var writer = context.CreateReplyWriter("a{oa{sa{sv}}}");

        // a{oa{sa{sv}}} - three nested dictionaries, and every entry of every one of them has to
        // start on an eight-byte boundary. WriteArrayStart(DictEntry) opens an array of the right
        // element type and does not insert that padding, so the second entry onwards lands one to
        // seven bytes early and the whole message is malformed - which BlueZ answers by closing
        // the connection with nothing said. WriteDictionaryEntryStart is the padding.
        var outer = writer.WriteDictionaryStart();

        WriteObject(ref writer, ServicePath, BlueZ.ServiceInterface);
        WriteObject(ref writer, InboxPath, BlueZ.CharacteristicInterface);
        WriteObject(ref writer, OutboxPath, BlueZ.CharacteristicInterface);

        writer.WriteDictionaryEnd(outer);
        context.Reply(writer.CreateMessage());
    }

    private void WriteObject(ref MessageWriter writer, string path, string iface)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteObjectPath(path);

        var interfaces = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString(iface);
        WriteProperties(ref writer, path, iface);
        writer.WriteDictionaryEnd(interfaces);
    }

    private void WriteProperties(ref MessageWriter writer, string path, string iface)
    {
        var dict = writer.WriteDictionaryStart();

        if (iface == BlueZ.ServiceInterface && path == ServicePath)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("UUID");
            writer.WriteVariantString(BleProtocol.ServiceUuid.ToString("D"));

            writer.WriteDictionaryEntryStart();
            writer.WriteString("Primary");
            writer.WriteVariantBool(true);
        }
        else if (iface == BlueZ.CharacteristicInterface)
        {
            bool inbox = path == InboxPath;

            writer.WriteDictionaryEntryStart();
            writer.WriteString("UUID");
            writer.WriteVariantString((inbox ? BleProtocol.InboxCharacteristicUuid
                                             : BleProtocol.OutboxCharacteristicUuid).ToString("D"));

            writer.WriteDictionaryEntryStart();
            writer.WriteString("Service");
            writer.WriteVariantObjectPath(ServicePath);

            writer.WriteDictionaryEntryStart();
            writer.WriteString("Flags");
            writer.WriteSignature("as");
            writer.WriteArray(inbox ? new[] { "write", "write-without-response" } : new[] { "notify" });

            if (!inbox)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("Notifying");
                writer.WriteVariantBool(_notifying);
            }
        }
        else if (iface == AdvertisementInterface && path == AdvertPath)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("Type");
            writer.WriteVariantString("peripheral");

            writer.WriteDictionaryEntryStart();
            writer.WriteString("ServiceUUIDs");
            writer.WriteSignature("as");
            writer.WriteArray(new[] { BleProtocol.ServiceUuid.ToString("D") });

            writer.WriteDictionaryEntryStart();
            writer.WriteString("LocalName");
            writer.WriteVariantString(_localName);
        }

        writer.WriteDictionaryEnd(dict);
    }

    private bool WriteOneProperty(ref MessageWriter writer, string path, string iface, string name)
    {
        if (iface == AdvertisementInterface && path == AdvertPath)
        {
            switch (name)
            {
                case "Type": writer.WriteVariantString("peripheral"); return true;
                case "LocalName": writer.WriteVariantString(_localName); return true;
                case "ServiceUUIDs":
                    writer.WriteSignature("as");
                    writer.WriteArray(new[] { BleProtocol.ServiceUuid.ToString("D") });
                    return true;
            }
        }

        if (iface == BlueZ.CharacteristicInterface && path == OutboxPath && name == "Notifying")
        {
            writer.WriteVariantBool(_notifying);
            return true;
        }

        return false;
    }

    private static void ReplyIntrospect(MethodContext context, string path)
    {
        // Enough for BlueZ to walk the tree. It reads properties over the Properties interface
        // rather than trusting this, so it does not need to be exhaustive.
        string xml = path switch
        {
            Root => Introspect(ObjectManagerInterface, "service0", "advert0"),
            ServicePath => Introspect(BlueZ.ServiceInterface, "char0", "char1"),
            InboxPath or OutboxPath => Introspect(BlueZ.CharacteristicInterface),
            AdvertPath => Introspect(AdvertisementInterface),
            _ => Introspect(),
        };

        var writer = context.CreateReplyWriter("s");
        writer.WriteString(xml);
        context.Reply(writer.CreateMessage());
    }

    private static string Introspect(string? iface = null, params string[] children)
    {
        string nodes = string.Concat(children.Select(c => $"<node name=\"{c}\"/>"));
        string body = iface == null ? "" : $"<interface name=\"{iface}\"/>";

        return "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" " +
               "\"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">" +
               $"<node>{body}<interface name=\"{PropertiesInterface}\"/>{nodes}</node>";
    }

    // ──────────────────────────────── notifying

    /// <summary>
    /// Pushes a frame to the subscribed central.
    ///
    /// A GATT notification is delivered by announcing that the characteristic's <c>Value</c>
    /// changed, so this is a <c>PropertiesChanged</c> signal rather than a method call.
    /// </summary>
    public bool Notify(byte[] frame)
    {
        if (_disposed || !_notifying) return false;

        try
        {
            using var writer = _bluez.Connection.GetMessageWriter();
            writer.WriteSignalHeader(null!, OutboxPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");

            writer.WriteString(BlueZ.CharacteristicInterface);

            var changed = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("Value");
            writer.WriteSignature("ay");
            writer.WriteArray(frame);
            writer.WriteDictionaryEnd(changed);

            writer.WriteArray(Array.Empty<string>());   // nothing invalidated

            return _bluez.Connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            Log.Write("BlePeripheral", "Could not push a notification", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _ = _bluez.CallAsync(_adapterPath, BlueZ.AdvertisingManagerInterface, "UnregisterAdvertisement", "o",
                (ref MessageWriter w) => w.WriteObjectPath(AdvertPath));
            _ = _bluez.CallAsync(_adapterPath, BlueZ.GattManagerInterface, "UnregisterApplication", "o",
                (ref MessageWriter w) => w.WriteObjectPath(Root));
        }
        catch { /* Shutting down; BlueZ drops it when the connection goes anyway. */ }

        FrameReceived = null;
        SubscriptionChanged = null;
    }
}
