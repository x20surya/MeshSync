---
type: mechanism
status: partial
platforms: [windows, android, linux]
tier: either
code:
  - src/CoreLib/Transport/Fabric/MeshFabric.cs
  - src/CoreLib/Transport/Fabric/PeerLink.cs
  - src/CoreLib/Transport/Fabric/IPeerRoute.cs
  - src/CoreLib/Transport/Fabric/RoutePolicy.cs
  - src/CoreLib/Transport/Fabric/LinkSupervisor.cs
updated: 2026-08-23
---

# Peer links and the fabric

**One object per paired device, owning every way of reaching it.**

This is the v0.4 replacement for the shape where the Wi-Fi tier held a per-peer table and the
Bluetooth tier held two nullable fields per head.
Every question about reachability is now asked of a peer rather than of the app.

## Where it lives

| Piece | File |
|---|---|
| One way of reaching one peer | `Fabric/IPeerRoute.cs` |
| Everything known about reaching one peer | `Fabric/PeerLink.cs` |
| The peer table, and links that arrive before identity | `Fabric/MeshFabric.cs` |
| Which routes this device wants, as a pure function | `Fabric/RoutePolicy.cs` |
| The reconcile loop, and the watchdog over it | `Fabric/LinkSupervisor.cs` |
| Every interval, in one record | `Fabric/RouteTimings.cs` |
| The socket route and its provider | `Fabric/WiFiRoute.cs`, `Fabric/WiFiRouteProvider.cs` |

## The state machine, and why it is load-bearing

A route moves `Idle → Wanted → Discovering → Connecting → Handshaking → Established`, with
`Draining` and `Backoff` as exits.

**There is no transition into `Established` that does not pass through a session, and
`Handshaking` has a deadline.**
That single property closes the defect where a device from somebody else's mesh connected,
answered pings - ping is answered before identity by design - failed the key agreement, and was
left holding the link while the head reported "Connected over Bluetooth".
Two of the three heads had that bug and the third did not, because each had written the rule
separately.
See [[ble-link-arbitration]] for the discovery half of the same story.

`MeshFabric` applies the same deadline to routes that have not said who they are yet, because that
is where a stranger's link actually lives: connected, answering, and belonging to no peer.

## Two links to one peer, and two links to two peers

The collision rule lives inside `PeerLink`, which makes the second case unrepresentable.

- **Two of the same kind** - both ends dialled at once - keep the one dialled by the lower
  fingerprint. Both ends compute it from values they already hold, so they converge with no round
  trip.
- **Both radio halves** - a central link and a peripheral link to one device - go through
  `BleLinkArbiter.KeepFor`. A duplicate is not cosmetic: [[echo-suppression]] is on the sending
  side, so the receiver has no defence and every clipboard item crosses twice.

The Android version guarded on "a central link exists and a peripheral link exists" without
comparing fingerprints, so with three devices it would have torn down a good link.

## The policy is a pure function

`RoutePolicy.Plan(peers, conditions, now)` returns the set of routes that should exist, the peers
owed an outbound radio link, and whether to advertise.

Two rules changed shape rather than value:

- **Wi-Fi demand is per peer.** `WiFiWantedFor(peer)` ends in
  "nothing is carrying presence *for this peer*", where the old `WiFiWanted()` ended in
  `!BleConnected` for the whole device - so a radio link to the laptop dropped the socket to the
  desktop.
- **Scanning is wanted while some peer is owed a link**, not while no link exists. Every head
  stopped scanning once one link was up, which is why the third device in a mesh was never
  reached over the radio.

Advertising is still never gated on having a link: a peer that cannot advertise depends on this
device staying findable.

## The watchdog

`LinkSupervisor` races each reconcile pass against `SupervisorWatchdog` and counts the passes it
had to abandon.

This exists because `Console.In.ReadLineAsync` is a synchronized reader whose async methods run
the blocking read inline, so an await that never yielded stopped the thread D-Bus needed and
killed the entire Bluetooth tier - while failing nothing and logging nothing.
A loop that is alive but wedged looks exactly like a loop that is working.
A timestamp and a race are the whole cost of catching that class of failure.

## Status

`CoreLib` and its tests are done, and the socket tier runs through it in a three-device loopback
test.
The radio tier, the heads and the mesh beacon are the phases after this one.

## See also

[[link-state]] · [[ble-link-arbitration]] · [[wifi-tier]] · [[bluetooth-tier]] · [[timings]]
