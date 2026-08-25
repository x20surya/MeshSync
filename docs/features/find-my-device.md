---
type: feature
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/SyncContent.cs
  - src/WinDaemon/Ringer.cs
  - src/DesktopCore/Platform/Ringer.cs
  - src/DesktopCore/Daemon.cs
updated: 2026-08-25
---

# Find my device

Make a device sound an alarm, through silent mode, with no network.

## Where it lives

- `src/WinDaemon/Ringer.cs`
- `src/DesktopCore/Platform/Ringer.cs`
- Android has its own, on the alarm stream
- The wire side is one content type: `Ring` (0x06), one byte, non-zero to start and zero to stop

## Why ringing is a content type and not a control frame

This is the design decision worth carrying away from this note.

Two bytes down the Bluetooth control path would have been the obvious shape and is the wrong one.
Control frames ride *outside* the encrypted payload, so anything that knew the service UUID could
have made a phone shriek from across the street.
Riding the normal encrypted path costs nothing and makes the request authenticated, exactly as an
address is.

It is still small enough for Bluetooth, which is the entire point: the moment you most want to
find a device is the moment it is not on any network.

## Platform notes

**Android needs `android.permission.VIBRATE` in the manifest.**
It was missing, the sound still played, and Android throws a `SecurityException` for the vibrate
call rather than ignoring it.
The buzz is the half that finds a phone face-down in a sofa, so this was invisible until a real
ring was tried.

## What is still open

**Ringing a phone that is face-down on silent is unverified**, which is the exact case the alarm
stream exists for.


## Asked, not ringing

`Daemon.HasAskedToRing` remembers which devices this one has asked to ring and not yet asked to
stop, and `Device1.IsRinging` publishes it - see [[dbus-interface]].

**It cannot mean "that phone is making a noise",** because the phone does not report back. What
can be known is whether it was asked, and that is what a "stop ringing" button needs. It is held
in the daemon rather than in whatever drew the button: [[plasma-widget]] kept it in a list
delegate, which is reused as the list re-sorts and rebuilt when the popup is, so a row offered to
stop a ring it never started and lost the offer for one it did. A request that did not arrive is
not remembered, so the button says "ring", which is the truth.

## See also

[[content-types]] · [[bluetooth-tier]] · [[session-keys]]
