---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Identity/DeviceIdentity.cs
updated: 2026-08-23
---

# Device identity

Every device holds a persisted P-256 keypair.
Its identity is the SHA-256 fingerprint of that key, written as four groups like
`AC83-492B-684F-4263`.

**Identity comes first** is the first architecture rule in `AGENTS.md`, and it is first because
that one fingerprint decides three separate things:

1. Whether a peer is allowed to connect at all.
2. Which key traffic is sealed with. See [[session-keys]].
3. Which role each device takes on a link. See [[ble-role-negotiation]].

## Where it lives

`src/CoreLib/Identity/DeviceIdentity.cs`, with the on-disk key at `device.key` beside the log.

| Platform | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\MeshSync\device.key` |
| Linux and macOS | `~/.local/share/MeshSync/device.key`, or `$XDG_DATA_HOME/MeshSync` |
| Android | the app-private files directory |

Deleting it forces a re-pair, which is the fastest way to test [[pairing]] from scratch.

## What replaced

`DeriveKey("MasterPassword123", "Salt")` was in both apps, which made every install of this
application interchangeable with every other.
That is gone, and `AGENTS.md` carries a standing prohibition on anything like it returning.

`TrustManager` was deleted rather than left alongside this.
It minted a fresh keypair on every construction and nothing on the wire consulted it.

## Findings worth knowing

**A device must refuse its own public key.**
Otherwise it agrees a secret with itself and echoes its own clipboard back for ever.
This is not hypothetical: it is what the two-devices-on-one-machine setup provokes, and it is how
the [[wifi-tier]] port bug was found.

**Refusing to load a key file is not the same as being allowed to replace it.**
The first attempt returned null for both, so a wrapped key that could not be unwrapped - a
Keystore briefly unavailable, say - was overwritten by a fresh identity, silently costing every
pairing on the device.
There are two outcomes and only one of them may touch the file.
A test caught this one.

## See also

[[session-keys]] · [[peer-registry]] · [[key-at-rest]] · [[pairing]]
