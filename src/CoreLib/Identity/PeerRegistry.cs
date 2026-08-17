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

        [JsonIgnore]
        public string Fingerprint => DeviceIdentity.FingerprintOf(PublicKey);
    }

    /// <summary>Shape of the file on disk. Versioned so the format can move later.</summary>
    internal sealed class PeerFile
    {
        public int Version { get; set; } = 1;
        public string? MeshName { get; set; }
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

        /// <summary>Records that a peer was reachable, and where. Cheap enough to call on every connect.</summary>
        public void NoteSeen(string fingerprint, string? address = null, string? name = null)
        {
            bool changed = false;

            lock (_gate)
            {
                if (!_peers.TryGetValue(fingerprint, out var peer)) return;

                peer.LastSeenUtc = DateTimeOffset.UtcNow;

                if (!string.IsNullOrWhiteSpace(address) && peer.LastAddress != address)
                {
                    peer.LastAddress = address;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(name) && peer.Name != name)
                {
                    peer.Name = name;
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
                lock (_gate) file = new PeerFile { MeshName = _meshName, Peers = _peers.Values.ToList() };

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
