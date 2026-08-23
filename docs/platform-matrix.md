---
type: meta
updated: 2026-08-23
---

# Platform matrix

The single most expensive thing to rediscover, in one table.
This is where the vault earns its keep: nothing else in the repo says all of this in one place.

Legend: **Y** works · **P** partial, see the note · **-** not built · **n** never will be

## Features by head

| | Windows | Android | Linux | macOS |
|---|---|---|---|---|
| [[clipboard-sync]] receive | Y | Y | P | P |
| [[clipboard-sync]] send, automatic | Y | n | P | P |
| [[clipboard-sync]] send, user-initiated | Y | Y | Y | Y |
| [[file-transfer]] | Y | Y | Y | Y |
| [[notification-mirroring]] source | - | Y | - | - |
| [[notification-mirroring]] display | Y | Y | Y | P |
| [[find-my-device]] | Y | Y | Y | Y |
| [[remote-browse]] | Y | Y | Y | Y |
| [[pairing]] | Y | Y | Y | Y |
| [[transport-preference]] | Y | - | Y | Y |

## Tiers by head

| | Windows | Android | Linux | macOS |
|---|---|---|---|---|
| [[wifi-tier]] | Y | Y | Y | Y |
| [[bluetooth-tier]] central | Y | Y | Y | n |
| [[bluetooth-tier]] peripheral | Y | Y | - | n |
| [[ble-link-arbitration]] | Y | Y | Y | n |

## Storage and platform services

| | Windows | Android | Linux | macOS |
|---|---|---|---|---|
| [[key-at-rest]] wrapping | DPAPI | Keystore | Keyring | - |
| [[transport-preference]] store | Registry | - | File | File |
| [[link-state]] answers per peer | - | - | Y | Y |
| [[dbus-ipc]] | n | n | P | n |
| Autostart | Y | Y | Y | Y |

## The entries worth explaining

**Automatic clipboard send on Android is `n`, not `-`.**
Android only lets an app read the clipboard while it is in front, and the accessibility service
that used to work around this was removed for good.
See [[clipboard-sync]] for why that is not a gap to be closed.

**Linux clipboard is `P` because it depends on the session.**
Native and watchable on Wayland compositors offering `ext-data-control`, polled through a helper
on X11, and absent with no helper installed.
GNOME implements neither data-control protocol, so GNOME Wayland has no background clipboard.

**Linux Bluetooth is central-only.**
BlueZ accepts the scan and rejects the exported GATT tree, so the peripheral half stands aside.
That is a supported arrangement rather than a missing half, because
[[ble-role-negotiation]] is capability first.

**macOS has no Bluetooth and will not get it here.**
CoreBluetooth needs a target framework that only builds on a Mac with Xcode, which would end the
cross-publish from Linux.
The Mac head is to be split out when its radio is built.
See [[desktop-core]].

**`link-state` per peer on Windows is the remaining half.**
Windows answers per app, so it can mark only one device connected and guesses which by name,
which breaks with two devices called the same thing.
See [[link-state]].
