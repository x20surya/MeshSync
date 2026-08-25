---
type: feature
status: shipped
platforms: [linux]
tier: n/a
code:
  - plasma/dev.meshsync.desktop/contents/ui/MeshBus.qml
  - plasma/dev.meshsync.desktop/contents/ui/FullRepresentation.qml
  - plasma/dev.meshsync.desktop/contents/ui/NotificationDelegate.qml
  - plasma/dev.meshsync.desktop/contents/ui/DeviceDelegate.qml
  - plasma/dev.meshsync.desktop/contents/ui/CompactRepresentation.qml
  - plasma/dev.meshsync.desktop/contents/ui/main.qml
  - plasma/check.sh
  - plasma/preview.sh
  - src/DesktopCore/Platform/WidgetInstaller.cs
updated: 2026-08-25
---

# Plasma widget

> **It drew correctly and almost nothing it offered to do reached the daemon.** Six of eighteen
> controls worked, and the six were exactly the ones that take no arguments. Fixed on 2026-08-25;
> the section below records what it was, because three of the four causes are invisible from
> inside QML and will be met again.

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
| The only check it has | `plasma/check.sh` and `plasma/check/` |
| Run the working tree in one window | `plasma/preview.sh` |

## No compiled code, and that is the whole design

KDE Connect's plasmoid imports `libkdeconnectdeclarativeplugin.so` - half a megabyte of C++
wrapping its daemon's D-Bus interfaces. Copying that would need CMake, ECM and KF6 development
packages, produce an artifact bound to one Qt minor version per architecture, and put a second
language in a .NET repo.

Plasma 6 ships **`org.kde.plasma.workspace.dbus`**, a generic D-Bus binding usable from pure QML,
as part of `plasma-workspace`. So the widget is QML and nothing else: it builds nowhere, installs
from a directory, and survives Qt updates.

## What made twelve controls do nothing

Every one of them was dispatched, answered and logged as an ordinary failure, which is why the
widget looked wired and was not. All four were reproduced with `dbus-monitor` rather than reasoned
about, because none is visible from inside QML.

**A `signature` on the message sends an empty body.** `DBusMessage` has a `signature` property and
setting it makes the binding send the call with **no arguments at all**. The daemon then reads
past the end and answers `Unexpected end of data`, which reads as a marshalling fault on our side
and is not one. Two otherwise identical `Join` calls, one with `signature: "s"` and one without,
differ on the wire only in whether the string is there. Omit it; the types come from the `DBus.*`
wrappers, which is what the binding actually reads.

**`DBus.string(x)` throws unless it is called with `new`.** `TypeError: Function can only be
called with |new|` - and the throw aborts the *calling* function before `asyncCall` is reached, so
nothing is sent and nothing is logged. Eight functions died this way. `MeshBus.text()` is now the
only place a string is wrapped.

**A `(bs)` answer comes back with its string still wrapped.** `reply.value` is
`[false, { value: "..." }]` - the bool bare, the string wrapped. `String(reply[1])` is
`"[object Object]"`, which is what the footer showed after every confirm, reject and file send.
`MeshBus.outcome()` opens it.

**The reject callback is handed the `DBusPendingReply`, not a message.** `String(error)` is
`"Plasma::DBusPendingReply(0x55...)"`. The text is on `reply.error.message`, and the D-Bus error
name is on `reply.error.name`.

## And one that was not the widget's fault

`Properties.Get` and `Set` arrived at the daemon with an empty body while the same calls to every
other service on the bus carried their arguments. **Qt introspects an object before it marshals a
call to it**, and `MeshBusObject` did not declare `org.freedesktop.DBus.Properties` at all - so Qt
found no such method and sent nothing. Reproduced with two `Get` calls issued microseconds apart
from one QML process, one to this daemon and one to `org.kde.StatusNotifierWatcher`.

`gdbus` and `busctl` always send the arguments, which is why [[dbus-interface]]'s own regression
tool passed happily against a surface no Qt client could use. See `MeshBusObject.PropertiesXml`.

## Live without polling

The note here used to say a `SignalWatcher` was unavailable, so the lists were driven off the
daemon's counts with a ten second timer as a backstop. The first half is true - `onReceivedSignal`
is exported as a slot and QML cannot attach to it. The conclusion was too broad.

**`Properties.propertiesChanged` is a real signal** and it carries the keys that moved, so the
refresh is targeted. What the counts could not see was every change that keeps the set the same: a
rename, a link going from Bluetooth to Wi-Fi, a new address. The daemon now publishes
`TreeRevision`, bumped for anything a list has to be redrawn for and deliberately not for
`LastSeen`, which moves on every dial round and would make it a poll wearing a property's clothes.

The timer is gone. So is `watching` being tied to component lifetime: Plasma keeps a full
representation alive after the first expand, so a widget opened once at login went on waking up
every ten seconds until logout. It follows `Plasmoid.expanded` now.

## What a reused row will do to you

