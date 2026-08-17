# Agent Instructions

This document describes the architecture and the rules for anyone, human or agent, working on
Mesh Sync.
Read [HANDOFF.md](HANDOFF.md) alongside it: it records the findings behind these decisions, most
of which are not guessable from the code.

## The Goal

- **Absolute privacy**: no cloud hosting, no third-party servers, no account.
- **Seamless sync**: devices stay in sync automatically whenever they are in range of each other.
- **Universal clipboard**: copy text or an image on any device, paste it on any other.

## Architecture

### 1. Identity comes first

Every device holds a persisted P-256 keypair.
Its identity is the SHA-256 fingerprint of that key, and that identity decides three separate
things: whether a peer is allowed to connect, which key traffic is sealed with, and which role
each device takes on a link.

**There is one key per pair of devices**, agreed by ECDH with both fingerprints sorted into the
derivation so the two ends compute the same value without exchanging it.
A single mesh-wide key would let any paired device read traffic meant for another pair.
That distinction is invisible with two devices and matters immediately with three.

A listener refuses any peer it has not paired with.
A stranger is accepted only while the pairing window is open, which is exactly while the pairing
code is on screen: that is the only signal the receiving side gets that a stranger was invited.

### 2. Two transport tiers, neither of them a fallback

- **Bluetooth LE is the standing link.**
  Held continuously whenever a peer is in range.
  Carries text, presence and control frames at roughly 6.7 KB/s.
  Peers are found by scanning for the service UUID, so pairing carries no Bluetooth address and
  no OS-level bonding is used or needed.
- **Wi-Fi is raised on demand.**
  Length-prefixed TCP on port 45001.
  Carries anything, and is the only tier that carries images.

Wi-Fi is wanted when any of these hold: the screen is on, a send needs it, a peer has asked for
it, or Bluetooth is not up.
**That last one is load-bearing.** Without it, losing Bluetooth would leave a device with no link
at all, and inverting the tiers would have been a regression rather than an improvement.

A device holding something Bluetooth cannot carry sends a `ControlWakeWiFi` frame over the open
link, and its peer raises Wi-Fi in response.
This exists because a device cannot dial its peer on demand: either end may be the listener.

### 3. No fixed roles

Every device listens **and** dials on both tiers.

- **Wi-Fi**: when two devices dial each other at once they collide, and the link opened by the
  lower fingerprint survives.
  Both ends compute that from values they already exchanged, so there is no negotiation round trip.
- **Bluetooth**: GATT roles are genuinely asymmetric, so `BleRoleRules` decides them
  **capability first, fingerprint second**.
  Advertising is hardware-dependent on Android, so a device that cannot advertise must be the
  central whatever its fingerprint sorts to.
  The naive "lower fingerprint advertises" rule agrees on an arrangement neither device can
  perform.

### 4. One session per peer, and no relaying

`TcpAcceptor` listens, `TcpTransportConnection` is one framed session with one peer, and
`MeshLinks` holds one of those per paired device and fans out on send.

Every device talks to every other directly and nobody forwards anything, so there is no routing
and no loops to prevent.
The trade is that it assumes a complete graph: two devices that cannot reach each other simply do
not sync, rather than being bridged by a third.

### 5. Wire formats

**TCP** is a byte stream with no message boundaries, so frames carry
`[magic u16][version u8][kind u8][length u32][payload]`.
The magic detects a desynchronised stream instead of acting on it, and the length is
bounds-checked before a byte is allocated.
The hello carries a device name, a public key and the mesh name, the last two length-prefixed and
the mesh name optional so an older peer still parses.

**Bluetooth** has the inverse problem: a GATT write is already a message and arrives in order, but
is hard-capped at 512 octets whatever the MTU claims.
Frames are told apart by length alone, except for one:

| Length | Frame |
|---|---|
| 2 bytes | Control: ping, pong, wake Wi-Fi |
| 4 bytes | Chunk receipt |
| 5+ bytes | Data chunk: `[msgId u8][seq u16][total u16][payload]` |
| leading `0x00` | Extended control, currently the identity exchange |

The extended frame borrows the one value a data chunk's message id can never take.
`BleProtocol.NextMessageId` is what keeps that promise.

### 6. The mesh has a name

A set of devices is called something - "Surya's Mesh" - and both apps say that rather than naming
whichever device answered.
With three devices a peer name picks one arbitrarily and reads as though the app pairs with a
single machine.

There is no coordinator to hold the name, so the rule is that the device which starts the mesh
names it, and devices that join adopt it.
It travels in the pairing code and in both hellos, and is adopted **only** by a device that has
none of its own, which is what stops two devices that disagree overwriting each other.

