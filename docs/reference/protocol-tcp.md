---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: wifi
code:
  - src/CoreLib/Transport/TcpTransportConnection.cs
  - src/CoreLib/Transport/TcpAcceptor.cs
updated: 2026-08-24
---

# TCP wire protocol

Everything on this page is read from `src/CoreLib/Transport/TcpTransportConnection.cs`.
Port **45001** (`DefaultPort`), all integers **little endian**.

## Frame

```
[magic u16 = 0x4D53 "MS"][version u8][kind u8][length u32][payload...]
```

Header is 8 bytes (`HeaderSize`).

| Field | Value |
|---|---|
| `magic` | `0x4D53`. Detects a desynchronised stream instead of acting on it |
| `version` | **4** (`ProtocolVersion`) |
| `kind` | `0` data, `1` ping, `2` pong, `3` hello |
| `length` | Bounds-checked against `MaxPayloadBytes` **before a byte is allocated** |

`MaxPayloadBytes` is **32 MB** (`32 * 1024 * 1024`).
A negative or oversized length drops the connection rather than allocating.

A frame is built into **one buffer and written once**.
Concurrent senders - a clipboard copy landing mid-screenshot - previously interleaved header and
body and desynchronised the stream permanently.

### The version byte's history

| Version | Change |
|---|---|
| 1 | Name only |
| 2 | Hello grew a static public key |
| 3 | Hello grew an ephemeral key, for forward secrecy |
| 4 | Hello grew a capability byte, so role arbitration stops assuming |

There is no mixed-version mesh at 3 or above: a peer that cannot offer an ephemeral key cannot
agree a session key at all, so there is nothing to negotiate down to.
Version 4 changes nothing about that - the byte is a trailing optional field and a version 3 peer
simply reads as "both halves", which is the assumption every call site made unconditionally
before it existed.

`ProtocolVersion` is `internal`, not `private`, **specifically so the tests read it from here**.
A copy in a test file goes stale the moment it is bumped, and a version mismatch drops a
connection in exactly the way most of those tests are trying to provoke, so they carry on passing
for the wrong reason. This has happened twice.

## Hello payload (kind 3)

```
[nameLen u8][name][keyLen u16][static key][meshLen u8][mesh][ephLen u16][ephemeral][caps u8]
```

All strings UTF-8. Trailing fields are optional on parse, so a shorter payload still reads.

`caps` is a `BleCapability`: bit 0 central, bit 1 peripheral. Unknown bits are masked off, so a
newer peer can add one without breaking this build.

**The socket hello is the half of the capability exchange that matters.** A Linux box that cannot
advertise says so over Wi-Fi, long before the two devices ever meet on the radio - so the phone
knows to advertise for it rather than both of them scanning. See [[ble-role-negotiation]].

| Field | Cap |
|---|---|
| name | `MaxDeviceNameBytes` = 128 |
| static key | `MaxPublicKeyBytes` = 512 (a base64 P-256 SPKI is about 120) |
| mesh name | 128 |
| ephemeral key | 512 |

**Both ends send a hello unprompted the moment a socket exists**, so the two ephemeral keys are
in flight immediately and neither side has to ask.
That is what keeps the key agreement at **zero extra round trips**.

A key longer than `MaxPublicKeyBytes` is replaced with empty rather than parsed.
A second hello on one connection replaces the session key by interlocked exchange and **logs**,
disposing the first, because there is no legitimate reason for one and silently leaking a key
would be worse.

## Timings

| Constant | Value | What it does |
|---|---|---|
| `HelloTimeout` | 10s | An unidentified socket is dropped |
| `HeartbeatInterval` | 30s | Ping cadence |
| `PeerTimeout` | 90s | Silence beyond this is a dead link |
| Connect timeout | 5s | Bounded in `WiFiRouteProvider`, not here |

**Do not shorten the heartbeat without reading the comment on it.**
It was 10s/30s, chosen for fast drop detection before anything weighed the cost.
An idle TCP socket is free; a heartbeat is not, because every one pulls the Wi-Fi chip out of
power save. For comparison the push service every app on the phone shares heartbeats about every
15 minutes, and most of that is holding a NAT mapping open across the internet, which does not
apply between two devices on one subnet.
[[bluetooth-tier]] also carries presence and notices a vanished peer in 24s regardless.

TCP keepalive is set as well (15s idle, 5s interval, 3 retries) but is **advisory**: not every
platform exposes every knob, so the application heartbeat is the real safety net.

## The hello deadline is a security control, not a tidiness one

Without `EnforceHelloAsync`, refusing unknown peers would be trivially bypassed by simply not
sending a hello.
The socket would stay open, every payload would fail to decrypt, and the link would sit there
looking connected for ever.

## Reading is exact

`ReadExactAsync` loops until it has the bytes asked for.
A single `ReadAsync` may return fewer, and the original framing read the 4-byte length prefix from
a partial read and permanently desynchronised every subsequent frame.

## What `IsConnected` actually means

Three conditions, and the third is the one that is easy to get wrong:

1. A session exists and is not closed.
2. Deliberately **not** `TcpClient.Connected`, which merely reports the last I/O and stays true on
   a half-open socket.
3. When there is a registry behind the transport, **a key has actually been agreed**.

That third clause is the "key ready" gate.
A socket used to be usable the moment it opened, because the key came from the peer's identity
alone. An ephemeral agreement is not complete until both hellos have crossed, so reporting the
link connected any earlier lets a caller hand it a payload there is no key to seal.

## Addresses

The listener binds `IPAddress.Any` in dual-stack mode, so a peer that connected over IPv4 is
reported as `::ffff:192.168.0.103`.
That parses as an address, reads perfectly well in a log, and **can never be dialled back**.

It is unwrapped in two places, and both are needed: where addresses are recorded
(`Session.RemoteAddress`) and where they are dialled (`MeshLinks.SplitAddress`), so a value stored
by an earlier build self-heals rather than timing out for ever.

## See also

[[wifi-tier]] · [[protocol-payloads]] · [[protocol-ble]] · [[crypto]] · [[wire-formats]]
