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

### Run Tests

```powershell
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

205 tests. Both apps hold a zero-warning bar, and an incremental build will not re-report
warnings, so use `-t:Rebuild` when you need to be sure.

`src/CryptoTest` and `src/TransportTest` are console demos kept from early development.
They print to the screen and assert nothing; the real coverage is in `tests/CoreLib.Tests`.

### Read The Logs

Diagnostics go through `CoreLib.Diagnostics.Log`, never `Console.WriteLine`.
The daemon is a `WinExe` with no console attached, so anything written there is discarded.

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 20   # Windows
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

## Project Structure

- `src/CoreLib`: cross-platform logic shared by both apps.
  - `Identity/`: the device keypair, the peer registry, per-connection session keys and the
    agreement behind them, key-at-rest wrapping, the pairing window and its confirmation queue.
  - `Transport/`: the TCP acceptor and framed session, the per-peer mesh link table, Bluetooth
    fragmentation, protocol constants and role negotiation, content types, file transfer and
    notification framing.
  - Crypto (AES-256-GCM, Argon2id for the future vault), echo suppression, the in-memory activity
    log, and the logging sink.
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
