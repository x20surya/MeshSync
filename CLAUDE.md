# Project Guidelines & Quick Reference

Please refer to [AGENTS.md](AGENTS.md) for the project overview, architecture rules, and strict guidelines for AI assistants.

## Useful Commands

### Build & Run Windows Daemon
```powershell
dotnet run --project src/WinDaemon/WinDaemon.csproj
```

### Build & Deploy Android App (Manually)
Because of .NET 10 MAUI device deployment bugs, we compile and push manually:
```powershell
# 1. Build the APK (FastDeployment disabled in .csproj)
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android

# 2. Install to connected device via ADB
adb install -r src/AndroidClient/bin/Debug/net10.0-android/com.companyname.androidclient-Signed.apk
```

### Run Crypto Simulation Tests
```powershell
dotnet run --project src/CryptoTest/CryptoTest.csproj
```

## Project Structure
- `src/CoreLib`: Cross-platform business logic & crypto engine (AES-256-GCM, Argon2id).
- `src/WinDaemon`: Win32 background clipboard listener.
- `src/AndroidClient`: .NET MAUI Android Accessibility Service app.
- `src/CryptoTest`: Console simulation of cross-platform cryptography.
