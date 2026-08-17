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

Every other tool in this space needs a LAN. That is the whole difference.

**Every device is equal.**
There is no host and no client.
Every device listens and dials, and which one accepts a given link is decided per connection by
comparing key fingerprints.
Laptop to laptop and phone to phone work exactly as phone to laptop does.

**Each connection has its own key.**
Devices authenticate each other by keypair, and every connection agrees a fresh AES-256 key.
A paired device cannot read traffic meant for another pair, and traffic captured today cannot be
opened later by recovering a key.

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

## What it does

| | |
|---|---|
| **Clipboard** | Copy text or an image anywhere, paste it anywhere else |
| **Files** | Send a file from the share sheet, the tray, or by dropping it on the window |
| **Find my device** | Make a device sound an alarm, through silent mode, with no network |
| **Notifications** | Mirror the apps you choose from your phone, and dismiss them from either end |

Everything except files works with no network at all.
Files need Wi-Fi, and asking for one raises it automatically.

Pairing is one QR scan, then one comparison.
The code carries an address, a public key and the mesh name.
The device being joined then shows the four-group fingerprint of the device asking, and you check
it matches what that device is showing. That second step is what stops someone else on the network
getting in by connecting first.

## Installing

There is no store release. Android builds here need the accessibility service to read the
clipboard in the background, which Google Play's accessibility policy does not permit, so the app
is distributed as a signed APK instead.

### Windows

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a machine with Bluetooth LE.

```powershell
dotnet run --project src/WinDaemon/WinDaemon.csproj
```

It runs in the tray and enables run-on-startup the first time.

### Android

Requires the .NET 10 SDK with the `maui-android` workload, and a device on Android 8 or newer.

```powershell
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android
adb install -r src/AndroidClient/bin/Debug/net10.0-android/dev.meshsync.app-Signed.apk
```

Then grant the accessibility service in Settings. Without it, Android will not let any app read
the clipboard in the background and only the share sheet and text-selection entry points work.

## Privacy

Clipboard traffic is ephemeral by design.
It is encrypted for the device it is going to, sent straight there, and never written to disk.
The activity list lives in memory and dies with the process.

Each device holds a P-256 keypair that never leaves it, wrapped by DPAPI on Windows and by the
Android Keystore on Android.
Session keys are agreed by ECDH, so no key material crosses the wire.

[SECURITY.md](SECURITY.md) states what that does and does not protect against, including the parts
it does not.

## Projects

- **`src/CoreLib`** - everything that is not platform specific.
  Identity and pairing, the peer registry, session key agreement, the TCP transport and its mesh
  link table, Bluetooth framing and role negotiation, crypto, echo suppression, the activity log
  and the logging sink.
- **`src/WinDaemon`** - WPF window with a sidebar and a tray icon.
  Win32 clipboard listener, TCP listener and dialler, Bluetooth GATT server and client.
- **`src/AndroidClient`** - .NET MAUI app with a navigation drawer.
  Accessibility service for the clipboard, MediaStore observer for screenshots, a
  `connectedDevice` foreground service, TCP listener and dialler, Bluetooth GATT client and
  server, and `PROCESS_TEXT` and share targets.
- **`src/assets`** - brand handoff: the mark, the palette and the illustrations.
- **`tests/CoreLib.Tests`** - transport tests over real loopback sockets, key agreement, wire
  formats, Bluetooth role rules and the peer registry.

## Status

Clipboard, files, find-my-device and notification mirroring are built and covered by tests.
The clipboard tier has been exercised on real hardware; the rest has not been near a phone since
it was written, which [HANDOFF.md](HANDOFF.md) sets out honestly.

Windows and Android are the platforms today.
macOS and Linux are planned behind a shared desktop shell; an iOS companion is planned as
receive-mostly, because iOS does not let any app watch the clipboard in the background and a
backgrounded iPhone cannot be found over Bluetooth by anything that is not another Apple device.

See [AGENTS.md](AGENTS.md) for the architecture and the rules, and [HANDOFF.md](HANDOFF.md) for the
findings behind the current design - most of them are not guessable from the code and cost real
time to isolate.

## Licence

GPL-3.0. See [LICENSE](LICENSE).
