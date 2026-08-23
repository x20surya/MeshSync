---
type: feature
status: partial
platforms: [windows, android, linux, macos]
tier: wifi
code:
  - src/CoreLib/Transport/BrowseService.cs
  - src/CoreLib/Transport/BrowseProtocol.cs
  - src/CoreLib/Transport/SharedFolders.cs
updated: 2026-08-23
---

# Remote browse

List a paired device's shared folders and fetch a file out of one.
Downloads is shared out of the box on both sides.

## Where it lives

- `src/CoreLib/Transport/BrowseService.cs` drives it.
- `BrowseProtocol.cs` is the framing.
- `SharedFolders.cs` is the security, and is the whole point of the feature.

Three content types: `BrowseRequest` (0x09), `BrowseReply` (0x0A) and `FetchRequest` (0x0B).
Fetching hands off to [[file-transfer]] once the request is accepted.

## `SharedFolders` is the security story

**The wire carries a folder id and a relative path, never a path.**
The relative half is then rejected, joined, resolved, and checked to still be inside the folder it
came from.
All four steps, in that order.

This is the concrete form of the standing rule in `AGENTS.md`: nothing that arrives from a peer
decides where bytes land without being parsed.
The payload is authenticated and comes from a paired device, and it is still parsed rather than
believed.

## What is still open

**Only one direction is verified.**
Browsing a phone's shared folders from the desktop was exercised on hardware: the listing came
back with real sizes and dates, and a fetched `screenshot.png` landed in Downloads.
The phone's own Files page against the desktop runs the same code from the other end and has never
been tapped through.

A browse against a peer that does not speak it times out, says so, and leaves the link unharmed,
which was verified.

## See also

[[file-transfer]] · [[content-types]] · [[wifi-tier]]
