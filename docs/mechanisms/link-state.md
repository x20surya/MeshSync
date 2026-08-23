---
type: mechanism
status: partial
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Transport/LinkState.cs
updated: 2026-08-23
---

# Link state

The one shared answer to "is anything reachable, and over which link".
Wi-Fi wins when both are up.

Every head calls this rather than asking a transport directly, and every screen reads it rather
than computing its own.

## Where it lives

`src/CoreLib/Transport/LinkState.cs`.

It is one of the three things that moved out of `src/WinDaemon` into `CoreLib` in v0.2.3, with
[[transport-preference]] and [[ble-link-arbitration]], because the Linux head had reimplemented
each of them differently or not at all and **every one of those divergences was a bug**.

## The gap that is left

`LinkState` is still an *aggregate*: it answers per app.

| Head | Granularity |
|---|---|
| Linux and macOS | **per peer**, through `Daemon.IsConnectedTo` and `IsBluetoothConnectedTo` |
| Windows | per app |
| Android | per app |

So the desktop head's device list names the tier each device is actually on.
Windows can only mark one device connected, and **guesses which by comparing names**, which breaks
with two devices called the same thing.

Bringing the per-peer answer to Windows is the remaining half, and it is the thing that has to
grow when a third device arrives.

## The rule this note exists to carry

**Anything one head needs and another already has belongs in `CoreLib`, not written a second
time.**
A platform should be wiring and storage - a registry key here, a file there - and never its own
copy of a rule.

## See also

[[ble-link-arbitration]] · [[transport-preference]] · [[wifi-tier]] · [[bluetooth-tier]]
