# Project Guidelines & Quick Reference

Please refer to [AGENTS.md](AGENTS.md) for the project overview, architecture rules, and strict
guidelines for AI assistants.
Read [HANDOFF.md](HANDOFF.md) before touching the transports or pairing: it records the findings
behind the current design, most of which are not guessable from the documentation.

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

### Run Tests

```powershell
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

252 tests. Every app holds a zero-warning bar, and an incremental build will not re-report
warnings, so use `-t:Rebuild` when you need to be sure.

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

- `src/CoreLib`: cross-platform logic shared by both apps.
  - `Identity/`: the device keypair, the peer registry, per-connection session keys and the
    agreement behind them, key-at-rest wrapping, the pairing window and its confirmation queue.
  - `Transport/`: the TCP acceptor and framed session, the per-peer mesh link table, Bluetooth
    fragmentation, protocol constants and role negotiation, content types, file transfer and
    notification framing.
  - Crypto (AES-256-GCM, Argon2id for the future vault), echo suppression, the in-memory activity
    log, and the logging sink.
- `src/DesktopCore`: the running device, shared by both Linux and Mac heads.
  Identity and registry loading, the Wi-Fi links, payload dispatch, the dial loop, pairing and
  the pluggable clipboard bridge. No UI and no platform assumptions beyond POSIX paths.
- `src/DesktopShell`: the Avalonia window and tray icon for Linux and macOS.
  The same sidebar, palette and type scale as the Windows daemon, on a toolkit that builds on
  either platform. Wi-Fi only for now; there is no Bluetooth tier here yet.
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
- `tests/CoreLib.Tests`: 205 tests, including transport tests over real loopback sockets.
