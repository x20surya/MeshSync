---
type: feature
status: shipped
platforms: [windows, android, linux, macos]
tier: wifi
code:
  - src/CoreLib/Identity/PairingWindow.cs
  - src/CoreLib/Identity/PeerSecurity.cs
  - src/CoreLib/Identity/PendingPairing.cs
  - src/CoreLib/Identity/PeerRegistry.cs
updated: 2026-08-23
---

# Pairing

One QR scan, then one comparison.
Both steps are required and the second one is the one that matters.

## Where it lives

- `src/CoreLib/Identity/PairingWindow.cs` - the window during which a stranger may be queued.
- `src/CoreLib/Identity/PeerSecurity.cs` - owns the window and the confirmation queue.
- `src/CoreLib/Identity/PendingPairing.cs` - a stranger waiting for a human.
- `src/CoreLib/Identity/PeerRegistry.cs` - where an accepted peer lands.

## How it works

The QR code carries three things: an address, a public key, and the [[mesh-name]].

The scan is one-directional, which is the problem the whole design works around.
It shows one device's public key and the other scans it, so the scanner can authenticate us and we
get nothing back.
**Showing the pairing code is therefore the signal that a stranger was invited**, and that is what
the pairing window is: a bounded period during which an unknown device is queued rather than
refused outright.

Then the device being joined displays the four-group fingerprint of the device asking, and you
check it matches what that device is showing.
That second step closes the race an attacker on the same network could otherwise win by connecting
first.

## The rule

**Trust-on-first-use is not enough on its own.**
A stranger inside the pairing window is queued for a human to compare fingerprints, never trusted
outright.
`AGENTS.md` forbids adding a path that skips it.

## Findings worth knowing

**A rejected dialler briefly believes it succeeded.**
Refusal happens when the listener reads the hello, which is after the socket is already open.
Tests must assert on the durable outcome, not on what the dial returned.

**The pairing window used to be static**, which broke test isolation before it broke anything
else: one test class opening it made another class's "a stranger is refused" fail intermittently
under xUnit's parallel execution.
It belongs to `PeerSecurity` now, which is better design and fixed the tests as a side effect.

**`SetupComplete` and "is paired" used to mean the same thing and no longer do.**
Pairing lives in the peer registry, so a device can finish setup and hold no peers.

## What is still open

**Introduction is designed and not surfaced.**
`PeerRegistry.PeersToIntroduceTo` exists so a new device can learn the whole set from one scan
instead of one scan per pair.
Nothing consumes it yet, and it needs a confirmation step in the UI before it should be wired up.

## See also

[[device-identity]] · [[peer-registry]] · [[session-keys]] · [[mesh-name]]
