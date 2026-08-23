---
type: mechanism
status: shipped
platforms: [windows, android, linux]
tier: ble
code:
  - src/CoreLib/Transport/Ble/MeshBeacon.cs
  - src/CoreLib/Transport/Ble/MeshDiscovery.cs
  - src/CoreLib/Identity/PeerRegistry.cs
updated: 2026-08-24
---

# Mesh beacon

**Six bytes in the advertisement that say which mesh a device belongs to.**

Every install advertises the same service UUID, so a scan finds every Mesh Sync device in range
and not only the ones in this mesh.
Before this, "is this one of mine" could not be answered until after a connect, an MTU exchange
and a hello - by which point both devices had told each other their device name and mesh name.

## The rule this note exists to carry

**The beacon decides who to *try*, never who is let in.**

Authorisation stays exactly where it was: the peer registry, the per-connection key agreement, and
a human comparing fingerprints.
A forged or replayed beacon buys an attacker one wasted connect attempt, which is what *every*
stranger cost before.
`The_mesh_key_never_reaches_a_session_key` asserts that the key never enters a session derivation,
and it must stay that way.

This is the same reasoning commit `5be68b2` applied to the name-keyed cooldown: it decides who to
try, so a device that spoofs one gains nothing but its own exclusion.

## Where it lives

| Piece | File |
|---|---|
| Build, verify, and the advertisement budget | `src/CoreLib/Transport/Ble/MeshBeacon.cs` |
| What to advertise, and which advertisements are worth trying | `src/CoreLib/Transport/Ble/MeshDiscovery.cs` |
| The 32-byte key, minting and adoption | `src/CoreLib/Identity/PeerRegistry.cs` |
| Distribution over existing links | `SyncContent.MeshKeyOffer` (`0x0D`) |

## The bytes

```
[0]    flags   u8   bits 0-3 version (1) · bit 4 pairing open · bit 5 can also be central
[1]    epoch   u8   low 8 bits of (unixSeconds / 900)
[2..5] tag     4B   HMAC-SHA256(meshKey, "meshsync-beacon-v1" || epochLE32 || flags)[0..4]
```

The flags are mixed into the tag, so a flipped bit fails verification rather than being believed.

## Every number has a reason

| Choice | Why |
|---|---|
| **15-minute epoch** | Matched to the LE private-address rotation window. Longer would make a Mesh Sync device trackable for longer than its own MAC already is; shorter costs clock-skew tolerance for nothing. |
| **&plusmn;1 epoch accepted** | 45 minutes of total tolerance. A device more than fifteen minutes out of true is a real failure and should be diagnosable, not silently half-working. |
| **4-byte tag** | All that fits. One in 4.3&times;10<sup>9</sup> accidental matches, and a match only earns a connect attempt that then has to survive the registry. Truncation is safe precisely because this is not a credential. |
| **Company ID `0xFFFF`** | The SIG's reserved/test identifier, in `MeshBeacon.CompanyId` so it can be swapped without a protocol change. Noted in [SECURITY.md](../../SECURITY.md). |
| **No local name** | A machine name in an advertisement is readable by anyone in the room, which is the leak this exists to close. There is also no room for one. |

## The advertisement budget is exact

```
 3   Flags
18   128-bit service UUID          ← kept, so every platform's existing scan filter still works
10   Manufacturer data 0xFFFF + 6 bytes
──
31   the legacy limit, exactly
```

`The_advertisement_fits_in_the_legacy_limit` is a test rather than a comment, because any future
field breaks discovery on the strictest stack silently.

## It is a ranking, not a gate

**This is the part that is easy to get wrong.**

Not every stack gives up room for a beacon.
Android and BlueZ let a caller put manufacturer data beside the service UUID; a Windows GATT
service provider advertises what it likes and has no room for it.

If a missing beacon meant "not ours", one platform failing to publish one would **partition the
mesh** - a far worse failure than the one the beacon fixes.

| Match | Meaning | What the scanner does |
|---|---|---|
| `Ours` | The beacon verified | Try first |
| `Unknown` | No beacon at all | Try after, exactly as every build before this did |
| `Foreign` | A beacon that is present and does not verify | Never connect |

Only the third case is refused, and it is the only one that costs nothing to refuse.

## The key reaches a mesh without a re-pair

Minted on the first run that has peers and no key - **not** on a fresh install, because a beacon
for a mesh of one is pointless and would be replaced by the inviter's anyway.
The first pairing is the moment a device becomes a mesh.

It travels as content type `MeshKeyOffer` over the links that already exist, so it rides the
ordinary authenticated path and only a paired device can offer one.

**Lowest key wins**, compared as 32 unsigned bytes.
Deterministic, with no timestamps and no coordinator, so two halves that minted separately
converge in one exchange rather than ping-ponging the way every simple rule for the
[[mesh-name]] does.
A device that adopts a new key re-advertises within one epoch, and tells every other peer it can
reach - or a mesh of three converges only as far as the two that happened to meet first.

Verified on this machine: two daemons, both minted, the lower won, one exchange.

**A paired device could push an all-zero key.** True, and it changes nothing: a paired device is
already trusted with the clipboard, the notifications and the files, and the key affects who this
mesh *looks for*, never who it lets in.

## Pairing has a beacon of its own

A joining device has no mesh key, so the **inviting** device - the one showing the code, the one
whose pairing window is open - advertises a tag derived from the pairing secret already in the
`meshsync://` payload.
The joiner computes the same tag from the code it scanned and finds exactly that device.

A joiner that knows which device it wants treats every other pairing beacon as `Foreign`, so a
second pairing screen open in the same room is told apart rather than connected to.

That is what lets two devices pair with no network at all.

## See also

[[ble-link-arbitration]] · [[bluetooth-tier]] · [[peer-link]] · [[pairing]] · [[protocol-ble]]
