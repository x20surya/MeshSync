---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/EchoSuppressor.cs
updated: 2026-08-23
---

# Echo suppression

Stops a clipboard item that arrived from a peer being captured and sent straight back.
`src/CoreLib/EchoSuppressor.cs`.

It is transport-agnostic and must stay that way: both tiers carry the same encrypted payload, so
crypto, echo suppression and the [[activity-log]] all sit above the transport.

## The two findings

**`IsEcho` must not consume its entry.**
Both platforms raise several clipboard notifications per copy, so consuming on the first check let
the second look like a genuine user copy.

**Images cannot be matched by content hash.**
Windows decodes a received JPEG and re-encodes it on capture, so the bytes never match.

## The limitation that matters elsewhere

**The suppressor is on the sending side.**

That is why a duplicate link is not cosmetic: if two links to the same peer are both up, the
receiver gets every copy twice and has no defence.
See [[ble-link-arbitration]], which exists because of this.

Relatedly, a device with both tiers up to one peer receives every copy twice unless Bluetooth is
skipped for peers Wi-Fi already reached.

## See also

[[clipboard-sync]] · [[ble-link-arbitration]] · [[link-state]]
