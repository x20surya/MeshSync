# Project Guidelines & Quick Reference

Please refer to [AGENTS.md](AGENTS.md) for the project overview, architecture rules, and strict
guidelines for AI assistants.
Read [HANDOFF.md](HANDOFF.md) before touching the transports or pairing: it records the findings
behind the current design, most of which are not guessable from the documentation.

**Start at [docs/Home.md](docs/Home.md) when the question is "what is this feature, where does it
live, and which platforms have it".**
`docs/` is a linked vault with one note per feature, mechanism and head - three notes and a file
path usually beat reading AGENTS.md and HANDOFF.md in full.
[docs/platform-matrix.md](docs/platform-matrix.md) is the one table nothing else in the repo has.
If you change a file a note lists under **Where it lives**, update that note in the same commit.

## Useful Commands

### Build & Run Windows Daemon

Kill any running instance first, or the build fails on a locked `CoreLib.dll`.
It relaunches on its own because run-on-startup is enabled.

```powershell
Get-Process WinDaemon -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project src/WinDaemon/WinDaemon.csproj
```

### Build & Deploy Android App (Manually)

Because of .NET 10 MAUI device deployment bugs, we compile and push manually.
Use `install -r`, which preserves app data, the device identity and the paired devices.

**The next install after the rename is the exception.** The application id moved from
`com.companyname.androidclient` to `dev.meshsync.app`, and Android treats that as a different app:
the old one has to be uninstalled and the devices paired again. Once only.
Avoid `pm clear` otherwise: it wipes the identity and the paired devices, which costs a re-pair.

```powershell
# 1. Build the APK (FastDeployment disabled in .csproj)
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android

# 2. Install to connected device via ADB
adb install -r src/AndroidClient/bin/Debug/net10.0-android/dev.meshsync.app-Signed.apk

# 3. Confirm the identity and paired devices survived the install
adb shell run-as dev.meshsync.app ls /data/data/dev.meshsync.app/files
```

### Build & Run The Linux Desktop

The Avalonia shell is the Linux and Mac head.
It runs in the tray and holds the links whether or not the window is open.

```bash
dotnet run --project src/DesktopShell/DesktopShell.csproj
```

The headless daemon is the same core with a terminal in front of it, which is what to reach for
when there is no desktop session or when something needs driving from a script.

```bash
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj
```

Its shell takes `pair`, `uri`, `join`, `confirm`, `reject`, `forget`, `peers`, `status`, `send`,
`clip`, `clipset`, `ring`, `unring`, `bt`, `bluetooth`, `transport`, `name`, `help` and `quit`.
`transport` shows or sets which links this device offers - `both`, `wifi` or `ble` - and applies it without a restart.
`links` prints every route to every peer and why any of them is not up, which is the fastest way to
tell "Bluetooth is broken" from "two devices seen, neither in this mesh".
`--no-shell` holds the links open with nobody to take commands from, which is what a service manager wants and what to reach for when driving it from a script.

`--data` and `--port` together run a second device on one machine, which is how the mesh is
exercised without a second piece of hardware.

```bash
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj -- --data ~/dev2 --port 45002
```

### Build For macOS

Cross-published from Linux. The binary is real; signing and notarising still need a Mac.

```bash
dotnet publish src/DesktopShell/DesktopShell.csproj -c Release -r osx-arm64 --self-contained true
```

### Package And Publish

```bash
packaging/build.sh                      # AppImage, .deb and tarball into packaging/out
packaging/apt-repo.sh <debs> <out>      # a signed apt repository from those .debs
```

A release is a `v*` tag: CI builds all four heads, publishes the GitHub release, and republishes
the apt repository at `http://x20surya.me/MeshSync` from the last three releases.
The signing key lives in the `APT_GPG_PRIVATE_KEY` and `APT_GPG_PASSPHRASE` repository secrets.
See [docs/reference/apt-repository.md](docs/reference/apt-repository.md) and
[docs/reference/installing.md](docs/reference/installing.md).

### Run And Check The Plasma Widget

The widget is QML with no build step, so it is run out of the working tree and checked against a
live bus rather than compiled.

```bash
plasma/preview.sh    # the working tree in one window, against whichever daemon is running
plasma/check.sh      # a scratch daemon, every MeshBus call, and dbus-monitor reading the wire
```

`check.sh` asserts **how many bytes were on the wire**, not that no exception was thrown - every
defect it exists for produces a call that is dispatched and answered. See
[docs/reference/testing.md](docs/reference/testing.md).

Installing the widget is not enough to see a change: plasmashell holds the QML it has loaded.

```bash
systemctl --user restart plasma-plasmashell.service
```

### Run Tests

