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

**There is one key per connection**, not per pair and certainly not per mesh.
Each end mints an ephemeral P-256 keypair, announces it in the hello, and the session key mixes
two ECDH secrets through HKDF: the ephemeral one gives forward secrecy, the static one gives
authentication.
An attacker can complete the first with anybody, because it is unauthenticated by construction,
but not the second without a private key this device has paired with - so the two ends never
agree and AES-GCM refuses the payload.
This is the shape of Noise's `KK` handshake, deliberately, so it can be read against a known
pattern rather than assessed as an invention.

Both fingerprints are sorted into the salt so the two ends mix the same bytes in the same order.
Unsorted, they derive different keys and every payload fails to decrypt with nothing on the wire
to say why.

The key therefore belongs to the connection, which is what `PeerSession` is.
Disposing it is what makes the traffic unrecoverable, so a link that closes takes its key with
it.

A listener refuses any peer it has not paired with.
A stranger gets no further than a queue: showing the pairing code says somebody was invited, and
comparing the four-group fingerprint on both screens says it is the *right* somebody.
That second step is what closes the race an attacker on the same network could otherwise win.

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
- **Android**: nothing watches the clipboard, and nothing can.
  Android only lets an app read it while that app is in front, and the accessibility service that
  used to work around this has been removed - UPI and banking apps refuse to run at all while any
  accessibility service is enabled, because that is the route screen-reading fraud takes.
  A clipboard tool that stops you paying for things is not one worth having.
- **Linux and macOS**: whatever the session offers, behind `IClipboardBridge`. `wl-clipboard` is
  the only one that can be *told* the selection changed; `xclip`, `xsel` and `pbpaste` are polled,
  because none of them has a watch mode. Watching in the background on Wayland needs
  `ext_data_control_manager_v1`, which a client can only speak over a native Wayland connection -
  so that watcher is a component of its own rather than something the UI toolkit does.
- So **sending from the phone is user-initiated**, through three routes that each get focus in
  their own way: a Quick Settings tile, the `PROCESS_TEXT` selection menu, and the share sheet.
  Receiving is unaffected - writing to the clipboard has never been restricted.
- A `ContentObserver` on MediaStore intercepts screenshots without touching the clipboard, so
  those still send with no interaction at all.
- A `connectedDevice` foreground service holds the links, which is what lets them survive Doze,
  and now also hosts the screenshot, network and screen watchers.
  A `BOOT_COMPLETED` receiver starts it after a restart - the accessibility service used to be
  what did that, since Android rebinds an enabled one on boot.
- Images are downscaled and re-encoded as JPEG before transmission.

### 8. What the tiers carry, and why the split falls where it does

Everything rides the same encrypted payload with a one-byte content type in front, so a new
feature inherits authentication rather than arranging its own.
The rule for which tier carries what is not preference, it is arithmetic: at roughly 6.7 KB/s
Bluetooth carries anything small and nothing large.

| | Size | Tier |
|---|---|---|
| Text, addresses, ring requests, notifications | Bytes to a kilobyte | Either. Works with no network at all |
| Images | Tens to hundreds of KB | Wi-Fi, raised on demand with the wake frame |
| Files | Unbounded | Wi-Fi only, streamed in 1 MB chunks |

A file is the one thing that is not a single payload. It is an offer, a decision, and a stream
written straight to disk as it arrives, with the SHA-256 in the offer so the receiver knows what
it is checking for before the first byte - which is what makes a truncated transfer a failure
rather than a file that looks complete.

**Ringing is a content type rather than a two-byte control frame, deliberately.**
Control frames ride outside the encrypted path, so anything that knew the service UUID could have
made a phone shriek from across the street.

## Current Status

- **Phase 1 (Foundation)**: COMPLETED.
- **Phase 2 (Crypto Engine)**: COMPLETED.
- **Phase 3 & 4 (Transport & Ephemeral Sync)**: COMPLETED.
- **Phase 4b (Bluetooth tier)**: COMPLETED. Text syncs with no network at all.
- **Phase 4c (Bluetooth standby)**: COMPLETED. The tiers are inverted, with a wake frame for what
  Bluetooth cannot carry and Wi-Fi following the screen.
- **Phase 4d (Identity and mesh)**: COMPLETED. Real pairing crypto, a peer registry, symmetric
  roles on both tiers, a session per peer, a foreground service, address handover and a named mesh.
- **Security prerequisites**: COMPLETED. Forward secrecy on both tiers, the identity key wrapped
  by DPAPI and the Android Keystore, and a fingerprint comparison before a device is let in.
