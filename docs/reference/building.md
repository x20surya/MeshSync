---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - packaging/build.sh
  - packaging/apt-repo.sh
updated: 2026-08-25
---

# Building and running

> Installing a **released** build is [[installing]]. This note is about building one.

.NET 10 SDK for everything. Android also needs the `maui-android` workload.

## The five heads

```bash
# Windows - kill it first or the build fails on a locked CoreLib.dll
Get-Process WinDaemon -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project src/WinDaemon/WinDaemon.csproj

# Linux and macOS, windowed
dotnet run --project src/DesktopShell/DesktopShell.csproj

# Linux and macOS, headless
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj

# Android
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android
adb install -r src/AndroidClient/bin/Debug/net10.0-android/dev.meshsync.app-Signed.apk

# macOS, cross-published from Linux
dotnet publish src/DesktopShell/DesktopShell.csproj -c Release -r osx-arm64 --self-contained true
```

**Use `adb install -r`.** `pm clear` wipes the identity and the paired devices, costing a re-pair.
MAUI device deployment is broken enough on .NET 10 that this is done by hand, with
FastDeployment disabled in the `.csproj`.

## Two devices on one machine

The only way to exercise a third device without a third piece of hardware.

```bash
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj -- --data ~/dev2 --port 45002
```

`--data` moves the whole data directory; `--port` moves the listener.
Both are needed: they cannot share either.

This arrangement is what found the two-ports bug and the `WouldLosePort` bug, because in
the field every device listens on 45001 and neither one shows.
See [[on-disk-formats]].

## `LinuxDaemon` flags and commands

**Flags**: `--data`, `--port`, `--name`, `--no-shell`, `--quiet`, `--help`.

`--no-shell` holds the links open with nobody to take commands from, which is what a service
manager wants.

**Shell commands**: `pair`, `join`, `confirm`, `reject`, `forget`, `peers`, `send`, `clip`,
`clipset`, `ring`, `unring`, `bt`, `bluetooth`, `transport`, `name`, `status`, `uri`, `help`,
`quit`.

Note `--no-shell` and `--quiet` change the timing enough to hide the `Console.In` race described
in [[desktop-core]]. If the Bluetooth tier goes quiet, that is the first thing to read.

## Packaging

```bash
packaging/build.sh
```

Produces an **AppImage**, a **`.deb`** and a **tarball** into `packaging/out`.

The `.deb` is also what people install from, through the apt repository at
`http://x20surya.me/MeshSync` - see [[apt-repository]]. `packaging/apt-repo.sh` builds
that repository from a directory of `.deb` files and can be run locally against a throwaway key,
which is the point of it being a script rather than only a workflow step.
Nothing needs root. `appimagetool` is fetched on first use and cached in `packaging/.tools`.

`ARCH` defaults to `x64`; `arm64` also works.
The version is scraped from `<ApplicationDisplayVersion>` in the Android csproj, which is the
single place the version lives.

The headless daemon ships alongside the windowed one, for machines with no desktop session.
`appimagetool` is invoked with `--appimage-extract-and-run` so it works on a machine with no
FUSE, such as a CI runner or a container.

`packaging/install-user.sh` installs for the current user without root.

## Tests

```bash
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

440 cases as of 2026-08-24, in about two seconds. See [[testing]].

**All three heads build on Linux**, which is worth knowing before assuming CI is the only check
for the two that are not native here.

```bash
dotnet build src/WinDaemon/WinDaemon.csproj -p:EnableWindowsTargeting=true -warnaserror
dotnet build src/AndroidClient/AndroidClient.csproj -f net10.0-android -warnaserror
```

Without `EnableWindowsTargeting` the Windows daemon fails at `NETSDK1100` before it compiles a
line, which reads like an unsupported project rather than a missing flag.

**Every project holds a zero-warning bar.**
An incremental build will not re-report warnings, so use `-t:Rebuild` when you need to be sure.

## Logs

Diagnostics go through `CoreLib.Diagnostics.Log`, **never `Console.WriteLine`**: the Windows
daemon is a `WinExe` with no console attached, so anything written there is discarded.

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 20   # Windows
```
```bash
tail -f ~/.local/share/MeshSync/daemon.log                           # Linux and macOS
adb logcat -s MeshSync                                               # Android
```

## Resetting

Deleting `device.key` or `peers.json` forces a re-pair, which is the fastest way to test
[[pairing]] from scratch without `pm clear`.
See [[on-disk-formats]] for where they live.

## See also

[[testing]] · [[on-disk-formats]] · [[linux-daemon]] · [[android-client]] · [[windows-daemon]]
