---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Identity/PeerRegistry.cs
  - src/CoreLib/Identity/PeerSecurity.cs
updated: 2026-08-24
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

## Forgetting where a peer was

`ForgetAddress` clears `LastAddress` and leaves the peer paired.
It is for the one case where the stored address is not merely old but provably wrong: a dial to it
was answered by a different paired device, which happens whenever a DHCP lease is reused.
Clearing rather than overwriting is deliberate - the device has just learned where the peer is
*not*, and nothing about where it is.
The peer supplies a real address the next time it connects or announces one, so the registry heals
without a re-pair.
See [[wifi-tier]].

## What is designed and not surfaced

`PeersToIntroduceTo` exists so a new device can learn the whole set from one scan instead of one
scan per pair.
Nothing consumes it.
It needs a confirmation step in the UI before it should be wired up, which is why it has sat
there rather than being deleted.

## See also

[[device-identity]] · [[pairing]] · [[session-keys]] · [[address-handover]] · [[mesh-name]]
