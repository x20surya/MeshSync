---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/SyncContent.cs
updated: 2026-08-24
---

# Content types

One byte inside every encrypted payload, saying what the bytes after it are.
Everything rides this, so **a new feature inherits authentication rather than arranging its own**.

## The table

| Byte | Name | Carries | Tier |
|---|---|---|---|
| `0x00` | `Text` | UTF-8 clipboard text | Either |
| `0x01` | `Image` | JPEG, downscaled and re-encoded | Wi-Fi |
| `0x02` | `Address` | this device's current LAN address | Either |
| `0x03` | `FileOffer` | id, name, size, SHA-256 | Wi-Fi |
| `0x04` | `FileAck` | whether the receiver wants it | Wi-Fi |
| `0x05` | `FileChunk` | id, offset, bytes | Wi-Fi |
| `0x06` | `Ring` | one byte, non-zero starts | Either |
| `0x07` | `Notification` | a mirrored notification | Either |
| `0x08` | `NotificationDismiss` | the key alone | Either |
| `0x09` | `BrowseRequest` | folder id and relative path | Wi-Fi |
| `0x0A` | `BrowseReply` | a listing | Wi-Fi |
| `0x0B` | `FetchRequest` | ask for one file | Wi-Fi |
| `0x0C` | `NotificationReply` | a key and reply text | Either |
| `0x0D` | `MeshKeyOffer` | the 32-byte mesh discovery key | Either |

`MeshKeyOffer` is how [[mesh-beacon]] reaches a mesh without a re-pair.
It rides the ordinary authenticated path, so only a paired device can offer one - and it is
**not a credential**: it decides which advertisements are worth connecting to and nothing else.

Which tier carries what is arithmetic, not preference.
At about 6.7 KB/s Bluetooth carries anything small and nothing large.

## The rule

**A new content type goes in `SyncContent` and nowhere else, and both apps must dispatch on it.**

`SyncContentTests` fails until it is declared there, which is the reminder to go and handle it in
both.
A collision would route a file chunk into the clipboard and look like nothing more than an odd log
line.

Both apps carried their own private copies of these constants once, which is exactly the
duplication that lets two sides of a protocol drift apart silently.

## Why some of these are content types and not control frames

Two of them look like they should have been Bluetooth control frames and are deliberately not.

**`Ring`** because control frames ride outside the encrypted path, so anything that knew the
service UUID could have made a phone shriek from across the street.

**`Address`** because an address is exactly the sort of thing that must not be accepted from a
stranger, or it becomes an invitation to redirect the next connection.
It also could not have been a control frame anyway: Bluetooth tells its frames apart by length,
so a variable-length address would collide with clipboard content.
See [[address-handover]].

## See also

[[wire-formats]] · [[session-keys]] · [[file-transfer]] · [[remote-browse]]
