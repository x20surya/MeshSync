---
type: mechanism
status: partial
platforms: [windows, android, linux]
tier: ble
code:
  - src/CoreLib/Transport/BleProtocol.cs
  - src/CoreLib/Transport/BleFragmenter.cs
  - src/DesktopCore/Bluetooth/
  - src/WinDaemon/WindowsBleTransport.cs
updated: 2026-08-23
---

# Bluetooth tier

**The standing link.**
Held continuously whenever a peer is in range, at roughly 6.7 KB/s.
Carries text, presence and control frames, and keeps working with no network at all.

That last sentence is the whole difference between this project and everything else in the space.
Every other tool here needs a LAN.

## Where it lives

| Piece | File |
|---|---|
| Framing and the message id counter | `src/CoreLib/Transport/BleProtocol.cs` |
| Chunking and reassembly | `src/CoreLib/Transport/BleFragmenter.cs` |
| Roles | `src/CoreLib/Transport/BleRole.cs`, see [[ble-role-negotiation]] |
| Windows | `src/WinDaemon/WindowsBleTransport.cs`, `WindowsBleCentral.cs` |
| Linux, over BlueZ and D-Bus | `src/DesktopCore/Bluetooth/` |
| Android | `AndroidBleTransport.cs`, `AndroidBlePeripheral.cs` |

## Why it is held open

A connection interval of a second or two costs microamps between events.
Wi-Fi is not held open, because every heartbeat pulls the chip out of power save.
That asymmetry is the entire reason the tiers are arranged this way.

Peers are found by scanning for the service UUID, so [[pairing]] carries no Bluetooth address and
no OS-level bonding is used or needed.
Both characteristics are `GattProtectionLevel.Plain`, so "forget this device" in Bluetooth
settings changes nothing.

## Platform state

**Linux is the central half only.**
It scans, connects, exchanges the hello and holds the link.
It does not advertise: BlueZ accepts the scan and rejects the exported GATT tree, so
`LinuxBlePeripheral` registers, fails and stands aside.
That is a supported arrangement rather than a missing half, because [[ble-role-negotiation]] is
capability first - a device that cannot advertise is always the central, exactly as an Android
phone without advertising hardware would be.

Exercised against a phone on 2026-08-23: the link comes up, a session is agreed, the address
crosses the radio and text arrives.

**macOS has none of it.**
CoreBluetooth is reachable only from `net10.0-macos` or `net10.0-maccatalyst`, which need macOS
and Xcode to build.
See [[desktop-core]] for why that decides the shape of the Mac head.

## The findings that shaped the protocol

**A GATT attribute value is capped at 512 bytes, whatever the MTU says.**
Windows reports `MaxNotificationSize` as MTU minus the ATT header, which is 514 on a 517 MTU.
Bisected on device: a 512-byte chunk arrives, a 513-byte chunk never does, with no error.

**Windows keeps one outstanding notification per characteristic.**
Chunks sent back to back overwrite each other, so a 128-chunk message arrived as its last chunk
alone.
Fixed with a four-byte receipt per chunk in our own protocol.

**Indications look like the right answer and are not.**
Acknowledged at the ATT layer, which is exactly the flow control needed, but on this stack the
confirmations never arrived and Windows tore the link down with `GATT status 19`.

**Notifying a characteristic reaches every subscriber.**
Invisible with one phone.
With two it hands each of them the other's traffic and lets either answer the receipt the sender
is waiting on.
Use the per-client overload.

**Android silently throttles BLE scanning.**
More than about five start/stop cycles in thirty seconds and the scan returns nothing, with no
error and no callback.
Holding the link rather than rebuilding it per use keeps the rate down.

**"Ready" is not proof of a usable link.**
Android reports the subscription write as successful even against a dead service, so the link must
answer a ping before it is reported connected.

**Killing the desktop process orphans its GATT registration.**
The phone keeps discovering the orphan, connects, subscribes, both ends report success, and
nothing crosses.
Quitting gracefully recovers; a crash or a Task Manager kill needs the adapter toggled.

## The churn that is not ours

A standing link is dropped by Windows at almost exactly 30 seconds and immediately re-established
by the phone, reported as `status 19`, `GATT_CONN_TERMINATE_PEER_USER`.
It reconnects in about a second and nothing is lost, but the "standing" link is reconnecting
roughly a hundred and twenty times an hour.
The phone is not the cause: its heartbeat has a 24 second timeout that never fires.
`GattSession.MaintainConnection` does not stop it, because the flag is honoured for a link Windows
dialled and not for one it accepted.
Whatever the cause, it is below this code, and the reconnect path covers it.

## What is still open

- **Bluetooth caps the mesh at a handful of peers.** A GATT central holds around seven on Android.
- **A phone acting as peripheral carrying real traffic is unverified.** With one phone the role
  rule correctly makes it the central, so nothing has crossed that link.
- **The central announces itself before it knows who it is talking to.** See the open decision in
  `HANDOFF.md`: two meshes in range learn each other's device and mesh names.

## See also

[[ble-link-arbitration]] · [[ble-role-negotiation]] · [[wire-formats]] · [[wifi-tier]] · [[link-state]]