- **Open source**: COMPLETED. GPL-3.0, `dev.meshsync.app`, CI on all three projects, a threat
  model in SECURITY.md.
- **File transfer, find my device, notification mirroring**: COMPLETED.
- **Linux and macOS desktop**: BUILT, Wi-Fi only. `DesktopCore` holds the running device,
  `DesktopShell` is the Avalonia window and tray for both, and `LinuxDaemon` is the same core with
  a terminal in front of it. Clipboard, files, find my device and notification mirroring all work;
  mirrored notifications go into the desktop's own notification centre. No Bluetooth tier and no
  key protector yet. Packaged as an AppImage, a .deb and a tarball by `packaging/build.sh`.
- **Per-peer connection state**: PENDING. Both apps still know whether *anything* is reachable
  rather than which peers are.
- **Password vault**: PENDING and gated. It does not start unless Android autofill and a desktop
  browser extension are also being built, because without those it is not a password manager.

### Known gaps

- **The identity key is wrapped, not hardware-generated.** DPAPI on Windows and a Keystore-held
  AES key on Android both keep the private key off the disk in the clear, but it still exists in
  process memory, so code already running as this user can read it while the app is up. Fixing
  that means generating the key inside the Keystore and doing ECDH through `KeyAgreement`, which
  forks the key agreement between platforms - not worth it for a clipboard, and worth it for a
  vault.
- **Renaming the mesh is local.** It propagates on joining but not afterwards, because every simple
  rule for which of two names wins either ping-pongs or lets a stale device overwrite the rest.
- **Introduction is designed but not surfaced.** `PeerRegistry.PeersToIntroduceTo` exists so a new
  device can learn the set from one scan instead of one scan per pair. Nothing consumes it yet.
- **Bluetooth caps the mesh at a handful of peers.** A GATT central holds around seven on Android.
  Wi-Fi has no such limit.
- **Connection state is per app, not per peer.** Both apps know whether *anything* is reachable
  rather than which peers are, so a device list can only mark one device connected - and it
  currently guesses which by comparing names, which breaks outright with two devices called the
  same thing.
- **Bluetooth splits Linux and macOS apart, and macOS is the one that leaves.**
  They share `DesktopCore` and `DesktopShell` today because Avalonia builds for both from one
  machine, which is the property that made it the right toolkit. Bluetooth breaks that. BlueZ is
  D-Bus and reachable from plain `net10.0`; CoreBluetooth is reachable only from `net10.0-macos`
  or `net10.0-maccatalyst`, and those can only be built on macOS with Xcode. Adding Bluetooth to
  the Mac head therefore stops it being cross-buildable from Linux and needs a Mac or a
  `macos-latest` runner for every build.
  **So the Mac head is to be separated when its Bluetooth is built**, into its own project with
  its own target framework, keeping `DesktopCore` and `DesktopShell` shared and platform-free.
  Until then macOS stays Wi-Fi only and stays cross-published from Linux, which costs it nothing
  it does not already lack.
- **The BLE service UUID is shared by every install**, so a scan finds every Mesh Sync device in
  range and not only the ones in this mesh. Refusing them is not enough on its own: a refusal that
  is not remembered is a reconnection four seconds later. Anything that scans must drop a link that
  does not produce a session and then leave that device alone for a while.
- **Linux Bluetooth is the central half only.** The device scans, connects, exchanges the hello
  and holds the link; it does not yet advertise. BlueZ accepts the scan and rejects the exported
  GATT tree, so `LinuxBlePeripheral` registers, fails and stands aside. That is a supported
  arrangement rather than a missing half: `BleRoleRules` is capability first, so a device that
  cannot advertise is always the central and the peer takes the peripheral role - which is
  exactly what Android does and what HANDOFF records as never having been exercised.
- **macOS has no Bluetooth tier at all**, for the reason above.
- **The Linux identity key is wrapped by the desktop keyring**, through
  `org.freedesktop.secrets` - KWallet on KDE, gnome-keyring on GNOME. A 32-byte key lives in the
  keyring and the device key is sealed with it using the project's own AES-256-GCM, so the blob
  matches what Android writes and `DeviceIdentity` already reads. A machine with no keyring falls
  back to an unwrapped key rather than refusing to start, and a key written before this existed
  upgrades itself on the next run without costing a re-pair.
- **macOS has no key protector.** The Keychain is the equivalent and belongs with the rest of the
  Mac work.
