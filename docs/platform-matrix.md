---
type: meta
updated: 2026-08-25
---

# Platform matrix

The single most expensive thing to rediscover, in one table.
This is where the vault earns its keep: nothing else in the repo says all of this in one place.

Legend: **Y** works · **P** partial, see the note · **-** not built · **n** never will be

**macOS is out of this table for now.** Nothing has ever launched the Mac binary, it has no radio,
no key protector and no clipboard watcher, and carrying an unverified column through the v0.4
transport refactor was maintaining a claim nobody had checked.
The cross-publish target stays in the solution and `IBleRadio` is shaped so CoreBluetooth drops in
behind it. See [[desktop-core]].

## How each head is installed

| Head | Route | Architecture |
|---|---|---|
| Linux, Debian-derived | apt, `https://x20surya.me/MeshSync` | **amd64 only** |
| Linux, everything else | AppImage, or `packaging/install-user.sh` | **amd64 only** |
| Windows | self-contained `.exe` from the release | x64 |
| Android | signed `.apk` from the release, CI debug key | all |

**Nothing ships for arm64.** A Raspberry Pi, an arm64 server or an Asahi Mac has no route at all.
`packaging/build.sh` already takes `ARCH=arm64` and derives the RID, the Debian architecture and
the AppImage architecture from it; `release.yml` calls it once with no `ARCH`, and that is the
whole of the gap. The Linux floor is **glibc 2.27** - Ubuntu 18.04, Debian 10 - measured from the
shipped binaries. See [[installing]].

## Features by head

| | Windows | Android | Linux |
|---|---|---|---|
| [[clipboard-sync]] receive | Y | Y | P |
| [[clipboard-sync]] send, automatic | Y | n | P |
| [[clipboard-sync]] send, user-initiated | Y | Y | Y |
| [[file-transfer]] | Y | Y | Y |
| [[notification-mirroring]] source | - | Y | - |
| [[notification-mirroring]] display | Y | Y | Y |
| [[find-my-device]] | Y | Y | Y |
| [[remote-browse]] | Y | Y | Y |
| [[pairing]] over Wi-Fi | Y | Y | Y |
| [[pairing]] with no network | Y | Y | P |
| [[transport-preference]] | Y | - | Y |

## Tiers by head

| | Windows | Android | Linux |
|---|---|---|---|
| [[wifi-tier]] | Y | Y | Y |
| [[bluetooth-tier]] central | Y | Y | Y |
| [[bluetooth-tier]] peripheral | Y | Y | - |
| [[bluetooth-tier]] several links at once | Y | Y | Y |
| [[ble-link-arbitration]] | Y | Y | Y |
| [[mesh-beacon]] published | - | Y | Y |
| [[mesh-beacon]] checked before connecting | Y | Y | Y |

## Storage and platform services

| | Windows | Android | Linux |
|---|---|---|---|
| [[key-at-rest]] wrapping | DPAPI | Keystore | Keyring |
| [[transport-preference]] store | Registry | - | File |
| [[peer-link]] answers per peer | Y | Y | Y |
| [[dbus-ipc]] | n | n | P |
| Autostart | Y | Y | Y |

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
It also means two Linux machines still cannot meet over Bluetooth, and pairing with no network is
therefore `P` there: a Linux box can join a phone that way, but not another Linux box.

**Windows publishes no beacon and is still found.**
A Windows GATT service provider advertises what it likes and has no room for manufacturer data
beside a 128-bit service UUID.
A missing beacon is read as "unknown, try after anything that verified" rather than as a refusal -
which is the whole reason [[mesh-beacon]] is a ranking and not a gate.
Windows still checks every beacon it *sees*, so it refuses other meshes as well as anything else.

**Every head answers per peer now.**
Windows and Android used to answer per app, so each could mark only one device connected and
guessed which by comparing names - which broke outright with two devices called the same thing.
See [[peer-link]].
