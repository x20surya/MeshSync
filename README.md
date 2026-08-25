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
| **Clipboard** | Copy on the desktop and paste on the phone with nothing to do. The other way takes one tap - [see below](#sending-from-the-phone) |
| **Files** | Send a file from the share sheet, the tray, or by dropping it on the window |
| **Find my device** | Make a device sound an alarm, through silent mode, with no network |
| **Notifications** | Mirror the apps you choose from your phone, and dismiss them from either end |
| **Connection** | Choose Wi-Fi and Bluetooth, Wi-Fi only, or Bluetooth only - it takes effect at once and is remembered |

Everything except files works with no network at all.
Files need Wi-Fi, and asking for one raises it automatically.

Pairing is one QR scan, then one comparison.
The code carries an address, a public key and the mesh name.
The device being joined then shows the four-group fingerprint of the device asking, and you check
it matches what that device is showing. That second step is what stops someone else on the network
getting in by connecting first.

## Installing

### Debian and Ubuntu

Also Mint, Pop!_OS, Zorin, elementary, Kali and Raspberry Pi OS on amd64.

```bash
sudo install -d -m 0755 /usr/share/keyrings
curl -fsSL http://x20surya.me/MeshSync/meshsync.gpg \
  | sudo tee /usr/share/keyrings/meshsync.gpg > /dev/null

echo "deb [arch=amd64 signed-by=/usr/share/keyrings/meshsync.gpg] http://x20surya.me/MeshSync stable main" \
  | sudo tee /etc/apt/sources.list.d/meshsync.list > /dev/null

sudo apt update && sudo apt install meshsync
```

It installs to `/opt/meshsync` with a launcher entry, the Plasma widget and the icons, and
`apt upgrade` carries it forward from there. Nothing else is needed - the build is self-contained
and wants no .NET runtime.

The repository is served over `http` and every index is GPG-signed, which is how apt authenticates
a repository, and why Debian's own mirrors are `http` too. The signing key is
`64B7 9912 F802 21C1 0E3A 341D 4C84 A1AE A04A B302`.

### Fedora, Arch, openSUSE, NixOS

The **AppImage** from any [release](https://github.com/x20surya/MeshSync/releases):

```bash
chmod +x MeshSync-v0.5.0-linux-x86_64.AppImage
./MeshSync-v0.5.0-linux-x86_64.AppImage
```

If it fails naming `libfuse.so.2`, that is FUSE 2, which Ubuntu 22.04+ and current Fedora no
longer install. Either add `libfuse2`, or skip FUSE with `--appimage-extract-and-run`.

`packaging/install-user.sh` does a full install with no root - launcher entry, icons, the Plasma
widget, `meshsyncctl` and D-Bus activation.

### Windows and Android

A self-contained `.exe` and a signed `.apk` are attached to every release. There is no store
listing yet.

### What it runs on

**GLIBC_2.27 and newer** - Ubuntu 18.04, Debian 10, and anything since. Measured from the shipped
binaries, not assumed.

**Every artifact is x86-64.** A Raspberry Pi or an arm64 machine gets nothing yet; `build.sh`
already takes `ARCH=arm64`, but the release workflow only ever calls it for x64.

Full detail in [docs/reference/installing.md](docs/reference/installing.md).

## Building from source

### Windows

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a machine with Bluetooth LE.

```powershell
dotnet run --project src/WinDaemon/WinDaemon.csproj
```

It runs in the tray and enables run-on-startup the first time.

### Linux

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).
The window and the tray icon are Avalonia.

```bash
dotnet run --project src/DesktopShell/DesktopShell.csproj
```

There is a headless build too, for a machine with no desktop session:

```bash
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj
```

On Wayland the clipboard needs nothing installed: the app speaks `ext-data-control` to the
compositor itself, so it is told when the selection changes rather than polling for it.
X11 sessions fall back to `xclip` or `xsel`.
With none of those the desktop still holds links and still sends; it just cannot reach the
clipboard.

Bluetooth works on Linux, as the central: this device scans for a peer advertising the mesh
service, connects, and holds the link, so text still crosses with no network at all. It does not
advertise yet, which means the phone takes the peripheral role - the role rules were built for
exactly that.

macOS is parked. Nothing has ever launched the Mac binary, it has no radio, no key protector and
no clipboard watcher, and carrying an unverified platform through the v0.4 transport refactor was
maintaining a claim nobody had checked. The cross-publish target is still in the solution, and the
radio interface is shaped so CoreBluetooth drops in behind it when somebody wants it - which needs
macOS and Xcode to build, and therefore splits the Mac head out of the shared Linux build.

### Packages

```bash
packaging/build.sh
```

Produces an AppImage that runs on most distributions, a `.deb`, and a plain tarball.
Nothing there needs root.

### Android

Requires the .NET 10 SDK with the `maui-android` workload, and a device on Android 8 or newer.

```powershell
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android
adb install -r src/AndroidClient/bin/Debug/net10.0-android/dev.meshsync.app-Signed.apk
```

### Sending from the phone

Android only lets an app read the clipboard while that app is in front, so sending from the phone
is something you do rather than something that happens:

- **Quick Settings tile** - add "Send clipboard" to your shade, then it is one tap from anywhere.
- **Select text** - highlight anything and pick "Send to my devices" from the menu.
- **Share** - share to Mesh Sync from any app, which also covers files and images.
- **Screenshots** go automatically, with no tap at all.

Receiving is never restricted, so anything sent *to* the phone arrives on its own.

Mesh Sync deliberately does **not** use an accessibility service to work around this. That is the
only way to read the clipboard in the background, and UPI and banking apps refuse to run while any
accessibility service is enabled - they treat it as a fraud risk, correctly, because it is the
route screen-reading fraud takes. A clipboard tool is not worth breaking payments for.

## Privacy

Clipboard traffic is ephemeral by design.
It is encrypted for the device it is going to, sent straight there, and never written to disk.
The activity list lives in memory and dies with the process.

Each device holds a P-256 keypair that never leaves it, wrapped by DPAPI on Windows, by the
Android Keystore on Android, and by the desktop keyring on Linux.
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
- **`src/DesktopCore`** - the running device for Linux, with no UI.
  Identity and registry loading, the route providers, payload dispatch, pairing, the Bluetooth
  tier over BlueZ, and the clipboard behind an interface so a session with no helper still runs.
- **`src/DesktopShell`** - Avalonia window and tray icon for Linux.
  The same sidebar, palette and type scale as the Windows daemon.
- **`src/LinuxDaemon`** - the same core with a terminal in front of it, for a headless machine or
  for driving from a script.
- **`src/AndroidClient`** - .NET MAUI app with a navigation drawer.
  A `connectedDevice` foreground service holding the links and the screenshot, network and
  screen watchers; a boot receiver; a notification listener; TCP listener and dialler; Bluetooth
  GATT client and server; and the Quick Settings tile, `PROCESS_TEXT` and share targets.
- **`src/assets`** - brand handoff: the mark, the palette and the illustrations.
- **`tests/CoreLib.Tests`** - 455 tests: a three-device mesh over real loopback sockets, the
  per-peer route state machine, the mesh beacon and its advertisement budget, key agreement, wire
  formats, Bluetooth role rules and the peer registry. A fake radio replays every hard-won
  Bluetooth finding as a scripted scenario.

## Status

Clipboard, files, find-my-device and notification mirroring are built and covered by tests.
The clipboard tier has been exercised on real hardware; the rest has not been near a phone since
it was written, which [HANDOFF.md](HANDOFF.md) sets out honestly.

Windows, Android and Linux all run the same connection layer as of v0.4: one link object per
paired device owning every route to it, one supervisor over the lot, and a radio scheduler that
holds several Bluetooth links at once rather than one.
Clipboard, files, find my device and notification mirroring all work, mirrored notifications land
in the desktop's own notification centre, and the clipboard needs nothing installed on Wayland.
Linux has the Bluetooth tier as a central and wraps its identity key with the desktop keyring.

**A device from another mesh is now told apart before anything connects.** Every install
advertises the same service UUID, so a scan finds all of them; the advertisement carries six bytes
that say which mesh a device belongs to, and a scanner skips anything whose tag it cannot open.
It decides who to *try* and never who is let in - pairing and the per-connection key agreement are
unchanged.

Two devices can now pair with no network at all, which was the last step that did not honour this
project's own central claim.

macOS is parked; see the note above.

An iOS companion is planned as receive-mostly, because iOS does not let any app watch the
clipboard in the background and a backgrounded iPhone cannot be found over Bluetooth by anything
that is not another Apple device.

See [AGENTS.md](AGENTS.md) for the architecture and the rules, and [HANDOFF.md](HANDOFF.md) for the
findings behind the current design - most of them are not guessable from the code and cost real
time to isolate.

## Licence

GPL-3.0. See [LICENSE](LICENSE).