- **Clipboard capture on Linux is native on Wayland and needs a helper on X11.** The desktop
  speaks `ext-data-control` to the compositor directly, so on KDE, wlroots and anything else
  offering that protocol the clipboard works with nothing installed and is *told* about changes
  rather than polling. X11 sessions and compositors without the protocol fall back to
  `wl-clipboard`, `xclip` or `xsel`, and a session with none of those still pairs, holds links
  and sends - it just cannot reach the clipboard. Avalonia runs through XWayland and an XWayland
  client cannot speak `ext-data-control`, which is why the watcher holds its own native Wayland
  connection instead of going through the toolkit.
- **A file transfer does not resume.** A failure restarts it. Worth saying out loud so it does
  not get half-built.

## Risks & Conflict Resolution

- **Dual architecture.** There are two distinct sync engines:
  1. **Clipboard sync (ephemeral)**: never saved or merged. Encrypt on copy, send immediately, drop
     it if nothing is in range. Do not add SQLite or CRDTs here.
  2. **Password vault (persistent)**: needs permanent storage. SQLite and CRDTs with logical clocks.
- **Battery.** Bluetooth is held open because a connection interval of a second or two costs
  microamps between events. Wi-Fi is not, because every heartbeat pulls the chip out of power save.

## Rules for Agents

- Do not introduce cloud dependencies or external third-party servers.
- **Never reintroduce a key that is not agreed per connection.** A single shared key is what made
  every install of this app interchangeable, and a per-pair one that never changes is what made
  every past session recoverable from one stolen private key.
- **Do not cache a session key against a peer.** It belongs to the connection; caching it against
  the device is exactly the thing that removed forward secrecy the first time, and it also
  quietly breaks revocation, because a forgotten device keeps working until its link drops.
- **Trust-on-first-use is not enough on its own.** A stranger inside the pairing window is queued
  for a human to compare fingerprints, never trusted outright. Do not add a path that skips it.
- **Bluetooth control frames are not encrypted.** Anything that acts on one must first check the
  peer has identified itself, or it is reachable by anybody who knows the service UUID. Ping is
  the exception, because the liveness handshake runs before the identity exchange.
- Both transports carry the same encrypted payload, so crypto, echo suppression and the activity
  log stay transport-agnostic. Keep it that way.
- **A new content type goes in `SyncContent` and nowhere else**, and both apps must dispatch on
  it. `SyncContentTests` fails until it is declared there, which is the reminder to go and handle
  it in both - a collision would route a file chunk into the clipboard and look like nothing more
  than an odd log line.
- **Nothing that arrives from a peer decides where bytes land without being parsed.** An address
  is checked as an IP; a filename is stripped of every path part on arrival as well as on the way
  out. Both arrive inside authenticated payloads from paired devices, and both are still parsed
  rather than believed.
- **Mirrored notifications are never written down.** Not to the activity log, not to a cache, and
  not into a log line carrying their contents. They are the most private thing this app touches.
- **A data chunk's message id must never be zero** - use `BleProtocol.NextMessageId`. Zero marks an
  extended control frame, and the whole identity exchange rests on it.
- **Both devices listen and dial**, so any change to connection handling must stay correct when two
  of them collide. `MeshLinks` settles that by fingerprint; do not add a second rule.
- **Bluetooth role selection is capability first.** Do not simplify it to a fingerprint comparison.
- Diagnostics go through `CoreLib.Diagnostics.Log`, never `Console.WriteLine`. The daemon is a
  `WinExe` with no console attached, so anything written there is discarded.
- Kill `WinDaemon` before building or the build fails on a locked `CoreLib.dll`. It relaunches on
  its own because run-on-startup is enabled.
- **Never reintroduce an accessibility service.** It is the only way to read the clipboard in the
  background and it is not worth it: UPI and banking apps in India refuse to run while one is
  enabled, so the app would make the phone worse at something the owner needs far more than
  clipboard sync. Sending is user-initiated on Android and that is a deliberate ceiling.
- `adb shell pm clear` still wipes the identity and the paired devices, which costs a re-pair.
  Use `adb install -r`, which keeps them.
- If editing the Android project, remember that it targets `net10.0-android` and requires
  `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>` for debug builds deployed via CLI.
- **Declaring an Android permission is not requesting it.** The Bluetooth permissions are runtime
  grants on Android 12+, and being refused one fails silently.
- Both apps hold a zero-warning bar. An incremental build will not re-report warnings, so use
  `-t:Rebuild` when you need to be sure.
