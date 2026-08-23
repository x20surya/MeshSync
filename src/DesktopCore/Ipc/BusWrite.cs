using Tmds.DBus.Protocol;

namespace DesktopCore.Ipc;

/// <summary>
/// The dictionary and variant writing every exported object shares.
///
/// <para><b>The rule this file exists to enforce.</b> A D-Bus dictionary entry is a struct, and
/// every struct is aligned to eight bytes - including the second entry and every one after it.
/// <c>WriteArrayStart(DBusType.DictEntry)</c> does not insert that padding, so a dictionary with
/// more than one entry written that way is malformed, and a malformed message is answered by the
/// bus closing the connection with nothing said. <c>WriteDictionaryEntryStart</c> does insert it.
/// Both have been in <c>Tmds.DBus.Protocol</c> all along; only one of them is correct here.</para>
///
/// <para>Reproduced rather than assumed: a five-entry <c>a{sv}</c> written the first way makes
/// <c>dbus-daemon</c> disconnect the sender mid-reply, and written the second way reads back
/// cleanly from <c>gdbus</c>, <c>busctl</c> and QML.</para>
///
/// <para>The writer is a <b>struct</b>, so it is passed by <c>ref</c> everywhere. Handing it to
/// an <c>Action&lt;MessageWriter&gt;</c> writes the body into a copy that is then discarded, and
/// the message promises bytes it does not have - which is the same silent disconnect from a
/// different direction.</para>
/// </summary>
internal static class BusWrite
{
    /// <summary>One <c>a{sv}</c> entry, into a dictionary that is already open.</summary>
    public static void Entry(ref MessageWriter writer, string name, object value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(name);
        Variant(ref writer, value);
    }

    /// <summary>
    /// A property value as a variant.
    ///
    /// <para>Switching on the runtime type keeps one snapshot of a property set usable for
    /// <c>Get</c>, <c>GetAll</c>, <c>GetManagedObjects</c> and <c>PropertiesChanged</c> alike,
    /// rather than four copies of the same list that can drift apart.</para>
    /// </summary>
    public static void Variant(ref MessageWriter writer, object value)
    {
        switch (value)
        {
            case string text: writer.WriteVariantString(text); break;
            case bool flag: writer.WriteVariantBool(flag); break;
            case uint number: writer.WriteVariantUInt32(number); break;
            case int number: writer.WriteVariantInt32(number); break;
            case long number: writer.WriteVariantInt64(number); break;

            // Never silently wrong: a property whose type was not considered becomes a string
            // rather than a mismatched signature, which a client reads as odd rather than as a
            // protocol error it cannot recover from.
            default: writer.WriteVariantString(value?.ToString() ?? ""); break;
        }
    }

    /// <summary>A whole <c>a{sv}</c>, opened and closed here.</summary>
    public static void Dictionary(ref MessageWriter writer, IReadOnlyDictionary<string, object> values)
    {
        var dictionary = writer.WriteDictionaryStart();
        foreach (var pair in values) Entry(ref writer, pair.Key, pair.Value);
        writer.WriteDictionaryEnd(dictionary);
    }

    /// <summary>
    /// One <c>a{sa{sv}}</c> entry: an interface name and the properties it carries.
    /// The shape <c>InterfacesAdded</c> and <c>GetManagedObjects</c> are both built from.
    /// </summary>
    public static void InterfaceEntry(ref MessageWriter writer, string iface,
                                      IReadOnlyDictionary<string, object> values)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(iface);
        Dictionary(ref writer, values);
    }
}
