using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreLib.Identity
{
    /// <summary>One device this one is paired with.</summary>
    public sealed class PeerRecord
    {
        /// <summary>Base64 SubjectPublicKeyInfo. The only field that actually authenticates anything.</summary>
        public string PublicKey { get; set; } = "";

        /// <summary>Friendly name the peer announced, for display.</summary>
        public string? Name { get; set; }

        /// <summary>
        /// Last address this peer was reachable at. A hint, not an identity - a DHCP lease
        /// change moves it, which is precisely why pairing is keyed on the public key instead.
        /// </summary>
        public string? LastAddress { get; set; }

        public DateTimeOffset LastSeenUtc { get; set; }

        /// <summary>
        /// Fingerprint of the peer that vouched for this one, or null if it was scanned
        /// directly. Kept so the user can see how a device got here and revoke a chain of
        /// introductions if they stop trusting the introducer.
        /// </summary>
        public string? IntroducedBy { get; set; }

        /// <summary>
        /// Which halves of a GATT link this peer's radio can take, as it last announced.
        ///
        /// <para>Remembered rather than asked for, because the arbiter needs it <em>before</em> a
        /// radio link exists - and the answer usually arrives over Wi-Fi, long before the two
        /// devices ever meet on the air. Null means never announced, which is read as both halves:
        /// the optimistic reading, and the one every build before wire version 4 used.</para>
        /// </summary>
        public Transport.BleCapability? BleCapability { get; set; }

        [JsonIgnore]
        public string Fingerprint => DeviceIdentity.FingerprintOf(PublicKey);
    }

    /// <summary>Shape of the file on disk. Versioned so the format can move later.</summary>
    internal sealed class PeerFile
    {
        /// <summary>2 added <see cref="MeshKey"/>. A version 1 file still reads, and costs no re-pair.</summary>
        public int Version { get; set; } = 2;

        public string? MeshName { get; set; }

        /// <summary>
        /// Base64 of the 32-byte discovery key this mesh shares, or null before one is minted.
        ///
        /// <para><b>It is not a credential.</b> It decides which advertisements are worth
        /// connecting to and nothing else. Nothing authorises on it, and no session key is ever
        /// derived from it - see <c>MeshBeacon</c>.</para>
        /// </summary>
        public string? MeshKey { get; set; }

        public List<PeerRecord> Peers { get; set; } = new();
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PeerFile))]
    internal partial class PeerFileContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// The devices this one is paired with, keyed by public key fingerprint.
    ///
    /// <para>Replaces a pair of preference strings holding one address and one key. A set is
    /// needed for two independent reasons: any device can pair with any other now, so there is
    /// no single "host" to point at; and every peer has its own session key, because one key
    /// shared across a mesh would let any paired device read traffic meant for another pair.
    /// That distinction is invisible with two devices and matters immediately with three.</para>
    ///
    /// <para>Address is stored but never trusted as identity. Two devices recognise each other
    /// by key; the address is only a hint about where to dial first.</para>
    /// </summary>
    public sealed class PeerRegistry
    {
        private const string FileName = "peers.json";

        private readonly object _gate = new();
        private readonly Dictionary<string, PeerRecord> _peers = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _path;

        private string _meshName = "";
        private byte[]? _meshKey;

        /// <summary>Raised whenever the set changes, so a dashboard can redraw its device list.</summary>
        public event Action? Changed;

        /// <summary>
        /// What the user calls this set of devices - "Surya's Mesh" rather than "a computer".
        ///
        /// <para>A property of the group, but there is no coordinator to hold it, so the rule is
        /// that the device which starts the mesh names it and devices that join learn the name
        /// when they pair. The pairing code carries it for exactly that reason.</para>
        ///
        /// <para>Renaming later is local to the device it is done on. Propagating a rename would
        /// need a rule for which of two names wins, and every simple answer to that either
        /// ping-pongs between devices or lets the least recently used one overwrite the rest.</para>
        /// </summary>
        public string MeshName
        {
            get { lock (_gate) return _meshName; }
            set
            {
                string trimmed = (value ?? "").Trim();
                if (trimmed.Length > MaxMeshNameLength) trimmed = trimmed.Substring(0, MaxMeshNameLength).Trim();

                lock (_gate)
                {
                    if (_meshName == trimmed) return;
                    _meshName = trimmed;
                }

                Diagnostics.Log.Write("Peers", $"Mesh name set to \"{trimmed}\".");
                Save();
                Changed?.Invoke();
            }
        }

        /// <summary>Long enough for "Someone's Mesh", short enough to fit a notification.</summary>
        public const int MaxMeshNameLength = 40;

        /// <summary>The mesh name, or a sensible stand-in when nothing has been chosen.</summary>
        public string MeshNameOrDefault => string.IsNullOrWhiteSpace(MeshName) ? "your mesh" : MeshName;

        /// <summary>Adopts a name only if this device has not been given one. Used when joining.</summary>
        public void AdoptMeshName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!string.IsNullOrWhiteSpace(MeshName)) return;

            MeshName = name!;
        }

        /// <summary>
        /// The 32-byte key every device in this mesh shares, so a scan can tell them apart from
        /// everybody else's before it connects. Null until one has been minted.
        /// </summary>
        public byte[]? MeshKey
        {
            get { lock (_gate) return _meshKey?.ToArray(); }
        }

        public bool HasMeshKey { get { lock (_gate) return _meshKey != null; } }

        /// <summary>
        /// Mints one if this device has none, and returns whatever it now holds.
        ///
        /// Called on the first v0.4 run. The key is then offered to every connected peer over the
        /// existing authenticated links, so upgrading costs no re-pair.
        /// </summary>
        public byte[] MintMeshKeyIfMissing()
        {
            lock (_gate)
            {
                if (_meshKey != null) return _meshKey.ToArray();
                _meshKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Transport.Ble.MeshBeacon.KeyLength);
            }

            Diagnostics.Log.Write("Peers", "Minted a mesh discovery key.");
            Save();
            Changed?.Invoke();

            lock (_gate) return _meshKey!.ToArray();
        }

        /// <summary>
        /// Takes a key offered by a peer, if it should win.
        ///
        /// <para><b>Lowest key wins, compared as 32 unsigned bytes.</b> Deterministic, with no
        /// timestamps and no coordinator, so two halves of a mesh that minted separately converge
        /// the first time any device from each can reach the other - and converge on the same
        /// answer without exchanging another message. A device that adopts a new key re-advertises
        /// within one beacon epoch.</para>
        ///
        /// <para>A paired device could push an all-zero key and steer which advertisements this
        /// mesh bothers with. That is true and it changes nothing: a paired device is already
        /// trusted with the clipboard, the notifications and the files, and the key affects who
        /// this mesh <em>looks for</em>, never who it lets in.</para>
        /// </summary>
        public bool AdoptMeshKey(byte[]? offered)
        {
            if (offered == null || offered.Length != Transport.Ble.MeshBeacon.KeyLength) return false;

            lock (_gate)
            {
                if (_meshKey != null && Compare(_meshKey, offered) <= 0) return false;
                _meshKey = offered.ToArray();
            }

            Diagnostics.Log.Write("Peers", "Adopted a mesh discovery key from a peer.");
            Save();
            Changed?.Invoke();
            return true;
        }

        private static int Compare(byte[] left, byte[] right)
        {
            for (int i = 0; i < left.Length && i < right.Length; i++)
            {
                if (left[i] != right[i]) return left[i] < right[i] ? -1 : 1;
            }

            return left.Length.CompareTo(right.Length);
        }

        private PeerRegistry(string? path) => _path = path;

        /// <summary>Loads the registry, starting empty if there is nothing saved yet.</summary>
        public static PeerRegistry LoadOrCreate(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(directory));

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            var registry = new PeerRegistry(path);

            if (!File.Exists(path)) return registry;

            try
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(path), PeerFileContext.Default.PeerFile);
                if (file == null) return registry;

                registry._meshName = (file.MeshName ?? "").Trim();

                // Absent in a version 1 file, which loads unchanged. The beacon simply stays off
                // until a key is minted or offered, which is exactly how every build before this
                // one behaved.
                if (!string.IsNullOrWhiteSpace(file.MeshKey))
                {
                    try
                    {
                        var key = Convert.FromBase64String(file.MeshKey!);
                        if (key.Length == Transport.Ble.MeshBeacon.KeyLength) registry._meshKey = key;
                        else Diagnostics.Log.Write("Peers", "Ignoring a stored mesh key that is the wrong length.");
                    }
                    catch (FormatException)
                    {
                        Diagnostics.Log.Write("Peers", "Ignoring a stored mesh key that will not decode.");
                    }
                }

                if (file.Peers == null) return registry;

                foreach (var peer in file.Peers)
                {
                    // A record whose key will not parse can never be used for anything, and
                    // keeping it would put an undeletable ghost in the user's device list.
                    if (!DeviceIdentity.IsValidPublicKey(peer.PublicKey))
                    {
                        Diagnostics.Log.Write("Peers", "Dropping a stored peer with an unreadable public key.");
                        continue;
                    }

                    registry._peers[peer.Fingerprint] = peer;
                }

                Diagnostics.Log.Write("Peers", $"Loaded {registry._peers.Count} paired device(s).");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Peers", "Could not read the peer registry; starting empty. Devices will need re-pairing.", ex);
            }

            return registry;
        }

        /// <summary>
        /// What each peer announced its radio can do, for the route policy.
        ///
        /// Peers that have never said are simply absent, and the arbiter reads an absent entry as
        /// both halves - the same optimistic reading it has always used, now confined to peers
        /// that genuinely have not told us rather than applied to every peer unconditionally.
        /// </summary>
        public IReadOnlyDictionary<string, Transport.BleCapability> Capabilities
        {
            get
            {
                lock (_gate)
                {
                    var map = new Dictionary<string, Transport.BleCapability>(StringComparer.OrdinalIgnoreCase);
                    foreach (var peer in _peers.Values)
                    {
                        if (peer.BleCapability.HasValue) map[peer.Fingerprint] = peer.BleCapability.Value;
                    }

                    return map;
                }
            }
        }

        /// <summary>Creates an in-memory registry that is never written to disk. For tests.</summary>
        public static PeerRegistry CreateEphemeral() => new(null);

        public IReadOnlyList<PeerRecord> Peers
        {
            get { lock (_gate) return _peers.Values.ToList(); }
        }

        public int Count
        {
            get { lock (_gate) return _peers.Count; }
        }

        public bool IsEmpty => Count == 0;

        public bool IsTrusted(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return false;
            lock (_gate) return _peers.ContainsKey(fingerprint);
        }

        public PeerRecord? Find(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return null;
            lock (_gate) return _peers.TryGetValue(fingerprint, out var peer) ? peer : null;
        }

        /// <summary>
        /// Adds a device, or updates what is known about one already present.
        ///
        /// Returns false for a key that will not parse, so a mistyped pairing code fails
        /// visibly at the point of entry rather than as a link that never connects.
        /// </summary>
        public bool Trust(string publicKey, string? name = null, string? address = null, string? introducedBy = null)
        {
            if (!DeviceIdentity.IsValidPublicKey(publicKey))
            {
                Diagnostics.Log.Write("Peers", "Refusing to trust an unreadable public key.");
                return false;
            }

            string fingerprint = DeviceIdentity.FingerprintOf(publicKey);
            bool added;

            lock (_gate)
            {
                added = !_peers.TryGetValue(fingerprint, out var existing);

                if (added)
                {
                    _peers[fingerprint] = new PeerRecord
                    {
                        PublicKey = publicKey,
                        Name = name,
                        LastAddress = address,
                        LastSeenUtc = DateTimeOffset.UtcNow,
                        IntroducedBy = introducedBy
                    };
                }
                else
                {
                    // Only fill in what we were told. A hello that carries no address must not
                    // erase the address we successfully dialled last time.
                    if (!string.IsNullOrWhiteSpace(name)) existing!.Name = name;
                    if (!string.IsNullOrWhiteSpace(address)) existing!.LastAddress = address;
                    existing!.LastSeenUtc = DateTimeOffset.UtcNow;
                }
            }

            Diagnostics.Log.Write("Peers", added
                ? $"Paired with {name ?? "a device"} ({DeviceIdentity.Shorten(fingerprint)})."
                : $"Updated {name ?? "a device"} ({DeviceIdentity.Shorten(fingerprint)}).");

            Save();
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// Forgets where a peer was last seen, leaving it paired.
        ///
        /// <para>For the one case where the stored address is not merely old but provably wrong:
        /// a dial to it was answered by a different paired device. That happens whenever a DHCP
        /// lease is reused - routine on a phone acting as a hotspot, where two devices that were
        /// both once at the same address are both still in the registry.</para>
        ///
        /// <para>Clearing it rather than overwriting it is deliberate: this device has just
        /// learned where the peer is <i>not</i>, and nothing about where it is. The peer supplies
        /// a real address the next time it connects or announces one, so the registry heals
        /// itself without a re-pair.</para>
        /// </summary>
        public void ForgetAddress(string fingerprint)
        {
            lock (_gate)
            {
                if (!_peers.TryGetValue(fingerprint, out var peer)) return;
                if (string.IsNullOrWhiteSpace(peer.LastAddress)) return;

                peer.LastAddress = null;
            }

            Save();
            Changed?.Invoke();
        }

        /// <summary>Records that a peer was reachable, and where. Cheap enough to call on every connect.</summary>
        public void NoteSeen(string fingerprint, string? address = null, string? name = null,
                             Transport.BleCapability? capability = null)
        {
            bool changed = false;

            lock (_gate)
            {
                if (!_peers.TryGetValue(fingerprint, out var peer)) return;

                peer.LastSeenUtc = DateTimeOffset.UtcNow;

                if (!string.IsNullOrWhiteSpace(address) && peer.LastAddress != address &&
                    !WouldLosePort(peer.LastAddress, address))
                {
                    peer.LastAddress = address;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(name) && peer.Name != name)
                {
                    peer.Name = name;
                    changed = true;
                }

                // Kept so the arbiter has an answer before a radio link exists. It is usually
                // learned over Wi-Fi, which is the point: a device that cannot advertise says so
                // long before the two ever meet on the air.
                if (capability.HasValue && peer.BleCapability != capability)
                {
                    peer.BleCapability = capability;
                    changed = true;
                }
            }

            // Only written when something durable moved. Otherwise every heartbeat would
            // rewrite the file.
            if (changed)
            {
                Save();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// True when taking the new address would throw away a port the old one carried.
        ///
        /// <para><b>Why this guard exists.</b> The address a connection reports is deliberately
        /// port-less: on an accepted socket the peer's port is its ephemeral source port, which
        /// is useless to dial back. That is right for learning where an unknown peer lives, and
        /// wrong for a peer whose <c>host:port</c> a human already supplied in a pairing code -
        /// which is exactly what happens on the first connect after joining, so the port is lost
        /// before it is ever used.</para>
        ///
        /// <para>It hides in the field, because every device in the field listens on 45001 and a
        /// bare host dials there anyway. It does not hide when two devices share one machine and
        /// cannot share a port - the one arrangement this project relies on to exercise the mesh
        /// without a third piece of hardware. The second device dialled the default port forever
        /// and was refused by nothing, because nothing was listening there.</para>
        ///
        /// <para>Only the same host is protected. A device that genuinely moved must still be
        /// able to record where it moved to.</para>
        /// </summary>
        private static bool WouldLosePort(string? stored, string candidate)
        {
            if (string.IsNullOrWhiteSpace(stored)) return false;

            // Exactly one colon, with a port after it. A bare IPv6 address has several, and
            // mistaking one for a host and port would pin a peer to an address it never had.
            int colon = stored.IndexOf(':');
            if (colon <= 0 || stored.IndexOf(':', colon + 1) >= 0) return false;
            if (!int.TryParse(stored[(colon + 1)..], out int port) || port is < 1 or > 65535) return false;

            return string.Equals(stored[..colon], candidate, StringComparison.OrdinalIgnoreCase);
        }

        public bool Forget(string fingerprint)
        {
            bool removed;
            lock (_gate) removed = _peers.Remove(fingerprint);

            if (!removed) return false;

            Diagnostics.Log.Write("Peers", $"Forgot {DeviceIdentity.Shorten(fingerprint)}.");
            Save();
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// Every peer except the one asking, for introduction.
        ///
        /// Pairing by QR alone is one scan per pair, which is fine for two devices and tedious
        /// at four. Sharing what this device already trusts lets a new device learn the rest of
        /// the set from one scan. It is deliberately only what <em>this</em> device has paired
        /// with directly or accepted before - it never forwards a stranger.
        /// </summary>
        public IReadOnlyList<PeerRecord> PeersToIntroduceTo(string requesterFingerprint)
        {
            lock (_gate)
            {
                return _peers.Values
                    .Where(p => !string.Equals(p.Fingerprint, requesterFingerprint, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void Save()
        {
            if (_path == null) return;

            try
            {
                PeerFile file;
                lock (_gate) file = new PeerFile
                {
                    MeshName = _meshName,
                    MeshKey = _meshKey == null ? null : Convert.ToBase64String(_meshKey),
                    Peers = _peers.Values.ToList(),
                };

                string json = JsonSerializer.Serialize(file, PeerFileContext.Default.PeerFile);

                // Written beside the target and moved into place, so an interrupted write
                // cannot leave a half-file that reads as "no devices paired".
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Peers", "Could not save the peer registry", ex);
            }
        }
    }
}
