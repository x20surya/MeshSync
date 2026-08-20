using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CoreLib.Transport
{
    /// <summary>One folder this device has agreed to let paired devices look inside.</summary>
    public sealed class SharedFolder
    {
        public SharedFolder(string id, string name, string path)
        {
            Id = id;
            Name = name;
            Path = path;
        }

        /// <summary>Stable across restarts, derived from the path, and meaningless to a peer.</summary>
        public string Id { get; }

        /// <summary>What the other device shows in its list.</summary>
        public string Name { get; }

        /// <summary>The real folder. Never sent anywhere.</summary>
        public string Path { get; }
    }

    /// <summary>
    /// The set of folders a paired device may browse, and the only thing that turns a request
    /// into a path.
    ///
    /// <para><b>This is the security boundary of the browse feature.</b> Everything else about
    /// browsing is a listing and a file transfer that already existed. The part that is new, and
    /// the part worth being careful about, is that a remote device now names something for this
    /// one to go and find. A request that names a path is a request that can name the wrong one:
    /// <c>../../../../etc/passwd</c> is the oldest trick there is, and on Windows it has friends -
    /// <c>C:\</c> as a "relative" path, <c>\\server\share</c>, a drive-relative <c>C:file</c>, and
    /// symbolic links that point somewhere else entirely.</para>
    ///
    /// <para><b>So the wire never carries a path at all.</b> It carries the id of a folder that a
    /// person on this device explicitly shared, plus a relative path underneath it. The id is
    /// looked up rather than trusted; the relative part is rejected outright if it is rooted or
    /// contains a traversal segment, then joined, then fully resolved, and then checked to still
    /// be inside the folder it came from. Rejecting up front and checking again after resolution
    /// are both necessary: the first stops the obvious cases and the second catches the ones that
    /// only become visible once the operating system has had its say, symlinks included.</para>
    ///
    /// <para><b>Nothing is shared by default.</b> An empty list is the correct starting state -
    /// a browse against a device that has shared nothing returns nothing, and says so.</para>
    /// </summary>
    public sealed class SharedFolders
    {
        private readonly object _gate = new();
        private readonly List<SharedFolder> _folders = new();

        /// <summary>Why a request could not be turned into something on disk.</summary>
        public enum Refusal
        {
            None,
            NoSuchFolder,
            OutsideTheFolder,
            NotFound
        }

        public IReadOnlyList<SharedFolder> All()
        {
            lock (_gate) return _folders.ToList();
        }

        public int Count { get { lock (_gate) return _folders.Count; } }

        /// <summary>
        /// Shares a folder, or does nothing if it is already shared.
        ///
        /// The id is derived from the resolved path so that the same folder shared twice is the
        /// same entry, and so that a peer's saved reference to it survives a restart.
        /// </summary>
        public SharedFolder? Add(string path, string? name = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string full;
            try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
            catch { return null; }

            if (!Directory.Exists(full)) return null;

            string id = IdFor(full);

            lock (_gate)
            {
                var existing = _folders.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
                if (existing != null) return existing;

                string label = string.IsNullOrWhiteSpace(name)
                    ? (System.IO.Path.GetFileName(full) is { Length: > 0 } leaf ? leaf : full)
                    : name!;

                var folder = new SharedFolder(id, label, full);
                _folders.Add(folder);
                return folder;
            }
        }

        public bool Remove(string id)
        {
            lock (_gate) return _folders.RemoveAll(f => string.Equals(f.Id, id, StringComparison.Ordinal)) > 0;
        }

        public void Clear()
        {
            lock (_gate) _folders.Clear();
        }

        /// <summary>
        /// Turns a folder id and a relative path into a real one, or refuses.
        ///
        /// <paramref name="relative"/> is whatever a peer sent, and is treated accordingly.
        /// </summary>
        public Refusal TryResolve(string id, string relative, out string resolved)
        {
            resolved = "";

            SharedFolder? folder;
            lock (_gate) folder = _folders.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

            if (folder == null) return Refusal.NoSuchFolder;

            relative ??= "";

            // The empty path is the folder itself, which is the normal first request.
            if (relative.Length == 0)
            {
                resolved = folder.Path;
                return Refusal.None;
            }

            if (!LooksRelative(relative)) return Refusal.OutsideTheFolder;

            string candidate;
            try { candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder.Path, relative)); }
            catch { return Refusal.OutsideTheFolder; }

            if (!IsInside(folder.Path, candidate)) return Refusal.OutsideTheFolder;

            resolved = candidate;
            return Refusal.None;
        }

        /// <summary>Lists a folder for a peer, having first established it is allowed to.</summary>
        public Refusal TryList(string id, string relative, out IReadOnlyList<BrowseEntry> entries)
        {
            entries = Array.Empty<BrowseEntry>();

            var refusal = TryResolve(id, relative, out string path);
            if (refusal != Refusal.None) return refusal;

            if (!Directory.Exists(path)) return Refusal.NotFound;

            var listed = new List<BrowseEntry>();

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(path))
                {
                    var info = new DirectoryInfo(directory);

                    // A link can point outside the shared folder, and following one would hand a
                    // peer a way past the boundary the rest of this class exists to hold.
                    if (info.LinkTarget != null) continue;

                    listed.Add(new BrowseEntry(info.Name, isDirectory: true, 0, info.LastWriteTimeUtc));
                }

                foreach (string file in Directory.EnumerateFiles(path))
                {
                    var info = new FileInfo(file);
                    if (info.LinkTarget != null) continue;

                    listed.Add(new BrowseEntry(info.Name, isDirectory: false, info.Length, info.LastWriteTimeUtc));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A folder the user shared but the process cannot read is empty as far as the
                // peer is concerned, rather than an error worth a dialog.
                return Refusal.None;
            }
            catch (IOException)
            {
                return Refusal.NotFound;
            }

            // Folders first, then names, so the listing is stable and reads like a file manager.
            entries = listed
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return Refusal.None;
        }

        /// <summary>Resolves a peer's request for one file, and confirms it is a file.</summary>
        public Refusal TryResolveFile(string id, string relative, out string path)
        {
            var refusal = TryResolve(id, relative, out path);
            if (refusal != Refusal.None) return refusal;

            if (!File.Exists(path))
            {
                path = "";
                return Refusal.NotFound;
            }

            return Refusal.None;
        }

        /// <summary>
        /// Whether a peer-supplied path is relative in the way this expects, before any of it is
        /// joined to anything.
        ///
        /// <para>Rooted paths are the obvious rejection. The rest are the ones that are only
        /// obvious once seen: a bare traversal segment, a Windows drive-relative path such as
        /// <c>C:file</c> which <see cref="System.IO.Path.IsPathRooted"/> does report, and a UNC
        /// path. Backslashes are normalised first so that a separator this platform does not use
        /// cannot smuggle a segment past the check.</para>
        /// </summary>
        private static bool LooksRelative(string relative)
        {
            string normalised = relative.Replace('\\', '/');

            if (normalised.StartsWith('/')) return false;
            if (normalised.Contains(':')) return false;

            foreach (string segment in normalised.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == "..") return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a fully resolved path is genuinely underneath a folder.
        ///
        /// The trailing separator matters: without it "/home/photos-private" reads as being
        /// inside "/home/photos".
        /// </summary>
        private static bool IsInside(string folder, string candidate)
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(folder, candidate, comparison)) return true;

            string prefix = folder.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? folder
                : folder + System.IO.Path.DirectorySeparatorChar;

            return candidate.StartsWith(prefix, comparison);
        }

        /// <summary>Short, stable, and says nothing about where the folder is.</summary>
        private static string IdFor(string fullPath)
        {
            string key = OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : fullPath;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash, 0, 8);
        }
    }
}
