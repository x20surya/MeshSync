---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Transport/SharedFolders.cs
updated: 2026-08-23
---

# Shared folder resolution

`src/CoreLib/Transport/SharedFolders.cs` is **the security boundary of [[remote-browse]]**.
Everything else about browsing is a listing and a file transfer that already existed.

The part that is new is that a remote device now names something for this one to go and find.
A request that names a path is a request that can name the wrong one.

## Why the wire carries no path

`../../../../etc/passwd` is the oldest trick there is, and on Windows it has friends:

- `C:\` offered as a "relative" path
- `\\server\share`
- a drive-relative `C:file`
- symbolic links pointing somewhere else entirely

So the wire carries **the id of a folder a person on this device explicitly shared, plus a
relative path underneath it**. Nothing else.

## The four steps, in order

`TryResolve(id, relative, out resolved)`:

1. **Look the id up.** Not trusted, looked up. Unknown id returns `NoSuchFolder`.
2. **Reject the relative part outright** if `LooksRelative` says no.
3. **Join and fully resolve** with `Path.GetFullPath(Path.Combine(...))`.
4. **Check it is still inside** the folder it came from, after resolution.

**Steps 2 and 4 are both necessary.**
The first stops the obvious cases; the second catches the ones that only become visible once the
operating system has had its say, symlinks included.

An empty relative path is the folder itself, which is the normal first request.

### `LooksRelative`

Backslashes are normalised to forward slashes **first**, so a separator this platform does not use
cannot smuggle a segment past the check. Then:

- rejects anything starting `/`
- rejects anything containing `:` (this is what catches `C:file`, which `Path.IsPathRooted` does
  report but which reads as relative)
- rejects any segment equal to `..`

### `IsInside`

Case-insensitive on Windows, ordinal elsewhere.

**The trailing separator matters**: without it, `/home/photos-private` reads as being inside
`/home/photos`.

## Listing

`TryList` **skips every entry whose `LinkTarget` is not null**, files and directories both.
A link can point outside the shared folder, and following one would hand a peer a way past the
boundary the rest of this class exists to hold.

`UnauthorizedAccessException` returns an **empty listing, not an error**: a folder the user shared
but the process cannot read is empty as far as the peer is concerned, rather than a dialog.

Sorted directories-first then by name, so the listing is stable and reads like a file manager.

## Folder ids

`SHA256(path)` truncated to the first 8 bytes, hex.
Lowercased first on Windows.

Short, **stable across restarts** so a peer's saved reference survives, and **says nothing about
where the folder is**.
The same folder shared twice is the same entry.

## Nothing is shared by default

An empty list is the correct starting state.
A browse against a device that has shared nothing returns nothing, and says so.
Both Windows and the desktop head add Downloads out of the box.

## See also

[[remote-browse]] · [[protocol-payloads]] · [[file-transfer]]
