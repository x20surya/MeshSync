# Agent Instructions

This document outlines the architecture and goals for the Local-First Password & Clipboard Sync project. It serves as a guide for any AI agent working on this codebase.

## The Goal
- **Absolute privacy**: No cloud hosting or third-party servers.
- **Seamless sync**: Laptop and phone stay in sync automatically when in proximity.
- **Universal Clipboard**: Copy text or photos on one device, paste on the other (Windows Phone Link style).

## Implementation Approach
1. **Connectivity & Transport Layer**: 
   - Discovery via Bluetooth Low Energy (BLE).
   - Transport upgrades to Wi-Fi Direct for large payloads (e.g., photos). Small payloads (passwords) use BLE.
2. **Encryption & Key Exchange**: 
   - Zero-trust architecture. Initial pairing via QR code (sharing public keys).
   - Transport encryption via ChaCha20-Poly1305 (or AES-256-GCM) over BLE/Wi-Fi.
   - Local vault encryption at rest via AES-256-GCM derived from a master password (Argon2id).
3. **Universal Clipboard Service**: 
   - Windows: C# background daemon using Win32 Clipboard APIs (`src/WinDaemon`).
   - Android: Accessibility Service to monitor clipboard text changes (`src/AndroidClient`).
   - Android Images: A background `ContentObserver` on the `MediaStore` natively intercepts screenshots instantly without requiring clipboard manipulation.
   - Images compressed to JPEG/WEBP before transmission.

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