### 7. Clipboard capture

- **Windows**: a message-only window receives `WM_CLIPBOARDUPDATE`.
  All clipboard access happens on one dedicated STA thread, never on the message pump, because
  those calls block for seconds whenever another process holds the clipboard lock.
- **Android**: an accessibility service watches clipboard text.
  A `ContentObserver` on MediaStore intercepts screenshots without touching the clipboard.
  A `connectedDevice` foreground service holds the links, which is what lets them survive Doze.
- Images are downscaled and re-encoded as JPEG before transmission.
- `PROCESS_TEXT` and a share target are two further entry points that avoid the clipboard entirely.

## Current Status

- **Phase 1 (Foundation)**: COMPLETED.
- **Phase 2 (Crypto Engine)**: COMPLETED.
- **Phase 3 & 4 (Transport & Ephemeral Sync)**: COMPLETED.
- **Phase 4b (Bluetooth tier)**: COMPLETED. Text syncs with no network at all.
- **Phase 4c (Bluetooth standby)**: COMPLETED. The tiers are inverted, with a wake frame for what
  Bluetooth cannot carry and Wi-Fi following the screen.
- **Phase 4d (Identity and mesh)**: COMPLETED. Real pairing crypto, a peer registry, symmetric
  roles on both tiers, a session per peer, a foreground service, address handover and a named mesh.
- **Phase 5 (Password Vault)**: PENDING. Next step is SQLite storage and CRDT merging.

### Known gaps

- **No forward secrecy.** The ECDH is static-static, so the same pair always derives the same key.
  Recovering one private key would expose past sessions with that peer.
- **The key file is not hardware-backed.** `device.key` is protected by filesystem permissions.
- **Renaming the mesh is local.** It propagates on joining but not afterwards, because every simple
  rule for which of two names wins either ping-pongs or lets a stale device overwrite the rest.
- **Introduction is designed but not surfaced.** `PeerRegistry.PeersToIntroduceTo` exists so a new
  device can learn the set from one scan instead of one scan per pair. Nothing consumes it yet.
- **Bluetooth caps the mesh at a handful of peers.** A GATT central holds around seven on Android.
  Wi-Fi has no such limit.
- **Connection state is per app, not per peer.** Both apps know whether *anything* is reachable
  rather than which peers are, so a device list can only mark one device connected.

## Risks & Conflict Resolution

- **Dual architecture.** There are two distinct sync engines:
  1. **Clipboard sync (ephemeral)**: never saved or merged. Encrypt on copy, send immediately, drop
     it if nothing is in range. Do not add SQLite or CRDTs here.
  2. **Password vault (persistent)**: needs permanent storage. SQLite and CRDTs with logical clocks.
- **Battery.** Bluetooth is held open because a connection interval of a second or two costs
  microamps between events. Wi-Fi is not, because every heartbeat pulls the chip out of power save.

## Rules for Agents

- Do not introduce cloud dependencies or external third-party servers.
- **Never reintroduce a key that is not derived per pair.** A single shared key is what made every
  install of this app interchangeable.
- Both transports carry the same encrypted payload, so crypto, echo suppression and the activity
  log stay transport-agnostic. Keep it that way.
- **A data chunk's message id must never be zero** - use `BleProtocol.NextMessageId`. Zero marks an
  extended control frame, and the whole identity exchange rests on it.
- **Both devices listen and dial**, so any change to connection handling must stay correct when two
  of them collide. `MeshLinks` settles that by fingerprint; do not add a second rule.
- **Bluetooth role selection is capability first.** Do not simplify it to a fingerprint comparison.
- Diagnostics go through `CoreLib.Diagnostics.Log`, never `Console.WriteLine`. The daemon is a
  `WinExe` with no console attached, so anything written there is discarded.
- Kill `WinDaemon` before building or the build fails on a locked `CoreLib.dll`. It relaunches on
  its own because run-on-startup is enabled.
- Never use `adb shell pm clear` on the Android client. It revokes the accessibility grant, which
  only the user can restore by hand, and every reading taken before they do is meaningless. Use
  `adb install -r` instead, and check the grant survived.
- If editing the Android project, remember that it targets `net10.0-android` and requires
  `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>` for debug builds deployed via CLI.
- **Declaring an Android permission is not requesting it.** The Bluetooth permissions are runtime
  grants on Android 12+, and being refused one fails silently.
- Both apps hold a zero-warning bar. An incremental build will not re-report warnings, so use
  `-t:Rebuild` when you need to be sure.
