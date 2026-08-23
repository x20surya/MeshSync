---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Identity/PeerRegistry.cs
  - src/CoreLib/Identity/PeerSecurity.cs
updated: 2026-08-23
---

# Peer registry

The list of devices this one has paired with, their last known addresses, and the [[mesh-name]].
Persisted as `peers.json` beside `device.key`.

## Where it lives

- `src/CoreLib/Identity/PeerRegistry.cs`
- `src/CoreLib/Identity/PeerSecurity.cs` owns the [[pairing]] window and the confirmation queue.

Deleting `peers.json` forces a re-pair.
On Android, `adb install -r` preserves it and `adb shell pm clear` wipes it.

## What it decides

**A listener refuses any peer it has not paired with.**
That check is this file.
A stranger gets no further than a queue.

It is also consulted on every payload, not only at connection time, because a session holds its
own key and would otherwise keep working after a device was forgotten.
See [[session-keys]].

## What is designed and not surfaced

`PeersToIntroduceTo` exists so a new device can learn the whole set from one scan instead of one
scan per pair.
Nothing consumes it.
It needs a confirmation step in the UI before it should be wired up, which is why it has sat
there rather than being deleted.

## See also

[[device-identity]] · [[pairing]] · [[session-keys]] · [[address-handover]] · [[mesh-name]]
