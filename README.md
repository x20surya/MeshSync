# Mesh Sync

A local-first universal clipboard for your own devices.
Copy on one, paste on another.

No cloud, no server, no account.
If two of your devices can see each other, they sync; if they cannot, nothing is queued anywhere.

## What makes it unusual

**It works with no network at all.**
Bluetooth is the link that is held open, not the fallback.
No router, no hotspot, nothing: two devices and a radio.
Wi-Fi is raised when there is an image to send or when the screen comes on, and dropped again.

**Every device is equal.**
There is no host and no client.
Every device listens and dials, and which one accepts a given link is decided per connection by
comparing key fingerprints.
Laptop to laptop and phone to phone work exactly as phone to laptop does.

**Each pair of devices has its own key.**
Devices authenticate each other by keypair, and every pair agrees a separate AES-256 key.
A paired device cannot read traffic meant for another pair.

## How it works

Two tiers, both device to device.

| | Bluetooth LE | Wi-Fi |
|---|---|---|
| When | Held open whenever a peer is in range | Raised on screen-on, on demand, or when Bluetooth is down |
| Carries | Text, presence, control frames | Anything, and the only tier that carries images |
| Needs a network | No | Yes |
| Throughput | About 6.7 KB/s | Whatever the LAN does |

A device that copies something Bluetooth cannot carry sends a wake frame over the link that is
already open, and its peer raises Wi-Fi in response.

Pairing is one QR scan.
The code carries an address, a public key and the mesh name, so the device that scans it joins
something named rather than pairing with an anonymous machine.

## Privacy

Clipboard traffic is ephemeral by design.
It is encrypted for the device it is going to, sent straight there, and never written to disk.
The activity list lives in memory and dies with the process.

Each device holds a P-256 keypair that never leaves it.
Session keys are agreed by ECDH against a peer's public key, so no key material crosses the wire.

## Projects

- **`src/CoreLib`** - everything that is not platform specific.
  Identity and pairing, the peer registry, the TCP transport and its mesh link table, Bluetooth
  framing and role negotiation, crypto, echo suppression, the activity log and the logging sink.
- **`src/WinDaemon`** - WPF window with a sidebar and a tray icon.
  Win32 clipboard listener, TCP listener and dialler, Bluetooth GATT server and client.
- **`src/AndroidClient`** - .NET MAUI app with a navigation drawer.
  Accessibility service for the clipboard, MediaStore observer for screenshots, a
  `connectedDevice` foreground service, TCP listener and dialler, Bluetooth GATT client and
  server, and `PROCESS_TEXT` and share targets.
- **`src/assets`** - brand handoff: the mark, the palette and the illustrations.
- **`tests/CoreLib.Tests`** - 138 tests, including transport tests over real loopback sockets.

## Status

Clipboard sync is complete and works on both tiers in both directions.
The password vault is the next phase and is not started.

See [AGENTS.md](AGENTS.md) for the architecture and the rules, and [HANDOFF.md](HANDOFF.md) for
the state, the findings behind the current design, and the known gaps.
Build and deployment commands are in [CLAUDE.md](CLAUDE.md).
