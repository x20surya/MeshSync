---
type: reference
status: shipped
platforms: [windows, android, linux]
tier: ble
code:
  - src/CoreLib/Transport/BleProtocol.cs
  - src/CoreLib/Transport/BleFragmenter.cs
updated: 2026-08-24
---

# Bluetooth wire protocol

Read from `src/CoreLib/Transport/BleProtocol.cs` and `BleFragmenter.cs`.

## UUIDs

| | UUID |
|---|---|
| Service | `7f3e2a10-9c41-4b8e-a2d7-5e1f0b6c8d90` |
| Inbox (central writes) | `7f3e2a11-9c41-4b8e-a2d7-5e1f0b6c8d90` |
| Outbox (peripheral notifies) | `7f3e2a12-9c41-4b8e-a2d7-5e1f0b6c8d90` |

Two characteristics, because one cannot carry both directions: a GATT client writes and a GATT
server notifies.

Both are `GattProtectionLevel.Plain` and peers are found by service UUID, so **no OS-level bonding
is used or needed** and "forget this device" in Bluetooth settings changes nothing.

**Every install shares this service UUID.** That is what makes a scan find other people's meshes,
and it is the whole reason [[ble-link-arbitration]] has a cooldown.

Since v0.4 the advertisement also carries six bytes of [[mesh-beacon]] in a manufacturer-data
section, where the platform has room for one, so a scanner can tell its own mesh from somebody
else's *before* it connects.

## Frame discrimination

Frames are told apart **by length alone**, except one, and the order of checks matters.

| Test | Frame | Layout |
|---|---|---|
| first byte `0x00` | Extended control | `[0x00][kind][payload...]` |
| length 2, first byte `0xC7` | Control | `[0xC7][kind]` |
| length 4, first byte `0xAC` | Chunk receipt | `[0xAC][msgId][seq lo][seq hi]` |
| length 5+ | Data chunk | `[msgId u8][seq u16][total u16][payload]` |

`ExtendedMarker` (`0x00`) **must be checked before the receipt and the reassembler, not after.**

Control kinds: `0x01` ping, `0x02` pong, `0x03` wake Wi-Fi.
Extended kinds: `0x01` hello.

### Why the extended marker works

Length alone stops discriminating as soon as one frame is variable, and an identity exchange is
about 120 bytes, squarely in the data range.
So it borrows the one value a data chunk's first byte can never be.

**`BleProtocol.NextMessageId` is what keeps that promise.**
The counter used to wrap straight through zero after 255 messages, which would have made one
clipboard item in every 256 parse as an identity exchange.

```csharp
public static byte NextMessageId(ref byte counter)
{
    do { counter = unchecked((byte)(counter + 1)); } while (counter == ExtendedMarker);
    return counter;
}
```

## Sizes

| Constant | Value | Note |
|---|---|---|
| `PreferredMtu` | 517 | The ceiling to ask for |
| `MaxAttributeValueBytes` | **512** | Spec cap, whatever the MTU says |
| `MinimumMtuPayload` | 20 | 23-byte MTU less the 3-byte ATT header |
| `MaxPayloadBytes` | 64 KB | This is the small-payload tier |
| `BleFragmenter.HeaderSize` | 5 | |
| `MaxChunks` | 65535 | A u16 sequence |
| `MaxDeviceNameBytes` | 64 | In a hello |

`UsablePayload(mtu)` is `clamp(mtu - 3, 20, 512)`.

**Windows reports `MaxNotificationSize` as MTU minus the ATT header, which is 514 on a 517 MTU,
and that is two bytes optimistic.** Bisected on device: a 512-byte chunk arrives, a 513-byte chunk
never does, with no error on either side.

## Chunk receipts

`AckMarker` `0xAC`, 4 bytes, `AckTimeout` 5 seconds.

The server needs to know a chunk landed before sending the next, because **Windows keeps only one
outstanding notification per characteristic** and a second overwrites the first in flight.
A 128-chunk message arrived as its last chunk alone.

Indications are acknowledged at the ATT layer and would solve this in principle.
On this stack they went unconfirmed and Windows tore the link down with `GATT status 19`.
Acknowledging in our own protocol works on both platforms and is not at the mercy of either
stack's quirks.

## Hello payload

Newline-separated, which a base64 key can never contain, so fields are read positionally and a
shorter payload still parses:

```
publicKey \n deviceName \n meshName \n ephemeralKey \n capability
```

The fifth field arrived with wire version 4 and is a `BleCapability` in decimal. A payload that
stops at four reads as "both halves", which is what every call site assumed unconditionally
before it existed.

A hello is written **in one go rather than through `BleFragmenter`**, because an extended frame is
marked by a leading zero and a fragmented chunk starts with its message id instead, so the two
shapes cannot be mixed.
The senders check the size and log rather than letting an oversized hello vanish silently.

The device name matters more here than it looks.
Wi-Fi carries it in its own hello, so a Wi-Fi pair has a name to show.
Bluetooth carried identity but no name, which left a Bluetooth-only pair with nothing to call each
other and a notification reading "your devices" for ever.

An **empty ephemeral key means no session can be agreed**, which the caller treats as a refusal
rather than as a peer to fall back for.

## Fragmentation

```
[messageId u8][sequence u16][totalChunks u16][payload...]
```

Little endian. The header is deliberately tiny because on an unnegotiated 23-byte MTU there are
only 20 usable bytes to spend.

An **empty payload still gets one chunk**, otherwise the peer never learns the message existed.

`BleReassembler` is one instance per peer connection, never shared.
It discards rather than throws, because a dropped BLE packet must not take the connection down.
It refuses: a runt chunk, zero total chunks, joining a message part-way (`sequence != 0` on a new
message), a changed chunk count mid-message, a sequence gap (GATT preserves order, so a gap is a
lost write rather than reordering), and a projected size over `maxMessageBytes` (default 4 MB).
A partial message is discarded after 30 seconds of silence so a peer that walks out of range
cannot pin its buffer.

## Timings

| Constant | Value |
|---|---|
| `HeartbeatInterval` | 8s |
| `PeerTimeout` | 24s |
| `AckTimeout` | 5s |

Note these are much tighter than [[protocol-tcp]]'s 30s/90s.
Bluetooth is the standing link, so it is the one that carries presence.

## The rule about control frames

**Control frames ride outside the encrypted payload.**
Anything that acts on one must first check the peer has identified itself, or it is reachable by
anybody who knows the service UUID.

Ping is the exception, because the liveness handshake runs before the identity exchange.

This is why [[find-my-device]] is a content type and not the two-byte control frame it obviously
should have been: anything that knew the service UUID could otherwise have made a phone shriek
from across the street.

## `ControlWakeWiFi` exists because a device cannot dial on demand

Either end may be the listener, so a device holding an image with only Bluetooth up cannot simply
open a socket to its peer.
It sends `0x03` over the link that is already open and the peer raises Wi-Fi in response.

## See also

[[mesh-beacon]] · [[bluetooth-tier]] · [[ble-link-arbitration]] · [[ble-role-negotiation]] · [[protocol-payloads]] · [[protocol-tcp]]
