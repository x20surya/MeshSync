---
type: feature
status: in-flight
platforms: [linux]
tier: n/a
code:
  - plasma/dev.meshsync.desktop/contents/ui/MeshBus.qml
  - plasma/dev.meshsync.desktop/contents/ui/FullRepresentation.qml
  - plasma/dev.meshsync.desktop/contents/ui/NotificationDelegate.qml
  - src/DesktopCore/Platform/WidgetInstaller.cs
updated: 2026-08-23
---

# Plasma widget

> **In flight.** `plasma/` is untracked as of 2026-08-23. Built and run against a live daemon on
> Plasma 6.6.6: header, device list, pairing requests and the reply box all draw from
> [[dbus-ipc]].

Your mesh on the panel, the desktop or the system tray: which devices are reachable and over what,
a device asking to join with its fingerprint beside the Allow, what your phone is showing, and a
box to reply in.

## Where it lives

| Part | File |
|---|---|
| The only file that knows the bus | `contents/ui/MeshBus.qml` |
| Panel icon, status, tooltip | `contents/ui/main.qml` |
| The popup | `contents/ui/FullRepresentation.qml` |
| One device | `contents/ui/DeviceDelegate.qml` |
| A knock, and the two answers | `contents/ui/PairingRequest.qml` |
| One notification, and the reply box | `contents/ui/NotificationDelegate.qml` |
| Installed on first run under Plasma | `src/DesktopCore/Platform/WidgetInstaller.cs` |

## No compiled code, and that is the whole design

KDE Connect's plasmoid imports `libkdeconnectdeclarativeplugin.so` - half a megabyte of C++
wrapping its daemon's D-Bus interfaces. Copying that would need CMake, ECM and KF6 development
packages, produce an artifact bound to one Qt minor version per architecture, and put a second
language in a .NET repo.

Plasma 6 ships **`org.kde.plasma.workspace.dbus`**, a generic D-Bus binding usable from pure QML,
as part of `plasma-workspace`. So the widget is QML and nothing else: it builds nowhere, installs
from a directory, and survives Qt updates.

## Four things that binding will catch you with

**The resolve callback is handed the `DBusPendingReply`, not the value.** Reading `reply` directly
gives the wrapper, which iterates as an object with no error and quietly produces nothing.
Use `reply.value`.

**A `variant` arrives wrapped.** A string reads as `{ value: "..." }` while a bool reads as
itself. Put a wrapped one into a `ListModel` and QML makes it a nested model, so `model.Name` is
an object and a string property bound to it comes out empty - a device list of blank rows that
looks like a daemon bug. `MeshBus.unwrap` and `MeshBus.plain` are the only place this is handled.

**`Properties.properties` is a `QQmlPropertyMap` and starts empty.** A binding to
`map.MeshName` written before the first `GetAll` returns resolves to undefined and is never
re-evaluated when the key appears. The map is read once per change into ordinary typed properties
instead.

**`SignalWatcher.onReceivedSignal` is a slot, not a signal**, so QML cannot attach a handler and
the applet refuses to load outright if you try. The lists are driven off the daemon's counts
instead - `PeerCount`, `ConnectedCount`, `PendingCount`, `NotificationCount` - which move on every
arrival, departure, connect and disconnect, and arrive as ordinary `PropertiesChanged`.

## It wears Plasma's theme

Kirigami colours everywhere except the state dot and the mark. A widget painted `#F7F6F3` on a
dark Breeze panel looks broken; the Mesh Sync palette belongs to the Mesh Sync window.

## How it gets there

Bundled with the app and copied into `~/.local/share/plasma/plasmoids/` on first run under a
Plasma session, because an AppImage has no install time and cannot write into a plasmoid directory.
Only when the bundled version is newer, never onto a panel by itself, and a `no-widget` file in
the data directory turns it off for good.
The `.deb` also ships it to `/usr/share/plasma/plasmoids/`.

## See also

[[dbus-ipc]] · [[dbus-interface]] · [[tray-applet]] · [[notification-mirroring]] · [[pairing]]
