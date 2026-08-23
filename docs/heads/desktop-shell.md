---
type: head
status: shipped
platforms: [linux, macos]
tier: either
code:
  - src/DesktopShell/App.axaml.cs
  - src/DesktopShell/MainWindow.axaml
updated: 2026-08-24
---

# Desktop shell

The Avalonia window and tray icon for Linux and macOS, over [[desktop-core]].
The same sidebar, palette and type scale as [[windows-daemon]], on a toolkit that builds for both
platforms from one machine.

It runs in the tray and holds the links whether or not the window is open.

```bash
dotnet run --project src/DesktopShell/DesktopShell.csproj
```

Cross-published for macOS from Linux.
The binary is a real Mach-O arm64 with the Cocoa backend in it; signing and notarising still need
a Mac, and **nothing has ever launched it**.

```bash
dotnet publish src/DesktopShell/DesktopShell.csproj -c Release -r osx-arm64 --self-contained true
```

Packaged as an AppImage, a `.deb` and a tarball by `packaging/build.sh`.
Nothing there needs root.

## What it has

Clipboard, files, find my device and notification mirroring all work, and mirrored notifications
go into the desktop's own notification centre rather than a window this app owns.
Settings offers the same three modes as Windows for [[transport-preference]].

Its device list **names the tier each device is actually on**, because [[link-state]] answers per
peer here through `Daemon.IsConnectedTo` and `IsBluetoothConnectedTo`.
Windows still answers per app.
This head is ahead on that one thing.

## Avalonia 12 is not Avalonia 11

Most of what is written about Avalonia is 11, and these are the differences that cost time:

- `AvaloniaLocator.Current` is gone.
- `TextBox.Watermark` is now `PlaceholderText`.
- The clipboard moved to an `IDataTransfer` model, with the text helpers in
  `Avalonia.Input.Platform.ClipboardExtensions`, and it is `TryGetTextAsync`, not `GetTextAsync`.
- `Bitmap.Save(string, int?)` is obsolete in favour of the `BitmapEncoderOptions` overload.
- **A window loaded from XAML needs a public parameterless constructor**, so the running device is
  handed over by a method rather than through the constructor.

**Avalonia has no native Wayland backend.**
The shell runs through XWayland - its toplevel reports as `Avalonia.X11.X11Window` - which is why
the clipboard watcher holds its own Wayland connection instead of going through the toolkit.
See [[clipboard-sync]].

## One testing gotcha

**A screenshot is evidence of what was drawn, not of what was running.**
A capture path returned before the line that starts the daemon, so the window rendered a device
with no listener and no links, and the only clue was the absence of `Listening on 45001` in the
log.

## See also

[[desktop-core]] · [[linux-daemon]] · [[windows-daemon]] · [[dbus-ipc]] · [[link-state]]
