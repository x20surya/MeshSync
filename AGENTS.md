# Agent Instructions

This document outlines the architecture and goals for the Local-First Password & Clipboard Sync project. It serves as a guide for any AI agent working on this codebase.

## The Goal
- **Absolute privacy**: No cloud hosting or third-party servers.
- **Seamless sync**: Laptop and phone stay in sync automatically when in proximity.
- **Universal Clipboard**: Copy text or photos on one device, paste on the other (Windows Phone Link style).

## Implementation Approach

1. **Connectivity & Transport Layer**. Two tiers, both direct device to device.
   - **Wi-Fi (preferred)**: length-prefixed TCP on port 45001 over the local network.
     Carries text and images. The computer listens, the phone connects.
   - **Bluetooth LE (fallback)**: used when no Wi-Fi or Ethernet transport exists at all.
     Carries text only, at roughly 6.7 KB/s. The computer is the GATT server and the phone the
     client, mirroring the TCP roles. The phone finds it by scanning for the service UUID, so
     pairing carries no Bluetooth address.
   - Wi-Fi Direct was in the original plan and is not used. Plain TCP over whatever local network
     already exists turned out simpler and needs no pairing of its own.
2. **Encryption & Key Exchange**
   - Transport encryption via AES-256-GCM. The encrypted payload is byte-identical on both
     transports, so crypto, echo suppression and the activity log are transport-agnostic.
   - Local vault encryption at rest via AES-256-GCM derived from a master password (Argon2id).
   - **The zero-trust pairing is not implemented.** See Known gaps below.
3. **Universal Clipboard Service**
   - Windows: background daemon using Win32 clipboard APIs (`src/WinDaemon`). All clipboard access
     happens on one dedicated STA thread, never on the message pump, because those calls block for
     seconds whenever another process holds the clipboard lock.
   - Android: an accessibility service monitors clipboard text changes (`src/AndroidClient`).
   - Android images: a `ContentObserver` on `MediaStore` intercepts screenshots without touching
     the clipboard. Wait for the row to stop being pending before reading it, or the capture is
     read half-written.
   - Images are downscaled and re-encoded as JPEG before transmission.
   - Two further entry points avoid the clipboard entirely: `PROCESS_TEXT` puts "Send to PC" in the
     text selection toolbar, and a share target accepts text and images from any app.

## Current Project Status

See [HANDOFF.md](HANDOFF.md) for the detailed state, the non-obvious findings behind the current
design, and the testing gotchas. Read it before changing the transports.

- **Phase 1 (Foundation)**: COMPLETED. `WinDaemon` monitors the Win32 clipboard. `AndroidClient`
  monitors clipboard text and uses a `MediaStore` observer for native screenshot beaming.
- **Phase 2 (Crypto Engine)**: COMPLETED. `CoreLib` derives keys via `Argon2id` and encrypts
  payloads using `AES-256-GCM`.
- **Phase 3 & 4 (Transport & Ephemeral Sync)**: COMPLETED and reworked. TCP framing carries a
  magic number, a protocol version and a bounds-checked length, with a heartbeat that surfaces
  half-open links. Both apps were rebuilt: the daemon is WPF with a sidebar, the phone has a
  setup wizard and a dashboard.
- **Phase 4b (Bluetooth tier)**: COMPLETED. Text syncs with no network at all. Payloads are
  fragmented over the negotiated MTU, chunks are acknowledged in our own protocol, and the phone
  falls back automatically when Wi-Fi cannot reach the computer. Images stay on Wi-Fi.
- **Phase 5 (Password Vault)**: PENDING. Next step is SQLite storage and CRDT merging.

### Known gaps

- **Pairing is not authenticated.** Both sides derive from a hardcoded password and salt, so every
  install shares one key and the listener accepts any LAN connection. The `TrustManager` keypair is
  displayed, scanned and stored but never used. The repository is public.
- **No Android foreground service.** The connection is held by the accessibility service, which
  Doze will eventually kill.
- **Discovery is not wired to pairing.** A DHCP lease change breaks pairing until the QR is rescanned.

## Risks & Conflict Resolution
- **Dual Architecture**: The project has two distinct sync engines:
  1. **Clipboard Sync (Ephemeral)**: The user explicitly requested NOT to save or merge clipboard histories. Do not implement SQLite or CRDTs for clipboards. When a copy event occurs, encrypt it and broadcast it instantly to connected devices. If no devices are in range, it is dropped.
  2. **Password Vault (Persistent)**: Passwords and secure notes require permanent storage. Implement SQLite and CRDTs (Conflict-free Replicated Data Types) for deterministic merging of password vault changes based on logical clocks.
- **Battery Drain**: Use BLE advertising only on phone unlock or clipboard change events, instead of constant polling.

## Rules for Agents
- Do not introduce cloud dependencies or external third-party servers.
- Prioritize offline-first and E2E encryption best practices.
- Consider battery optimization and bandwidth limitations for all local sync features.
- If editing the Android project, remember that it targets `net10.0-android` and requires `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>` for debug builds deployed via CLI.
- Never use `adb shell pm clear` on the Android client. It revokes the accessibility grant, which
  only the user can restore by hand, and every reading taken before they do is meaningless. Use
  `adb install -r` instead, and check the grant survived.
- Kill `WinDaemon` before building or the build fails on a locked `CoreLib.dll`. It relaunches on
  its own because run-on-startup is enabled.
- Diagnostics go through `CoreLib.Diagnostics.Log`, never `Console.WriteLine`. The daemon is a
  `WinExe` with no console attached, so anything written there is discarded.
- Both transports carry the same encrypted payload, so crypto, echo suppression and the activity
  log stay transport-agnostic. Keep it that way.
