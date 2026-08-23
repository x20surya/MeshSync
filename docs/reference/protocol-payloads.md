---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/SyncContent.cs
  - src/CoreLib/Transport/FileTransferProtocol.cs
  - src/CoreLib/Transport/NotificationProtocol.cs
  - src/CoreLib/Transport/BrowseProtocol.cs
updated: 2026-08-23
---

# Payload formats

Every content type's exact body layout, read from the four protocol files.

Each payload is `[contentType u8][body]` **inside** the AES-256-GCM envelope, so every one of
these inherits authentication rather than arranging its own.
See [[crypto]] for the envelope.

## The type table

| Byte | Name | Body | Tier |
|---|---|---|---|
| `0x00` | `Text` | UTF-8 | Either |
| `0x01` | `Image` | JPEG, downscaled and re-encoded | Wi-Fi |
| `0x02` | `Address` | UTF-8 LAN address | Either |
| `0x03` | `FileOffer` | below | Wi-Fi |
| `0x04` | `FileAck` | below | Wi-Fi |
| `0x05` | `FileChunk` | below | Wi-Fi |
| `0x06` | `Ring` | 1 byte, non-zero starts | Either |
| `0x07` | `Notification` | below | Either |
| `0x08` | `NotificationDismiss` | the key, UTF-8, max 256 B | Either |
| `0x09` | `BrowseRequest` | below | Either |
| `0x0A` | `BrowseReply` | below | Wi-Fi in practice |
| `0x0B` | `FetchRequest` | same shape as `BrowseRequest` | Either |
| `0x0C` | `NotificationReply` | below. **In flight, uncommitted** | Either |

**A new content type goes in `SyncContent` and nowhere else**, and both apps must dispatch on it.
`SyncContentTests` fails until it is declared there.
A collision would route a file chunk into the clipboard and look like nothing more than an odd log
line.

## File transfer

All little endian. `src/CoreLib/Transport/FileTransferProtocol.cs`.

```
FileOffer  [transferId u32][nameLen u16][name][size i64][sha256 32B]
FileAck    [transferId u32][accepted u8]
FileChunk  [transferId u32][offset i64][data...]
```

| Constant | Value |
|---|---|
| `ChunkBytes` | 1 MB |
| `MaxNameBytes` | 255 |
| `MaxFileBytes` | 4 GB |
| `AnswerTimeout` | 30s |
| Receiver `StaleAfter` | 5 min |

**The hash is in the offer, not at the end.**
The receiver therefore knows what it is checking for before the first byte, which is what makes a
truncated transfer a failure rather than a file that looks complete.
It is compared with `CryptographicOperations.FixedTimeEquals`, because a mismatch is a security
answer and not merely a fault.

**Offsets are checked, never trusted.** A chunk whose offset is not exactly `Written` fails the
transfer, so a chunk cannot be used to write elsewhere in the file or to inflate it past what was
offered.

**A transfer id is keyed with the peer** (`{fingerprint}/{transferId}`), because an id is only
meaningful to its sender and two devices will eventually pick the same one.
Ids are never zero, and wrapping simply starts again.

**An empty file completes at the offer.** There is no chunk coming, because there is nothing to
put in one. Without that it would sit open until it went stale.

### `SafeName`

The one field in a transfer that decides where bytes land, so it is sanitised on the way out
**and again on arrival**.
The sender already doing it protects against a careless name; doing it here protects against a
sender that did not.

Strips any directory part on **both** separators (so a Windows name cannot escape on Linux or the
reverse), replaces `: * ? " < > |`, NUL and every control character with `_`, trims trailing dots
and spaces, and maps `""`, `"."` and `".."` to `received-file`.

## Notifications

Little endian. `src/CoreLib/Transport/NotificationProtocol.cs`.

```
[postedUtc i64 ms][keyLen u16][key][pkgLen u16][package][appLen u16][appName]
[titleLen u16][title][textLen u16][text][canReply u8][labelLen u16][replyLabel]
```

| Field | Cap |
|---|---|
| key, package, appName | 256 B |
| title | 256 B |
| text | 1024 B |
| replyLabel | 64 B |
| reply text | 2048 B |

Every field is capped because a notification is written by whatever app posted it, so its length
is not this project's to assume, and an uncapped one would occupy the Bluetooth link for as long
as the sender felt like.

**`canReply` and `replyLabel` are appended, not inserted**, and read back only if present.
A device on the older build reads the five fields it knows and stops, so both directions keep
working across a mixed mesh, which matters because the phone and the desktop are updated on
different days by different means.

`canReply` is **set by the sender, never guessed by the receiver**: only the phone knows whether
the notification carried a reply action, and offering a reply box for one that did not is a
message the user believes they sent.

`replyLabel` is carried rather than hardcoded to "Reply" because it is the app's own word, and on
some apps the action is not a reply at all.

```
NotificationReply  [keyLen u16][key][textLen u16][text]
```

An empty reply parses as invalid: sending one would post a blank message into somebody's
conversation, which is worse than doing nothing.

## Browse

**Big endian** here, unlike everything else. `src/CoreLib/Transport/BrowseProtocol.cs`.

```
Request  [idLen u16][folderId][pathLen u16][relativePath]

Reply    [status u8][idLen u16][folderId][pathLen u16][path][truncated u8][count u16]
         then count times:
           [nameLen u16][name][idLen u16][id][isDir u8][size i64][modified i64 ms]
```

| Status | Meaning |
|---|---|
| 0 | Ok |
| 1 | NoSuchFolder |
| 2 | NotAllowed |
| 3 | NotFound |

| Cap | Value |
|---|---|
| `MaxEntries` | 500 |
| folder id | 64 B |
| path | 1024 B |
| entry name | 512 B |
| `BrowseService.Timeout` | 20s |

An **empty `folderId` asks for the list of shared folders themselves**, which is where browsing
starts. The ids come back on those rows, and `Id` is empty on every ordinary row.

**The parser drops any entry whose name contains a separator or is `.` or `..`**, rather than
showing it, so it can never be echoed back as part of a fetch.
A name is chosen on the other device and is not this one's to trust as a path component.

Truncation is **reported** rather than silent, so the other end can say the listing was shortened
instead of quietly showing part of a folder.

A `FetchRequest` is answered with an ordinary `FileOffer`, so the transfer, hashing and refusal
paths are the ones already built and tested.

## See also

[[content-types]] · [[crypto]] · [[protocol-tcp]] · [[protocol-ble]] · [[shared-folders-security]]
