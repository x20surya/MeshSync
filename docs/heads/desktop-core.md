---
type: head
status: shipped
platforms: [linux, macos]
tier: either
code:
  - src/DesktopCore/Daemon.cs
  - src/DesktopCore/Paths.cs
updated: 2026-08-23
---

# Desktop core

The running device for Linux and macOS, with no UI and no platform assumptions beyond POSIX paths.
Both [[desktop-shell]] and [[linux-daemon]] are front ends over this.

`Daemon.cs` is the whole device: identity and registry loading, the Wi-Fi links, payload dispatch,
the dial loop, [[pairing]], and the clipboard behind an interface so a session with no helper
still runs.

Its dial loop runs every **15s** with a **6s** per-peer timeout, and can be **nudged** to run now
rather than at the next interval, which confirming a pairing does so a freshly confirmed device
connects immediately. The loop also runs `BleServer.CheckHeartbeat()`, because the inbound radio
link has no loop of its own - it was written to be called from here and never was, which left a
peripheral link whose central had walked away showing as connected for ever.

The pairing code it shows is `meshsync://pair?ip=…&key=…&mesh=…`, the same shape the Windows
daemon puts in its QR, because the phone parses one format and a second would be a second thing to
keep in step. The port rides along **only when it is not the default**.

## What is in it

| Folder | Holds |
|---|---|
| `Bluetooth/` | the BlueZ tier - `BlueZ.cs`, `LinuxBleCentral`, `LinuxBlePeripheral`, `LinuxBleServer` |
| `Clipboard/` | `IClipboardBridge` and its three implementations |
| `Ipc/` | [[dbus-ipc]], in flight |
| `Platform/` | `Ringer`, `DesktopNotifier`, `Autostart`, `SecretServiceKeyProtector`, `FileTransportPreferenceStore` |
| root | `Daemon.cs`, `Paths.cs`, `MirroredNotifications.cs` |

## The clipboard is three implementations behind one interface

`ClipboardFactory` picks:

1. **`WaylandClipboard`** - speaks `ext_data_control_manager_v1` to the compositor over its own
   native Wayland connection. Told about changes rather than polling, and needs nothing installed.
2. **`CommandLineClipboard`** - `wl-clipboard`, `xclip`, `xsel` or `pbpaste`, polled, because none
   of them has a watch mode. **Compiled but never run**: no helper is installed on the development
   machine.
3. **Nothing** - the desktop still pairs, holds links and sends.

See [[clipboard-sync]] for why the Wayland watcher cannot go through Avalonia.

## Sending once per peer, not once per link

`BroadcastAsync` sends over Wi-Fi first, then over each radio half, **deduplicating on the
fingerprint**.

The two radio halves can hold two different peers, and then sending over both is exactly right.
They can equally hold the **same** peer, one link in each direction, and then sending over both
delivers the clipboard twice - the [[echo-suppression]] is on the sending side, so the receiver
has no defence.
Deduplicating covers both cases; picking one link, as the Windows daemon does, would only ever
have covered the second.

`SendToPeerAsync` is the single-peer version, and it tries the radio when Wi-Fi is not holding
that peer. `Mesh.SendToAsync` alone would silently do nothing for a device that is only on
Bluetooth, which is exactly the device a notification reply is most wanted for.

## `EnsureWiFiToAsync`

A file needs a socket, so a peer reachable only over the radio used to be simply absent from the
list of things a file could be sent to, with nothing said about why.

This asks **in both directions at once** - nudges the dial loop and sends `ControlWakeWiFi` over
the radio - then polls for up to 15s.
Either end may be the one that can actually open the socket.

## A received file never overwrites

`OnFileReceived` moves the working copy into `~/Downloads`, appending ` (1)`, ` (2)` and so on
rather than overwriting.
A second file of the same name is a second file.
A failure to move is logged with the path it is still at rather than losing it.

## Bluetooth is where Linux and macOS have to part

This is the most consequential open item in the whole project's shape.

`DesktopCore` and [[desktop-shell]] are shared between Linux and macOS today because Avalonia
builds for both from one machine, and that is the property that made it the right toolkit.
Bluetooth breaks it.

| | Reachable from |
|---|---|
| BlueZ | plain `net10.0`, over D-Bus |
| CoreBluetooth | `net10.0-macos` or `net10.0-maccatalyst` only, which need macOS and Xcode |

**So the Mac head is to be separated when its Bluetooth is built**, into its own project with its
own target framework, keeping `DesktopCore` and `DesktopShell` shared and platform-free.
Until then macOS stays Wi-Fi only and cross-published from Linux, which costs it nothing it does
not already lack.

**Keep both `DesktopCore` and `DesktopShell` free of either radio API.**

## The finding to read first if the Bluetooth tier ever goes quiet

**`Console.In.ReadLineAsync` is not asynchronous, and it killed the whole Bluetooth tier.**

`Console.In` is a *synchronized* `TextReader`: its async methods run the blocking read inline while
holding the reader's monitor, so the await never yields and the thread it was called on stops
servicing anything else.
In [[linux-daemon]] that thread was one the D-Bus connection needed, so the moment the shell asked
for input the scanner wedged mid-handshake.

The symptom was a daemon that started cleanly, printed its banner, took commands, and simply never
found a single device over Bluetooth, forever.
Nothing failed and nothing was logged.
It is a race, so it looked intermittent, and `--no-shell` or `--quiet` changes the timing enough to
hide it.

**The rule: never `await` a `Console.In` read in a process that has anything else going on.**

## Storage paths

`Paths.cs`.
`~/.local/share/MeshSync/`, or `$XDG_DATA_HOME/MeshSync` when that is set.
`device.key`, `peers.json` and `daemon.log` all live there.

## See also

[[desktop-shell]] · [[linux-daemon]] · [[bluetooth-tier]] · [[ble-link-arbitration]] · [[dbus-ipc]]
