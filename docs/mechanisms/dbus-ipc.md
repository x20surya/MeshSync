---
type: mechanism
status: shipped
platforms: [linux]
tier: n/a
code:
  - src/DesktopCore/Ipc/MeshBus.cs
  - src/DesktopCore/Ipc/MeshBusObject.cs
  - src/DesktopCore/Ipc/BusNames.cs
  - src/DesktopCore/Ipc/BusWrite.cs
updated: 2026-08-25
---

# D-Bus IPC

> Exercised: two daemons paired entirely over this interface and text crossed a real socket
> between them. Its consumers are [[plasma-widget]], [[tray-applet]] and `meshsyncctl`.

Publishes the running device on the session bus as `dev.meshsync.Daemon`, so something outside the
app - a panel widget, a tray applet, a script - can drive it.

## The surface

| Path | Interface |
|---|---|
| `/dev/meshsync/Daemon` | `dev.meshsync.Daemon1` |
| `/dev/meshsync/Daemon/devices/<fingerprint>` | `dev.meshsync.Device1` |
| `/dev/meshsync/Daemon/pending/<fingerprint>` | `dev.meshsync.Pairing1` |

Plus `org.freedesktop.DBus.Properties` and `ObjectManager`.

**Daemon**: `SendText`, `SendFile`, `SendClipboard`, `Dial`, `Join`, `StopRinging`,
`Notifications`, `DismissNotification`, `ReplyToNotification`, `DismissAllNotifications`,
`Activity`, `Show`, `Quit`, and `MeshName` / `Transport` / `TrayIconVisible` /
`ShowNotificationContent` as writable properties.

**Device**: `Ring`, `SendFile`, `EnsureWiFi`, `Forget`, and `IsRinging`.

**Pairing**: `Confirm`, `Reject`.

## Where it lives, and why there

In `DesktopCore` rather than in the shell, because **the headless daemon has to publish the same
interface**.
A machine with a panel but no window still wants a widget and a tray icon, and that is the whole
reason the Linux head was built as a core plus two front ends.
Both [[desktop-shell]] and [[linux-daemon]] call `MeshBus.TryStartAsync`.

Nothing in it touches Avalonia and nothing is platform-specific beyond the session bus itself.

## `meshsyncctl`

`packaging/meshsyncctl`, a POSIX shell script over `gdbus` that drives the running device from a terminal.

It is written in shell rather than as a fourth .NET head because glib is already a dependency -
`DesktopNotifier` shells `gdbus` today - so it adds nothing to install.
**It is the regression test for the bus surface as well as a tool**: nothing in it knows anything
the interface does not expose, so if a command cannot be written against `dev.meshsync.Daemon1`,
the interface is missing something.

It is not a regression test for *clients*, and that distinction cost a widget. `gdbus` encodes
arguments correctly whatever the introspection says, so `meshsyncctl` passes against a surface a
Qt client cannot use. `plasma/check.sh` covers the other half by reading the wire.

## Say what changed, not that something did

`Publish` diffs the children before it reads the root's own properties, and bumps `TreeRevision`
when any of them arrived, left, or moved in a way a list has to be redrawn for. Order matters: the
other way round, a client that refetches when the revision moves is told one publish late, which
is a device list correct only after the next unrelated change.

`LastSeen` is excluded on purpose. It changes on every dial round, so counting it would turn one
property into a fifteen second poll for every client watching it.

## Declare the standard interfaces

`Introspect` emits `org.freedesktop.DBus.Properties` because **Qt introspects before it marshals**,
and against a peer that does not declare `Get` and `Set` it sends them with an empty body. See
[[dbus-interface]] - it cost [[plasma-widget]] three settings, and `meshsyncctl` could not catch it
because `gdbus` always sends the arguments.

## Three decisions worth knowing

**A bus that is not there is not an error.**
A user session without D-Bus is unusual and entirely workable.
`MeshBus` reports it once and stands aside, exactly as the Bluetooth tier does on a machine with
no radio.

**Only one device per session can own the name.**
Two daemons on one machine is a supported arrangement - it is how the mesh is exercised without a
second computer - so losing the race is expected rather than fatal.
The second device serves its objects on its unique name and is simply not the one a widget finds.

**A fingerprint cannot be a path element as it stands.**
A D-Bus object path element may contain only `[A-Za-z0-9_]` and a fingerprint is written
`AC83-492B-684F-4263`.
The hyphens become underscores on the way out and back, and doing that in more than one place is
how a device ends up unreachable at a path that looks right.
`BusNames` is the only place that mapping exists.

## The alignment trap, again

`BusWrite.cs` exists to enforce one rule: **a D-Bus dictionary entry is a struct, and every struct
is aligned to eight bytes**, including the second entry and every one after it.

`WriteArrayStart(DBusType.DictEntry)` does not insert that padding.
`WriteDictionaryEntryStart` does.
A dictionary written the first way is malformed, and `dbus-daemon` answers a malformed message by
closing the connection with nothing said.
Reproduced rather than assumed: a five-entry `a{sv}` disconnects the sender mid-reply written one
way and reads cleanly from `gdbus`, `busctl` and QML written the other.

**And the writer is a struct**, so it is passed by `ref` everywhere.
Handing it to an `Action<MessageWriter>` writes the body into a copy that is then discarded, and
the message promises bytes it does not have.
Calls with no arguments work perfectly, which is what makes it look like a marshalling problem in
the arguments themselves.

[[key-at-rest]] hit both of these first, over `org.freedesktop.secrets`.
This is the second system to pay for them, which is why the rules are now in a file of their own.

## See also

[[desktop-core]] · [[desktop-shell]] · [[linux-daemon]] · [[key-at-rest]]
