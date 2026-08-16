# Universal Password & Clipboard Sync (Local-First)

A completely local, Zero-Trust universal clipboard and password syncing tool. Inspired by the seamless integration of Apple's Universal Clipboard and Microsoft's "Phone Link," but built with absolute privacy in mind.

No cloud servers. No subscriptions. No third-party data harvesting.

## Features
- **True Universal Clipboard**: Copy text or photos on your Windows PC and instantly paste them on your Android phone, and vice-versa.
- **Zero-Trust Encryption**: Everything is encrypted *before* it leaves the device using `AES-256-GCM`. Keys are derived from a master password using `Argon2id`.
- **Offline First**: All data is transferred locally over Bluetooth Low Energy (BLE) or Wi-Fi Direct. You don't even need an internet connection.
- **Android Compatibility**: Uses an Accessibility Service to bypass Android 10+ background clipboard restrictions, ensuring it works on *all* Android devices, not just Samsung.

## Project Architecture
- **`CoreLib`**: The cross-platform brain. Contains the Crypto Engine (Argon2id, AES-256) and soon the CRDT (Conflict-free Replicated Data Type) logical clocks for merging conflicts.
- **`WinDaemon`**: A lightweight, invisible Windows background daemon that hooks into `Win32` clipboard APIs.
- **`AndroidClient`**: A .NET MAUI Android application containing the UI and the background Accessibility Service to monitor the Android clipboard.

## Current Status
- **Phase 1 (Foundation)**: ✅ Complete. Text clipboard capturing works natively on both platforms, along with native MediaStore screenshot interception on Android.
- **Phase 2 (Crypto Engine)**: ✅ Complete. `CoreLib` successfully encrypts and decrypts cross-platform.
- **Phase 3 & 4 (Transport Layer & Ephemeral Sync)**: ✅ Complete. TCP sockets, QR deep-link pairing, and bidirectional text/image syncing are fully operational.
- **Phase 5 (Password Vault)**: 🚧 In Progress. SQLite integration and CRDT sync mechanisms.

## Setup Instructions
Please refer to [CLAUDE.md](CLAUDE.md) for build and deployment instructions.
