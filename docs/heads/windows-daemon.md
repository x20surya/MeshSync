---
type: head
status: shipped
platforms: [windows]
tier: either
code:
  - src/WinDaemon/Program.cs
  - src/WinDaemon/MainWindow.xaml
  - src/WinDaemon/WinDaemon.csproj
  - packaging/windows/MeshSync.wxs
  - packaging/windows/build.ps1
updated: 2026-08-27
---

# Windows daemon

WPF window with sidebar navigation, a device list, mirrored notifications and a file drop target.
Runs in the tray and enables run-on-startup the first time.

One of the two finished platforms, alongside [[android-client]].

## What is in it

| File | Does |
|---|---|
| `Program.cs` | the running device, the TCP listener and dialler, the scan gate |
| `MainWindow.xaml` / `.cs` | sidebar, device list, notifications, file drop |
| `ClipboardWorker.cs` | the Win32 listener, on its own STA thread |
| `WindowsBleTransport.cs`, `WindowsBleCentral.cs` | both GATT halves |
| `Ringer.cs` | [[find-my-device]] |
| `MirroredNotifications.cs`, `WindowsToasts.cs` | [[notification-mirroring]] |
| `WindowsKeyProtector.cs` | DPAPI, see [[key-at-rest]] |
| `RegistryTransportPreferenceStore.cs` | [[transport-preference]] storage |
| `TrayIcons.cs`, `ThemeManager.cs`, `Themes/` | tray and palette |

## Running it

**Kill any running instance first**, or the build fails with `MSB3021` on a locked `CoreLib.dll`.
It relaunches on its own because run-on-startup is enabled.

```powershell
Get-Process WinDaemon -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project src/WinDaemon/WinDaemon.csproj
```

It is a `WinExe` with no console attached, so `Console.WriteLine` is discarded.
Everything goes through `CoreLib.Diagnostics.Log`.
See [[activity-log]].

## What is missing

**Per-peer [[link-state]].**
Windows answers per app, so it can mark only one device connected and guesses which by comparing
names, which breaks with two devices called the same thing.
The desktop head already answers per peer.
This is the remaining half of that work.

## Three things drawn or generated rather than shipped

**The tray icon** is drawn at runtime from the brand mark's own geometry, two rings of dots.
Rendering rather than shipping an `.ico` keeps it crisp at every DPI.

**The ring tone** is a two-second WAV built in memory: two alternating tones, which carry further
than one. A bundled sound file is one more thing to ship and to keep in step.
Capped at `MaxDuration` of one minute.

**The toast registration** is an AppUserModelID written under `HKCU`.
Windows will not raise a toast for a process without one, and the usual way to give it one is a
Start Menu shortcut carrying the id.
The installer does create that shortcut, but the registration stays: the portable `.exe` has no
shortcut, and the same code has to work either way.

## How it ships

An `.msi` and a portable `.exe`, both built by `packaging/windows/build.ps1`. See [[installing]].

**The installer is what a person should be given.** Per machine into `Program Files\Mesh Sync`, a
Start Menu shortcut carrying the AppUserModelID above, an Installed apps entry, and a firewall
rule for TCP 45001 scoped to the local subnet - so Windows never raises its own firewall prompt,
which needs an administrator and writes *block* rules if it is dismissed.
It closes the running copy before replacing it, because run-on-startup means there always is one.

**The portable `.exe` is one file and must stay one file.**
`IncludeNativeLibrariesForSelfExtract` in the `.csproj` is what makes that true.
Without it a single-file publish leaves WPF's five native libraries loose in the publish
directory, and since the release attaches only the `.exe`, what people downloaded from v0.1.0 to
v0.6.0 was missing them: it started, listened on 45001, advertised over Bluetooth, and threw
`DllNotFoundException` at the first window. No window, no tray icon, no console - and the process
stayed alive, so the second double-click was swallowed by the single-instance mutex.
`build.ps1` asserts the publish is exactly one file for that reason.

## `ClipboardWorker` owns one thread, not one per payload

Every Win32 clipboard interaction happens on a **single dedicated STA thread**.
Each received payload used to spawn a fresh one and block the transport while it ran.
Clipboard calls throw `ExternalException` whenever another process holds the clipboard lock, and
they block for seconds, so this must never be the message pump.

## Timings that differ from the desktop head

| | Windows | Desktop |
|---|---|---|
| Dial interval | 20s | 15s |
| BLE scan interval | 30s | 30s |
| Wi-Fi wake timeout | 15s | 15s |

See [[timings]].

## Gotchas specific to this head

**WinForms is referenced for the tray icon**, so `Brush`, `MessageBox` and `Timer` are all
ambiguous in WPF code and need qualifying.

**`HorizontalAlignment` inside a `FrameworkElement` resolves to the property, not the enum.**
Qualify it as `System.Windows.HorizontalAlignment.Center`.

**Reassigning colour keys does not repaint anything.**
Swap the whole merged dictionary instead.

**Toasts need an AppUserModelID registered under `HKCU`**, which is what makes them possible for
the portable build, which has no Start Menu shortcut to carry one.
Banners will not appear while Do Not Disturb is on, which is a setting rather than a bug.

## See also

[[android-client]] · [[desktop-shell]] · [[link-state]] · [[ble-link-arbitration]]
