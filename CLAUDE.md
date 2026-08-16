# Project Guidelines & Quick Reference

Please refer to [AGENTS.md](AGENTS.md) for the project overview, architecture rules, and strict
guidelines for AI assistants.
Read [HANDOFF.md](HANDOFF.md) before touching the transports: it records the findings behind the
current design, most of which are not guessable from the documentation.

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
Use `install -r`, which preserves app data and the accessibility grant.
Never use `pm clear`: it revokes the grant, and only the user can restore it by hand.

```powershell
# 1. Build the APK (FastDeployment disabled in .csproj)
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android

# 2. Install to connected device via ADB
adb install -r src/AndroidClient/bin/Debug/net10.0-android/com.companyname.androidclient-Signed.apk

# 3. Confirm the accessibility service survived the install
adb shell settings get secure enabled_accessibility_services
```

### Run Tests

```powershell
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

`src/CryptoTest` and `src/TransportTest` are console demos kept from early development.
They print to the screen and assert nothing; the real coverage is in `tests/CoreLib.Tests`.

### Read The Logs

Diagnostics go through `CoreLib.Diagnostics.Log`, never `Console.WriteLine`.
The daemon is a `WinExe` with no console attached, so anything written there is discarded.

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 20   # Windows
adb logcat -s MeshSync                                               # Android
```

## Project Structure

- `src/CoreLib`: cross-platform logic shared by both apps.
  Crypto (AES-256-GCM, Argon2id), the TCP transport, BLE fragmentation, echo suppression,
  the in-memory activity log, and the logging sink.
- `src/WinDaemon`: WPF window with sidebar navigation, Win32 clipboard listener, TCP server,
  BLE GATT server, and the tray icon.
- `src/AndroidClient`: .NET MAUI setup wizard and dashboard, accessibility service for the
  clipboard, MediaStore observer for screenshots, TCP and BLE clients, and the
  `PROCESS_TEXT` and share targets.
- `src/assets`: brand handoff, the source of truth for the mark, palette and illustrations.
- `tests/CoreLib.Tests`: 57 tests, including transport tests over real loopback sockets.
