---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/SyncActivityLog.cs
  - src/CoreLib/Diagnostics/Log.cs
updated: 2026-08-23
---

# Activity log

Two different things with similar names, kept apart deliberately.

## `SyncActivityLog` - what crossed

`src/CoreLib/SyncActivityLog.cs`.
**In memory only.**
It dies with the process and is never written to disk.

That is the privacy promise in `README.md` made concrete: clipboard traffic is ephemeral by
design, encrypted for the device it is going to, sent straight there, and never stored.

**Mirrored notifications never enter it.**
Not here, not in a cache, and not in a log line carrying their contents.
`AGENTS.md` states this as a prohibition.
See [[notification-mirroring]].

## `Log` - diagnostics

`src/CoreLib/Diagnostics/Log.cs`.
Everything diagnostic goes through it, **never `Console.WriteLine`**.

The Windows daemon is a `WinExe` with no console attached, so anything written to the console is
silently discarded.

| Platform | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\MeshSync\daemon.log` |
| Linux and macOS | `~/.local/share/MeshSync/daemon.log` |
| Android | `adb logcat -s MeshSync` |

`WindowsBleDiscovery` was deleted partly for logging through `Console.WriteLine`.

## See also

[[clipboard-sync]] · [[notification-mirroring]]
