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
   - Transport encryption via ChaCha20-Poly1305 over BLE/Wi-Fi.
   - Local vault encryption at rest via AES-256-GCM derived from a master password (Argon2id).
3. **Universal Clipboard Service**: 
   - Windows: C# or Rust background daemon using Win32 Clipboard APIs.
   - Android: Accessibility Service to monitor clipboard changes.
   - Images compressed to JPEG/WEBP before transmission.

## Risks & Conflict Resolution
- **Conflict Resolution**: Implement CRDTs (Conflict-free Replicated Data Types) for deterministic merging of vault changes based on logical clocks.
- **Battery Drain**: Use BLE advertising only on phone unlock or clipboard change events, instead of constant polling.

## Rules for Agents
- Do not introduce cloud dependencies or external third-party servers.
- Prioritize offline-first and E2E encryption best practices.
- Consider battery optimization and bandwidth limitations for all local sync features.
