---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/TcpTransportConnection.cs
  - src/CoreLib/Transport/BleProtocol.cs
updated: 2026-08-24
---

# Wire formats

Two transports with opposite problems, so two framings.

## TCP

A byte stream with **no message boundaries**, so frames carry their own:

```
[magic u16][version u8][kind u8][length u32][payload]
```

The magic detects a desynchronised stream instead of acting on it.
The length is bounds-checked before a byte is allocated.

The hello carries a device name, a public key and the [[mesh-name]], the last two length-prefixed
and the mesh name optional so an older peer still parses it.

**The original framing read the length prefix with a single `ReadAsync` and trusted it**, which
desynchronises the stream permanently the first time a read returns short.

## Bluetooth

The inverse problem: a GATT write is already a message and arrives in order, but is hard-capped at
512 octets whatever the MTU claims.
So frames are told apart **by length alone**, except for one.

| Length | Frame |
|---|---|
| 2 bytes | Control: ping, pong, wake Wi-Fi |
| 4 bytes | Chunk receipt |
| 5+ bytes | Data chunk: `[msgId u8][seq u16][total u16][payload]` |
| leading `0x00` | Extended control, currently the identity exchange |

**Length alone stops discriminating as soon as one frame is variable.**
An identity exchange is about 120 bytes, squarely in the data range.
So it borrows the one value a data chunk's message id can never take: zero.

**A data chunk's message id must never be zero.**
Use `BleProtocol.NextMessageId`.
The counter used to wrap straight through zero after 255 messages, so that had to be fixed before
the marker meant anything, and `AGENTS.md` now carries it as a rule.

## The rule about control frames

**Bluetooth control frames are not encrypted.**
Anything that acts on one must first check the peer has identified itself, or it is reachable by
anybody who knows the service UUID.
Ping is the exception, because the liveness handshake runs before the identity exchange.

This is why [[find-my-device]] is a content type rather than the two-byte control frame it
obviously should have been.

## The test trap, twice

**A hardcoded wire version in a test makes it pass for the wrong reason.**
Bumping the version broke a reassembly test, because a version mismatch drops a connection in
precisely the way the test is trying to provoke.
It happened twice.
The constant is `internal` now and the test reads it, so the copy cannot go stale a third time.

## See also

[[content-types]] · [[wifi-tier]] · [[bluetooth-tier]] · [[session-keys]]
