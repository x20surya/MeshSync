---
type: reference
status: in-flight
platforms: [linux]
tier: n/a
code:
  - src/DesktopCore/Ipc/BusNames.cs
  - src/DesktopCore/Ipc/MeshBusObject.cs
  - packaging/meshsyncctl
updated: 2026-08-23
---

# D-Bus interface

> **In flight.** `src/DesktopCore/Ipc/` and `packaging/meshsyncctl` are uncommitted as of
> 2026-08-23. This describes what is on disk - and what is on disk has been exercised end to end:
> two daemons paired entirely over this interface, `Confirm` on a `Pairing1`, then `IsConnected`,
> `ActiveLink wifi` and `SendText -> 1` across a real socket.

The running device publishes itself on the **session** bus as `dev.meshsync.Daemon`, so a panel
widget, a tray applet or a script can drive it.
Both [[desktop-shell]] and [[linux-daemon]] call `MeshBus.TryStartAsync`.

## Object tree

Two levels deep, by design. `BusNames.FingerprintIn` rejects grandchildren deliberately, because
answering for a deeper path would export an object nothing knows how to serve.

```
/dev/meshsync/Daemon                        dev.meshsync.Daemon1
/dev/meshsync/Daemon/devices/<fingerprint>  dev.meshsync.Device1
/dev/meshsync/Daemon/pending/<fingerprint>  dev.meshsync.Pairing1
```

Plus `org.freedesktop.DBus.Properties` and `org.freedesktop.DBus.ObjectManager`.

**A fingerprint cannot be a path element as it stands.**
A path element may contain only `[A-Za-z0-9_]`, and a fingerprint is written
`AC83-492B-684F-4263`. Hyphens become underscores on the way out and back, and
**`BusNames.ToElement` / `FromElement` are the only place that mapping exists** - doing it in more
than one place is how a device ends up unreachable at a path that looks right.

## Members

**`dev.meshsync.Daemon1`**

| Method | |
|---|---|
| `SendText`, `SendFile` | Send to the mesh |
| `Dial`, `Join` | Connect, and pair from a code |
| `StopRinging` | Silence this device's own alarm |
| `SendClipboard` | Send whatever is on the clipboard now, for the tray and the widget |
| `Notifications`, `DismissNotification`, `DismissAllNotifications` | [[notification-mirroring]] |
| `ReplyToNotification` | Answer a mirrored notification in the app that posted it |
| `Activity` | The in-memory [[activity-log]] |
| `Show`, `Quit` | Raise the window on a named page, and exit |

Writable properties: `MeshName`, `Transport`, `TrayIconVisible`.

**What is deliberately not on the bus.** Clipboard text, image bytes, notification titles and
bodies, and activity previews. Everything on the session bus is readable by every program running
as this user, and a mirrored notification is the most private thing Mesh Sync carries.
`Notifications` returns a key, an app name, a sender and a time - enough to badge "3 from S21 FE"
and draw a reply box, and nothing to read. `SendText` takes text from a caller; nothing hands text
back.

**`dev.meshsync.Device1`**: `Ring`, `SendFile`, `EnsureWiFi`, `Forget`.
`IsConnected` is answered **per peer** here, which is more than [[link-state]] can say - a device
list on a panel would otherwise show the same dot on every row.

**`dev.meshsync.Pairing1`**: `Confirm`, `Reject`.

## Three decisions

**A bus that is not there is not an error.**
A user session without D-Bus is unusual and entirely workable: the clipboard, the links and the
pairing all carry on. `MeshBus` reports it once and stands aside, exactly as the Bluetooth tier
does on a machine with no radio.

**Only one device per session can own the name.**
Two daemons on one machine is a supported arrangement - it is how the mesh is exercised without a
second computer - so losing the race is expected rather than fatal.
The second device serves its objects on its unique name and is simply not the one a widget finds.

**It lives in `DesktopCore`, not in the shell**, because the headless daemon has to publish the
same interface. A machine with a panel but no window still wants a widget and a tray icon, and
that is the whole reason the Linux head was built as a core plus two front ends.

## `meshsyncctl`

`packaging/meshsyncctl`, a POSIX shell script over `gdbus`.

```
dial  dismiss  dismiss-all  forget  join  notifications  pending  quit
reply  ring  send  send-file  show  status  stop-ringing  transport
```

Written in shell rather than as a fourth .NET head because glib is already a dependency -
`DesktopNotifier` shells `gdbus` today - so it adds nothing to install.

**It is the regression test for the bus surface as well as a tool.**
Nothing in it knows anything the interface does not expose, so if a command cannot be written
against `dev.meshsync.Daemon1`, the interface is missing something.
Given that no head has an automated test at all ([[testing]]), this is the only executable check
the Linux head has.

## The marshalling rules this cost

`BusWrite.cs` exists to enforce one rule, learned twice.

**A D-Bus dictionary entry is a struct, and every struct is aligned to eight bytes**, including
the second entry and every one after it.
`WriteArrayStart(DBusType.DictEntry)` does not insert that padding; `WriteDictionaryEntryStart`
does. A dictionary written the first way is malformed, and `dbus-daemon` answers a malformed
message by **closing the connection with nothing said**.

Reproduced rather than assumed: a five-entry `a{sv}` disconnects the sender mid-reply written one
way, and reads cleanly from `gdbus`, `busctl` and QML written the other.

**The same rule un-blocked [[bluetooth-tier]]'s peripheral half.** `LinuxBlePeripheral` hand-rolled
every dictionary in its `a{oa{sa{sv}}}`, which is why BlueZ closed the connection on
`RegisterApplication` and the peripheral stood aside. With `WriteDictionaryEntryStart` in place
BlueZ reads the tree and accepts it; the remaining failure has moved on to `RegisterAdvertisement`
and is a different problem, described in [[bluetooth-tier]].

**A slow method must not be awaited in the handler.** Dialling takes seconds and sending a file
takes as long as the file; awaiting either inline holds the dispatch loop so every property read
queues behind it. `MethodContext.DisposesAsynchronously = true`, return, and reply from a
background task - measured, a call issued during a 2.5s one comes back in 18 ms.
`MessageWriter` is a **ref struct**, so it cannot be a local or a parameter anywhere inside an
async method: the awaiting and the writing are two delegates for that reason.

**The writer is a struct, so it is passed by `ref` everywhere.**
Handing it to an `Action<MessageWriter>` writes the body into a copy that is discarded, and the
message promises bytes it does not have. Calls with no arguments work perfectly, which is what
makes it look like a marshalling problem in the arguments themselves.
`BlueZ.cs` declares `delegate void MessageArgs(ref MessageWriter writer)` for the same reason.

[[key-at-rest]] hit both of these first, over `org.freedesktop.secrets`.
There, both are avoided rather than solved: every dictionary written holds exactly one entry with
a second property set afterwards through the Properties interface, and the secret is read with
`Secret.Item.GetSecret`, which returns the struct alone at the start of the body where it is
aligned already.

## See also

[[dbus-ipc]] · [[desktop-core]] · [[linux-daemon]] · [[key-at-rest]] · [[testing]]