`MeshBus.fill` rewrites rows in place rather than clearing and refilling, so the pointer does not
lose a row from under it - which means a delegate becomes a different device or conversation while
whatever the person was doing to it is still in there.

- **A half-typed reply followed the slot, not the conversation.** The notification list re-sorts
  newest-first on every arrival, so this is not a corner case. Rows now carry an `identity` and
  clear the draft when it changes.
- **A reply in flight could never finish.** The answer was matched against the delegate's
  *current* key, and a message arriving while it was in flight moved that key. The key is captured
  when Send is pressed.
- **Ring was a delegate-local bool**, so a row offered to stop a ring it never started and lost
  the offer for one it did. It is `Device1.IsRinging` now, answered by the daemon - which knows
  whether it *asked*, and cannot know whether the phone is making a noise.

**And a merged group could reply to the wrong person.** With [[notification-mirroring]]'s content
setting off there is no title, so every conversation in an app collapses under one head - and one
reply box there has to thread onto one key, whichever won. A merged group offers no reply at all
now. A group can be replied to when it *is* a conversation, or when it holds exactly one
notification.

## Two more things that binding will catch you with

**The resolve callback is handed the `DBusPendingReply`, not the value.** Reading `reply` directly
gives the wrapper, which iterates as an object with no error and quietly produces nothing.
Use `reply.value` - and remember the strings inside it are still wrapped.

**`Properties.properties` is a `QQmlPropertyMap` and starts empty.** A binding to
`map.MeshName` written before the first `GetAll` returns resolves to undefined and is never
re-evaluated when the key appears. The map is read once per change into ordinary typed properties
instead. Iterating it is worse than useless: it hands back `objectName`, `keys`, `valueChanged`
and `__0`..`__6` along with the properties.

**A `variant` arrives wrapped** wherever it appears - the property map, a dictionary from
`GetManagedObjects`, and inside a returned struct. A string reads as `{ value: "..." }` while a
bool reads as itself. Put a wrapped one into a `ListModel` and QML makes it a nested model, so
`model.Name` is an object and a string bound to it comes out empty - a device list of blank rows
that looks like a daemon bug. `MeshBus.unwrap`, `plain` and `outcome` are the only places this is
handled.

## A `Text` reports the width it would like, not the width it takes

One long notification preview put a horizontal scrollbar under the whole widget. Every row here
elides or wraps, so nothing was actually too wide - but `implicitWidth` is the unwrapped string,
and it inflated the column that contained it. It was also the cause of two `ScrollBar` binding-loop
warnings, because Plasma's `ScrollView` pads for a bar when one is showing, so the content's width
moved with the bar and its height moved with its width. Turning horizontal scrolling off took both
to zero.

The popup is sized to its content between a floor and a ceiling now, rather than a flat 26 grid
units that drew one device row and then two thirds of nothing.

## It wears Plasma's theme

Kirigami colours everywhere except the state dot and the mark. A widget painted `#F7F6F3` on a
dark Breeze panel looks broken; the Mesh Sync palette belongs to the Mesh Sync window.

## How it gets there

Bundled with the app and copied into `~/.local/share/plasma/plasmoids/` on first run under a
Plasma session, because an AppImage has no install time and cannot write into a plasmoid directory.
Never onto a panel by itself, and a `no-widget` file in the data directory turns it off for good.
The `.deb` also ships it to `/usr/share/plasma/plasmoids/`.

**Equal is not newer, but equal is also what a working tree looks like.** Comparing versions alone
meant every edit between two version bumps was invisible, so an edited widget stayed stale and the
only symptom was that nothing changed. When the versions match, the newest write time decides.
Files deleted upstream are taken away too - a stale QML file beside a live one is not inert,
because a component is resolved by name from the directory it is in.

**Copying the files is not enough on its own.** plasmashell holds the QML it has loaded, so a
widget already on a panel keeps running the old one until the shell reloads:
`systemctl --user restart plasma-plasmashell.service`.

## How it is checked

`plasma/check.sh` starts a scratch daemon, loads **the real `MeshBus.qml`** under `plasmawindowed`,
calls every function on it once, and reads `dbus-monitor` to ask the only question that catches
any of this: **how many bytes were on the wire**. A test that asks whether an exception was raised
sees nothing, because every one of these failures produces a call that is answered.

It went 1/18 before the fixes and 20/20 after, the last two being liveness: that the widget
re-reads when the daemon says a property moved, and that `TreeRevision` is published at all.

`plasma/preview.sh` runs the working tree in one window against whichever daemon is running.
It points `XDG_DATA_HOME` at a throwaway copy, because KPackage searches that **before**
`XDG_DATA_DIRS` - so a widget already installed under `~/.local/share` silently wins, and the
tree you are editing is not the one you are looking at.

## See also

[[dbus-ipc]] · [[dbus-interface]] · [[tray-applet]] · [[notification-mirroring]] · [[pairing]]
