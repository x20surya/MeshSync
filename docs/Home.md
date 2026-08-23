---
type: meta
updated: 2026-08-23
---

# Mesh Sync

A local-first universal clipboard for your own devices, with no cloud, no server and no account.
Two tiers, five heads, one shared core.

[[README]] says what this vault is for and how it relates to the three root documents.
Read [[vault-guide]] before writing into it, and [[note-schema]] before adding frontmatter.
[[platform-matrix]] is the fastest way to answer "does this platform have that".

## Features

What the owner of a device can point at.

| | Tier | Status |
|---|---|---|
| [[clipboard-sync]] | Either | Shipped, and user-initiated on Android by design |
| [[file-transfer]] | Wi-Fi only | Shipped, does not resume |
| [[notification-mirroring]] | Either | Shipped; replying from the desktop is in flight |
| [[find-my-device]] | Either | Shipped, works with no network at all |
| [[remote-browse]] | Wi-Fi only | Shipped, one direction verified |
| [[pairing]] | Wi-Fi to start | Shipped, two steps, both required |
| [[mesh-name]] | Either | Shipped, renaming does not propagate |
| [[transport-preference]] | n/a | Shipped on Windows and the desktop head |
| [[plasma-widget]] | n/a | In flight; pure QML, no compiled plugin |
| [[tray-applet]] | n/a | In flight; replaces Avalonia's, and the headless head has one now |
| [[password-vault]] | n/a | Planned and deliberately gated |

## Mechanisms

Things nobody asked for that every feature rests on.

**Identity and trust**
[[device-identity]] · [[session-keys]] · [[peer-registry]] · [[key-at-rest]]

**Transport**
[[wifi-tier]] · [[bluetooth-tier]] · [[wire-formats]] · [[content-types]] · [[address-handover]]

**Deciding things once**
[[link-state]] · [[ble-link-arbitration]] · [[ble-role-negotiation]]

**Everything else**
[[echo-suppression]] · [[activity-log]] · [[dbus-ipc]]

## Reference

Exact, code-derived, and the part that cannot be got from `AGENTS.md` or `HANDOFF.md`.

| | |
|---|---|
| [[protocol-tcp]] | Frame bytes, hello layout, the version history, what `IsConnected` means |
| [[protocol-ble]] | UUIDs, frame discrimination, the 512-byte ceiling, chunk receipts |
| [[protocol-payloads]] | Every content type's exact body layout |
| [[crypto]] | The HKDF construction, AES-GCM layout, Argon2id parameters |
| [[shared-folders-security]] | The four-step path resolution, and why each step is needed |
| [[on-disk-formats]] | `device.key`, `peers.json`, and the `WouldLosePort` guard |
| [[timings]] | **Every timeout and interval in the project, in one table** |
| [[testing]] | What the 296 cases cover, and the six things nothing covers |
| [[building]] | Build, run, package, reset, and two devices on one machine |
| [[dbus-interface]] | The bus surface and `meshsyncctl`. In flight |

## Heads

The five runnable applications.

| | Platform | UI | Both tiers |
|---|---|---|---|
| [[windows-daemon]] | Windows | WPF window and tray | Yes |
| [[android-client]] | Android 8+ | MAUI drawer | Yes |
| [[desktop-core]] | Linux, macOS | None, it is a library | Linux only |
| [[desktop-shell]] | Linux, macOS | Avalonia window and tray | Linux only |
| [[linux-daemon]] | Linux, macOS | Terminal | Linux only |

## The three root documents

- [AGENTS.md](../AGENTS.md) - the architecture and the rules. Authoritative.
- [HANDOFF.md](../HANDOFF.md) - the findings, session by session. Authoritative.
- [SECURITY.md](../SECURITY.md) - the threat model, including what is not covered.

## Where the sharp edges are

If you are about to touch one of these, read the note first.

- [[ble-link-arbitration]] - two devices in range will each dial the other unless something stops
  them, and a duplicate link delivers every clipboard twice.
- [[session-keys]] - the key belongs to the connection, never to the peer. Caching it against the
  device has broken both forward secrecy and revocation before.
- [[content-types]] - a new type goes in `SyncContent` and nowhere else, or a file chunk routes
  into the clipboard.
- [[clipboard-sync]] - never reintroduce an accessibility service on Android, whatever it would fix.
