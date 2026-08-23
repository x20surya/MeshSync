---
type: feature
status: partial
platforms: [windows, linux, macos]
tier: n/a
code:
  - src/CoreLib/Transport/TransportPreference.cs
  - src/WinDaemon/RegistryTransportPreferenceStore.cs
  - src/DesktopCore/Platform/FileTransportPreferenceStore.cs
updated: 2026-08-24
---

# Transport preference

Choose Wi-Fi and Bluetooth, Wi-Fi only, or Bluetooth only.
It takes effect at once, with no restart, and is remembered between runs.

## Where it lives

The rule is in `CoreLib` and each platform supplies only storage.
This is the shape `AGENTS.md` demands of anything shared, and this feature is one of the three
that was moved to enforce it.

| Piece | File |
|---|---|
| The rule and `ITransportPreferenceStore` | `src/CoreLib/Transport/TransportPreference.cs` |
| Windows storage | `src/WinDaemon/RegistryTransportPreferenceStore.cs` (registry) |
| Linux and macOS storage | `src/DesktopCore/Platform/FileTransportPreferenceStore.cs` (file) |
| Headless control | `src/LinuxDaemon` - the `transport` command |

## Which heads have it

Windows and the desktop head both offer all three modes in Settings.
`LinuxDaemon` exposes the same thing as a `transport` command that shows or sets it.
**Android does not have it**, and that is the gap.

## Why it is in CoreLib

It was Windows-only code that the Linux head had reimplemented differently or not at all, and
every one of those divergences was a bug.
`LinkState`, `TransportSettings` and `BleLinkArbiter` all moved for the same reason in v0.2.3.
A platform should be wiring and storage - a registry key here, a file there - and never its own
copy of a rule.

## See also

[[link-state]] · [[ble-link-arbitration]] · [[wifi-tier]] · [[bluetooth-tier]]
