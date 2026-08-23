---
type: mechanism
status: shipped
platforms: [windows, android, linux]
tier: ble
code:
  - src/CoreLib/Transport/BleRole.cs
updated: 2026-08-23
---

# BLE role negotiation

GATT roles are genuinely asymmetric, so somebody has to be the central and somebody the
peripheral.
`BleRoleRules` decides, in `src/CoreLib/Transport/BleRole.cs`.

## The rule: capability first, fingerprint second

**Capability first.**
Advertising is a hardware capability on Android and scanning is not:
`BluetoothAdapter.BluetoothLeAdvertiser` is null on devices without peripheral support.
A device that cannot advertise must be the central, whatever its fingerprint sorts to.

**Fingerprint second**, only between two devices that can both do either.

`AGENTS.md` forbids simplifying this to a fingerprint comparison, because the naive
"lower fingerprint advertises" rule agrees on an arrangement neither device can perform.

## Why this is not the same as Wi-Fi

[[wifi-tier]] settles its collisions by fingerprint alone, because TCP is symmetric and either end
can do either job.
Bluetooth cannot, so it needs a different rule, and having two rules that look similar and are not
is exactly why this has its own note.

## Who this rule already covers for free

**Linux**, which scans but cannot advertise, so it is always the central.
**Android phones without advertising hardware**, identically.
**iOS**, which will declare `Central` only when it exists - a backgrounded iOS app's advertised
service UUIDs move to Apple's overflow area, which Windows and Android cannot see.

That is one of the two reasons iOS can never be a full peer.
The other is that background pasteboard reads have returned nil since iOS 9.

## The permission trap

**Declaring `BLUETOOTH_ADVERTISE` is not requesting it.**
It is a runtime grant on Android 12+, and the failure is quiet: the advertiser throws a
`SecurityException` naming the permission, which gets logged and swallowed, so the phone simply
never becomes findable and nothing says why.

Scan and connect had been granted long ago.
Advertising had never been needed until the phone could take the peripheral role.

## See also

[[ble-link-arbitration]] · [[bluetooth-tier]] · [[device-identity]]
