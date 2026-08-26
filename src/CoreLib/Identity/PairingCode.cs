using System;
using System.Collections.Generic;

namespace CoreLib.Identity
{
    /// <summary>
    /// The <c>meshsync://</c> pairing code: how one is built, and how one is read.
    ///
    /// <para><b>Why it is here and not in a head.</b> Every device both shows a code and scans
    /// one, so the format had grown four implementations - two building it and two reading it -
    /// with the validation rules written out separately in each. The Android client's reader was
    /// the one that had drifted: it pulled the address and key straight out of the URI without
    /// looking at either, so a damaged code produced a link that connected and then failed every
    /// decryption. A format shared by every head is exactly the kind of answer no head may
    /// reimplement.</para>
    ///
    /// <para><b>The address is optional and the key is not.</b> Since v0.4 the inviting device
    /// also advertises a <see cref="Transport.Ble.MeshBeacon"/> derived from the key already in
    /// the payload, so a joiner can find it over the radio with no network at all. A code
    /// carrying no address is that case, not a malformed one. The key is what the session is
    /// agreed against, so it is validated before anything is stored.</para>
    /// </summary>
    public sealed class PairingCode
    {
        /// <summary>The scheme, which is also what every head registers as a deep link.</summary>
        public const string Scheme = "meshsync";

        private PairingCode(string publicKey, string? address, string? meshName)
        {
            PublicKey = publicKey;
            Address = address;
            MeshName = meshName;
        }

        /// <summary>The inviting device's public key. Always present and always valid.</summary>
        public string PublicKey { get; }

        /// <summary>
        /// Where to dial over Wi-Fi, with a port when it is not the default. Null when the
        /// inviter offered no address, which is the radio-only case.
        /// </summary>
        public string? Address { get; }

        /// <summary>The mesh's name, for a joiner that has not been given one. May be null.</summary>
        public string? MeshName { get; }

        /// <summary>
        /// Builds the code a joiner scans.
        ///
        /// <para>The mesh name rides along so a device that scans this joins something with a
        /// name rather than pairing with an anonymous address. It is omitted rather than sent
        /// empty, because an empty name is not a name.</para>
        /// </summary>
        public static string Build(string publicKey, string? address, string? meshName)
        {
            string uri = $"{Scheme}://pair?";

            if (!string.IsNullOrWhiteSpace(address))
                uri += $"ip={Uri.EscapeDataString(address!.Trim())}&";

            uri += $"key={Uri.EscapeDataString(publicKey ?? "")}";

            if (!string.IsNullOrWhiteSpace(meshName))
                uri += $"&mesh={Uri.EscapeDataString(meshName!.Trim())}";

            return uri;
        }

        /// <summary>
        /// Reads a pairing code, or says why it will not do.
        ///
        /// <para>The message is written to be shown to a person: it is the only explanation they
        /// get when a scan does not work, and "that is not a Mesh Sync code" and "that code is
        /// damaged" send someone to two different places.</para>
        /// </summary>
        public static bool TryParse(string? raw, out PairingCode? code, out string error)
        {
            code = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "That code is empty.";
                return false;
            }

            if (!Uri.TryCreate(raw!.Trim(), UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            {
                error = "That is not a Mesh Sync pairing code.";
                return false;
            }

            var query = ParseQuery(uri.Query);

            if (!query.TryGetValue("key", out string? key) || !DeviceIdentity.IsValidPublicKey(key))
            {
                error = "That code carries no usable key. It may be damaged, or from an older version.";
                return false;
            }

            query.TryGetValue("ip", out string? address);
            query.TryGetValue("mesh", out string? mesh);

            code = new PairingCode(key, Blank(address), Blank(mesh));
            error = "";
            return true;
        }

        private static string? Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        /// <summary>
        /// Reads a URI query into a dictionary. Hand-rolled rather than pulling in
        /// <c>HttpUtility</c> for three keys that this class also generates.
        /// </summary>
        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;

                result[Uri.UnescapeDataString(pair.Substring(0, eq))] =
                    Uri.UnescapeDataString(pair.Substring(eq + 1));
            }

            return result;
        }
    }
}