```powershell
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

455 tests. Every app holds a zero-warning bar, and an incremental build will not re-report
warnings, so use `-t:Rebuild` when you need to be sure.

**All three heads build on Linux**, which is worth knowing before assuming CI is the only check.
The Windows daemon needs one flag, and the Android workload is already installed here.

```bash
dotnet build src/WinDaemon/WinDaemon.csproj -p:EnableWindowsTargeting=true -warnaserror
dotnet build src/AndroidClient/AndroidClient.csproj -f net10.0-android -warnaserror
```

`src/CryptoTest` and `src/TransportTest` are console demos kept from early development.
They print to the screen and assert nothing; the real coverage is in `tests/CoreLib.Tests`.

### Read The Logs

Diagnostics go through `CoreLib.Diagnostics.Log`, never `Console.WriteLine`.
The daemon is a `WinExe` with no console attached, so anything written there is discarded.

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 20   # Windows
```

```bash
tail -f ~/.local/share/MeshSync/daemon.log                           # Linux and macOS
adb logcat -s MeshSync                                               # Android
```

### Identity & Pairing State

Deleting either file forces a re-pair, which is the fastest way to test the pairing flow from
scratch without `pm clear`.

```powershell
# Windows
%LOCALAPPDATA%\MeshSync\device.key     # this device's keypair
%LOCALAPPDATA%\MeshSync\peers.json     # paired devices and the mesh name

# Android (app-private)
adb shell run-as dev.meshsync.app ls /data/data/dev.meshsync.app/files
```

```bash
# Linux and macOS - $XDG_DATA_HOME/MeshSync when that is set
~/.local/share/MeshSync/device.key
~/.local/share/MeshSync/peers.json
```

## Project Structure

- `docs`: the feature vault. One note per feature, mechanism and head, linked, and readable
  either as Markdown or as an Obsidian vault. An index over the code, never a copy of it.
- `src/CoreLib`: cross-platform logic shared by both apps.
  - `Identity/`: the device keypair, the peer registry, per-connection session keys and the
    agreement behind them, key-at-rest wrapping, the pairing window and its confirmation queue.
  - `Transport/Fabric/`: **the connection layer every head runs on.** One `PeerLink` per paired
    device owning every route to it, one supervisor with a watchdog reconciling what exists
    against what `RoutePolicy` wants, and the socket route.
  - `Transport/Ble/`: one scheduler over one adapter holding several links at a time, refusals
    remembered three ways, and the mesh beacon that tells this mesh from anyone else's before a
    connection is opened.
  - `Transport/`: the TCP acceptor and framed session, Bluetooth fragmentation, protocol constants
    and role negotiation, content types, file transfer and notification framing.
    Also the shared answers no head may reimplement: `LinkState` (is anything reachable and over which link, derived from the fabric now), `TransportSettings` with `ITransportPreferenceStore` (which links a device may offer), and `BleLinkArbiter` (which half of a radio link this device takes for a given peer).
  - Crypto (AES-256-GCM, Argon2id for the future vault), echo suppression, the in-memory activity
    log, and the logging sink.
- `src/DesktopCore`: the running device for Linux.
  Identity and registry loading, the route providers, payload dispatch, pairing and the pluggable
  clipboard bridge. The connection layer itself is `CoreLib.Transport.Fabric`; this supplies
  conditions and storage.
  It also holds the Bluetooth tier over BlueZ - `LinuxBleRadio` scans and connects, `LinuxBleLink`
  is one link - central only, because BlueZ rejects the exported GATT tree.
  No UI and no platform assumptions beyond POSIX paths. macOS is parked; see `docs/platform-matrix.md`.
- `src/DesktopShell`: the Avalonia window and tray icon for Linux and macOS.
  The same sidebar, palette and type scale as the Windows daemon, on a toolkit that builds on
  either platform.
  Both tiers on Linux, Wi-Fi only on macOS, and a connection preference in Settings offering the same three modes as the Windows window.
- `src/LinuxDaemon`: the same core with a terminal in front of it.
  Exists so the transport can be exercised with no desktop session and no clipboard helper, and
  so two devices can be run on one machine.
- `src/WinDaemon`: WPF window with sidebar navigation, a device list, mirrored notifications and a
  file drop target; Win32 clipboard listener, TCP listener and dialler, Bluetooth GATT server and
  client, the ringer, and the tray icon.
- `src/AndroidClient`: .NET MAUI app with a navigation drawer (Home, Activity, Devices, Settings,
  About) plus the setup wizard, a `connectedDevice` foreground service hosting the screenshot,
  network and screen watchers, a boot receiver, a notification listener for mirroring, TCP listener
  and dialler, Bluetooth GATT client and server, the ringer, and the Quick Settings tile,
  `PROCESS_TEXT` and share targets.
- `src/assets`: brand handoff, the source of truth for the mark, palette and illustrations.
- `tests/CoreLib.Tests`: 455 tests, including a three-device mesh over real loopback sockets, the
  per-peer route state machine, the mesh beacon and its 31-byte advertisement budget, and a fake
  radio that replays every Bluetooth finding in `HANDOFF.md` as a scripted scenario.
