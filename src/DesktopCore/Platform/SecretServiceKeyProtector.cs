using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Identity;
using Tmds.DBus.Protocol;

namespace DesktopCore.Platform;

/// <summary>
/// Wraps the device key with a secret held by the desktop's keyring.
///
/// <para><b>What it fixes.</b> Without it the private key sits on disk as a plain PKCS#8 blob,
/// protected by nothing but its file mode. 0600 keeps out other users; it does not keep out
/// anything running as this one, and unlike the Windows and Android cases the key is readable
/// whether or not the app is running - by a backup, by a dotfiles repository that reaches too
/// far, by any stray copy of the home directory.</para>
///
/// <para><b>How.</b> A 32-byte key is stored in the Secret Service, which is
/// <c>org.freedesktop.secrets</c> on every desktop that has a keyring: KWallet on KDE,
/// gnome-keyring on GNOME. The device key is then sealed with it through the project's own
/// AES-256-GCM, so the format matches what Android writes and <c>DeviceIdentity</c> already
/// knows how to read.</para>
///
/// <para><b>What it is not.</b> It is not hardware-backed, and it does not defend against code
/// running as this user at the moment it asks - that code can ask the keyring too. Raising that
/// bar needs a TPM-bound key or a prompt per use, and neither is worth it while the thing being
/// protected is a clipboard. It would be worth it for a vault.</para>
/// </summary>
public sealed class SecretServiceKeyProtector : IKeyProtector, IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";

    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>
    /// What the item is looked up by. Deliberately a single pair.
    ///
    /// <para>D-Bus aligns every dict entry to eight bytes, and this library's writer only aligns
    /// the first one in a hand-rolled array - there is no API to pad between them, and no
    /// <c>a{ss}</c> helper that would. One entry needs no padding, so one entry is what gets
    /// written. It is also enough: the value is unique to this app and this purpose.</para>
    /// </summary>
    private const string AttributeKey = "dev.meshsync.purpose";
    private const string AttributeValue = "device-key-wrap";

    private readonly DBusConnection _connection;
    private readonly string _sessionPath;
    private readonly byte[] _wrappingKey;
    private bool _disposed;

    public string Name => "the desktop keyring";

    private SecretServiceKeyProtector(DBusConnection connection, string sessionPath, byte[] wrappingKey)
    {
        _connection = connection;
        _sessionPath = sessionPath;
        _wrappingKey = wrappingKey;
    }

    /// <summary>
    /// Connects to the keyring and finds or creates the wrapping key.
    ///
    /// Returns null when there is no keyring, or it is locked and will not open. That is a
    /// normal state on a headless box, and the caller falls back to an unwrapped key rather than
    /// refusing to start - a device that cannot load its identity can do nothing at all.
    /// </summary>
    public static async Task<SecretServiceKeyProtector?> TryCreateAsync()
    {
        if (!OperatingSystem.IsLinux()) return null;

        DBusConnection? connection = null;

        try
        {
            connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);

            string session = await OpenSessionAsync(connection).ConfigureAwait(false);
            byte[] key = await LoadOrCreateKeyAsync(connection, session).ConfigureAwait(false);

            Log.Write("Identity", "The device key is wrapped by the desktop keyring.");
            return new SecretServiceKeyProtector(connection, session, key);
        }
        catch (Exception ex)
        {
            Log.Write("Identity",
                $"No usable keyring ({ex.GetType().Name}); the device key stays unwrapped on disk.");
            connection?.Dispose();
            return null;
        }
    }

    public byte[] Protect(byte[] plaintext) => CryptoEngine.Encrypt(plaintext, _wrappingKey);

    public byte[]? TryUnprotect(byte[] stored)
    {
        try
        {
            return CryptoEngine.Decrypt(stored, _wrappingKey);
        }
        catch (Exception ex)
        {
            // Expected when the keyring entry has gone: a cleared wallet, a restore onto another
            // machine. Costs a re-pair, which is visible, and is precisely the case this is meant
            // to make unrecoverable.
            Log.Write("Identity", "The keyring could not unwrap the stored identity", ex);
            return null;
        }
    }

    // ──────────────────────────────── the keyring

    /// <summary>
    /// Opens a "plain" session: secrets cross the bus unencrypted.
    ///
    /// The alternative negotiates a DH key with the keyring, which protects the value from
    /// anything watching the session bus. Anything that can watch this user's session bus can
    /// already read the file being protected, so it would buy nothing here.
    /// </summary>
    private static Task<string> OpenSessionAsync(DBusConnection connection)
    {
        var message = Build(connection, ServicePath, ServiceInterface, "OpenSession", "sv",
            (ref MessageWriter w) =>
            {
                w.WriteString("plain");
                w.WriteSignature("s");
                w.WriteString("");
            });

        return connection.CallMethodAsync(message, static (Message reply, object? _) =>
        {
            var reader = reply.GetBodyReader();
            reader.ReadVariantValue();                 // the algorithm's output, unused for "plain"
            return reader.ReadObjectPathAsString();
        }, null);
    }

    private static async Task<byte[]> LoadOrCreateKeyAsync(DBusConnection connection, string session)
    {
        string[] found = await SearchAsync(connection).ConfigureAwait(false);
        // A path the keyring could not give us is no use, and constructing one throws.
        found = found.Where(f => f.Length > 1).ToArray();

        if (found.Length > 0)
        {
            byte[]? existing = await ReadSecretAsync(connection, session, found[0]).ConfigureAwait(false);
            if (existing is { Length: 32 }) return existing;

            Log.Write("Identity", "The keyring held an unusable wrapping key; replacing it.");
        }

        byte[] fresh = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await CreateItemAsync(connection, session, fresh).ConfigureAwait(false);
        return fresh;
    }

    private static Task<string[]> SearchAsync(DBusConnection connection)
    {
        var message = Build(connection, ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            (ref MessageWriter w) => WriteAttributes(ref w));

        return connection.CallMethodAsync(message, static (Message reply, object? _) =>
        {
            var reader = reply.GetBodyReader();
            var unlocked = reader.ReadArrayOfObjectPath();      // locked items are ignored: an
            return unlocked.Select(p => p.ToString()).ToArray(); // unlock needs a user prompt
        }, null);
    }

    /// <summary>
    /// Reads one item's secret.
    ///
    /// <para>Asked of the item rather than of the service. <c>Service.GetSecrets</c> answers with
    /// a dictionary whose values are structs, and a struct is aligned to eight bytes wherever it
    /// sits - padding this library's reader will not insert for you when the fields are read one
    /// by one. <c>Item.GetSecret</c> returns the struct on its own at the start of the body,
    /// where it is aligned already and there is nothing to get wrong.</para>
    /// </summary>
    private static Task<byte[]?> ReadSecretAsync(DBusConnection connection, string session, string item)
    {
        var message = Build(connection, item, ItemInterface, "GetSecret", "o",
            (ref MessageWriter w) => w.WriteObjectPath(session));

        return connection.CallMethodAsync(message, static (Message reply, object? _) =>
        {
            // (oayays): the session it was encoded for, parameters, the value, the content type.
            var reader = reply.GetBodyReader();
            reader.ReadObjectPath();
            reader.ReadArrayOfByte();
            return (byte[]?)reader.ReadArrayOfByte();
        }, null);
    }

    /// <summary>
    /// Creates the item, then attaches the attribute it is later found by.
    ///
    /// <para>Split in two because every dictionary here has to hold exactly one entry: the label
    /// is mandatory, the attributes are what <c>SearchItems</c> matches on, and writing both in
    /// one <c>a{sv}</c> would need padding between the entries that this library's writer has no
    /// way to emit. Setting the second through the Properties interface sidesteps it.</para>
    /// </summary>
    private static async Task CreateItemAsync(DBusConnection connection, string session, byte[] secret)
    {
        string item = await CreateAsync(connection, session, secret).ConfigureAwait(false);
        if (item.Length == 0 || item == "/") return;

        await connection.CallMethodAsync(Build(connection, item, PropertiesInterface, "Set", "ssv",
            (ref MessageWriter w) =>
            {
                w.WriteString(ItemInterface);
                w.WriteString("Attributes");
                w.WriteSignature("a{ss}");
                WriteAttributes(ref w);
            })).ConfigureAwait(false);
    }

    private static Task<string> CreateAsync(DBusConnection connection, string session, byte[] secret)
    {
        var message = Build(connection, DefaultCollection, CollectionInterface, "CreateItem",
            "a{sv}(oayays)b",
            (ref MessageWriter w) =>
            {
                // One property. The label is required; the attributes follow separately.
                var properties = w.WriteArrayStart(DBusType.DictEntry);
                w.WriteString($"{ItemInterface}.Label");
                w.WriteVariantString("Mesh Sync device key");
                w.WriteArrayEnd(properties);

                // The Secret struct, written inline: session, parameters, value, content type.
                w.WriteObjectPath(session);
                w.WriteArray(Array.Empty<byte>());
                w.WriteArray(secret);
                w.WriteString("application/octet-stream");

                w.WriteBool(true);                // replace an existing item with these attributes
            });

        return connection.CallMethodAsync(message, static (Message reply, object? _) =>
            reply.GetBodyReader().ReadObjectPathAsString(), null);
    }

    private static void WriteAttributes(ref MessageWriter writer)
    {
        var dict = writer.WriteArrayStart(DBusType.DictEntry);
        writer.WriteString(AttributeKey);
        writer.WriteString(AttributeValue);
        writer.WriteArrayEnd(dict);
    }

    /// <summary>
    /// Builds a call. Not a using declaration and not an <c>Action</c>: a using variable cannot
    /// be passed by reference, and <c>MessageWriter</c> is a struct, so an ordinary delegate
    /// receives a copy and every argument written into it is silently thrown away.
    /// </summary>
    private static MessageBuffer Build(DBusConnection connection, string path, string iface,
                                       string member, string signature, MessageArgs args)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, path, iface, member, signature, MessageFlags.None);
            args(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Writes a call's arguments, by reference. See <see cref="Build"/>.</summary>
    private delegate void MessageArgs(ref MessageWriter writer);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Array.Clear(_wrappingKey);
        try { _connection.Dispose(); } catch { }
    }
}
