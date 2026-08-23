---
type: feature
status: shipped
platforms: [windows, android, linux, macos]
tier: wifi
code:
  - src/CoreLib/Transport/FileTransferService.cs
  - src/CoreLib/Transport/FileTransferSender.cs
  - src/CoreLib/Transport/FileTransferReceiver.cs
  - src/CoreLib/Transport/FileTransferProtocol.cs
updated: 2026-08-23
---

# File transfer

Send a file from the share sheet, the tray, or by dropping it on the window.
Unbounded size, streamed, Wi-Fi only.

## Where it lives

Almost all of it is in `CoreLib`, which is why every head has it.

- `src/CoreLib/Transport/FileTransferService.cs` orchestrates.
- `FileTransferSender.cs` and `FileTransferReceiver.cs` are the two ends.
- `FileTransferProtocol.cs` is the framing.
- Heads supply only the file picker and the place to write.

## How it works

A file is the one thing that is not a single payload.
It is an offer, a decision, and a stream of 1 MB chunks written straight to disk as they arrive.

Three content types carry it: `FileOffer` (0x03), `FileAck` (0x04) and `FileChunk` (0x05).
See [[content-types]].

**The SHA-256 is in the offer, not at the end.**
The receiver therefore knows what it is checking for before the first byte, which is what makes a
truncated transfer a failure rather than a file that looks complete and is not.

## Why it is Wi-Fi only

Arithmetic, not preference.
Bluetooth carries roughly 6.7 KB/s, which is fine for text and hopeless for a video.
A device holding a file sends a `ControlWakeWiFi` frame over the Bluetooth link that is already
open and its peer raises Wi-Fi in response, because either end may be the listener and a device
cannot simply dial its peer on demand.
See [[wifi-tier]].

## Platform notes

**Android receiving had a hole worth remembering.**
A file written through MediaStore has no path the app is allowed to know, so the content URI has
to be kept at the moment of writing or the file arrives and cannot be opened from the app that
received it.

**Filenames are stripped of every path part on arrival as well as on the way out.**
Nothing that arrives from a peer decides where bytes land without being parsed, even inside an
authenticated payload from a paired device.

## What is still open

- **A transfer does not resume.**
  A failure restarts it.
  This is called out explicitly in `AGENTS.md` so it does not get half-built.
- `SizeBytes` was an `int` once, which would have reported a video as a negative size.
  It is not any more, and the note is here so it does not come back.

## Verified

An 81.8 MB APK crossed the mesh in under two seconds over the phone's own hotspot, and a 12000
byte file arrived with an identical SHA-256 on both sides.
See `HANDOFF.md` under "Verified on hardware after the security and features work".

## See also

[[remote-browse]] · [[content-types]] · [[wifi-tier]] · [[wire-formats]]
