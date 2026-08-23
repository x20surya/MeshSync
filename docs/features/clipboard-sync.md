---
type: feature
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/WinDaemon/ClipboardWorker.cs
  - src/DesktopCore/Clipboard/ClipboardWatcher.cs
  - src/DesktopCore/Clipboard/WaylandClipboard.cs
  - src/AndroidClient/Platforms/Android
updated: 2026-08-23
---

# Clipboard sync

Copy on one device, paste on another, with nothing to do in between.
This is the feature the whole project exists for, and everything else rides the machinery it
needed.

## Where it lives

| Head | File |
|---|---|
| Windows | `src/WinDaemon/ClipboardWorker.cs` |
| Linux and macOS | `src/DesktopCore/Clipboard/` - `ClipboardWatcher`, `ClipboardFactory`, `IClipboardBridge` |
| Wayland native path | `src/DesktopCore/Clipboard/WaylandClipboard.cs`, `WaylandTransport.cs` |
| X11 and macOS fallback | `src/DesktopCore/Clipboard/CommandLineClipboard.cs` |
| Android | `src/AndroidClient/Platforms/Android/` - the tile, `PROCESS_TEXT` and share targets |

Text and images are ordinary payloads with a one-byte tag in front, so this feature owns no wire
format of its own.
See [[content-types]].

## Which heads have it

Receiving works everywhere and has never been restricted on any platform.
Sending is where the platforms diverge completely, and the divergence is the interesting part.

**Windows** watches properly.
A message-only window receives `WM_CLIPBOARDUPDATE`, and every clipboard call happens on one
dedicated STA thread rather than on the message pump, because those calls block for seconds
whenever another process holds the clipboard lock.

**Linux and macOS** do whatever the session allows, behind `IClipboardBridge`.
On a Wayland compositor offering `ext_data_control_manager_v1` the app is *told* the selection
changed and needs nothing installed.
On X11 it falls back to `wl-clipboard`, `xclip` or `xsel`, all of which have to be polled because
none has a watch mode.
With none of those present the desktop still pairs, holds links and sends; it just cannot reach
the clipboard.

**Android sends only when you ask it to**, through three routes that each acquire focus in their
own way: the Quick Settings tile, the `PROCESS_TEXT` selection menu, and the share sheet.
Screenshots are the exception and go with no interaction at all, because a `ContentObserver` on
MediaStore sees them without touching the clipboard.

## Why it is shaped this way

**The Android ceiling is deliberate and permanent.**
Android only lets an app read the clipboard while that app is in front.
The accessibility service that used to work around this has been removed, because UPI and banking
apps in India refuse to run at all while any accessibility service is enabled - that is the route
screen-reading fraud takes, and they are right to block it.
A clipboard tool that stops you paying for things is not one worth having.
`AGENTS.md` carries this as a standing rule: never reintroduce it.

**The Wayland watcher holds its own connection rather than going through Avalonia.**
Avalonia has no native Wayland backend, so the shell runs through XWayland, and an XWayland client
cannot speak `ext_data_control_manager_v1`.
That protocol is the only way to read the clipboard in the background on Wayland.
So Avalonia's clipboard API is right for sending and for applying what arrives, and the watcher is
a component of its own.

**Images cannot be deduplicated by content hash.**
Windows decodes a received JPEG and re-encodes it on capture, so the bytes never match.
See [[echo-suppression]], which is what stops a copy bouncing between two devices for ever.

## What is still open

- **GNOME Wayland has no background clipboard access at all.**
  KWin and the wlroots compositors implement `ext-data-control`; Mutter implements neither, and
  GNOME is the default on Ubuntu, Fedora and Debian.
  A companion GNOME Shell extension is the way out and is not built.
- **The X11 fallback has compiled without ever running.**
  No helper is installed on the development machine.
- **iOS can never be a peer for this.**
  Background pasteboard reads have returned nil since iOS 9.
  An iOS companion is planned as receive-mostly for that reason.

## See also

[[echo-suppression]] · [[content-types]] · [[wifi-tier]] · [[bluetooth-tier]] · [[activity-log]]
`HANDOFF.md` under "Clipboard" and "The Linux and Mac port".
