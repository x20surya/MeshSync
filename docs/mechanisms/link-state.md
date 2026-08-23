---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Transport/LinkState.cs
updated: 2026-08-24
---

# Link state

The one shared answer to "is anything reachable, and over which link".
Wi-Fi wins when both are up.

Every head calls this rather than asking a transport directly, and every screen reads it rather
than computing its own.

## Where it lives

`src/CoreLib/Transport/LinkState.cs`.

It is one of the three things that moved out of `src/WinDaemon` into `CoreLib` in v0.2.3, with
[[transport-preference]] and [[ble-link-arbitration]], because the Linux head had reimplemented
each of them differently or not at all and **every one of those divergences was a bug**.

## It is a view now, not a source

`LinkState` is still an *aggregate* - "is anything reachable, and over what" - and that is the
right question for a tray icon and a status line.

What changed in v0.4 is where it gets its answer. Two transports used to write into it directly,
so it was the only thing that knew, and it could hold exactly one connected peer name. Every head
now derives it from [[peer-link]], and anything that needs the per-peer answer asks the fabric
instead.

| Question | Ask |
|---|---|
| Is anything reachable, and over what | `LinkState` |
| Is *this peer* reachable, and over what | `MeshFabric.LinkTo(fingerprint)` |
| Why is this peer not reachable | `MeshHealth` |

**The gap this note used to record is closed.** Windows and Android answered per app, so each
could mark only one device connected and guessed which by comparing names - which broke outright
with two devices called the same thing. Both device lists ask per peer now.

## The rule this note exists to carry

**Anything one head needs and another already has belongs in `CoreLib`, not written a second
time.**
A platform should be wiring and storage - a registry key here, a file there - and never its own
copy of a rule.

## See also

[[peer-link]] · [[ble-link-arbitration]] · [[transport-preference]] · [[wifi-tier]] · [[bluetooth-tier]]
