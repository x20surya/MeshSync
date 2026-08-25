---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/Fabric/RouteTimings.cs
  - src/CoreLib/Transport/TcpTransportConnection.cs
  - src/CoreLib/Transport/BleProtocol.cs
  - src/CoreLib/Transport/Ble/MeshBeacon.cs
updated: 2026-08-25
---

# Every timeout and interval

Collected by reading the source.
Nothing else in the repo lists these together, and most of them have a reason that is not
guessable from the number.

**Since v0.4 most of them live in one record**, `CoreLib/Transport/Fabric/RouteTimings.cs`, so a
test can shrink them to milliseconds and nothing in the suite sleeps. The values are the ones that
were already in the field, not fresh choices.

## The fabric

| Constant | Value | What it does |
|---|---|---|
| `HandshakeGrace` | 12s | How long a connected route has to agree a session before it is closed. **The single value that stops a stranger holding the standing link**, and it applies to every route kind on every head. |
| `MinBackoff` / `MaxBackoff` | 1s / 60s | Per peer, per route kind, keyed on fingerprint so an LE address rotation cannot reset it |
| `ActiveCeiling` | 8s | Backoff ceiling while somebody is at the device |
| `IdleCeiling` | 60s | Backoff ceiling with the screen off |
| `RefusalCooldown` | 5 min | How long a device that produced no session is left alone |
| `ScanInterval` / `ScanWindow` | 30s / 12s | One discovery window, stopped in a `finally` between rounds |
| `ScanRoundBudget` | 45s | The longest a whole round may take before it is cancelled and retried. The window is what a scan aims for; this is what happens when it never comes back **at all** - one unanswered BlueZ call took Bluetooth off a Linux box for the life of the process, with the adapter left discovering because the round's own `finally` never ran |
| `RotationInterval` | 2 min | How often the radio reconsiders which peers hold its central links |
| `MaxBleCentralLinks` | 4 | Concurrent outbound radio links |
| `ReconcileInterval` | 15s | How often the supervisor runs with nothing signalling it |
| `SupervisorWatchdog` | 60s | A pass that has not finished in this long means the loop is wedged |

`SupervisorWatchdog` exists because `Console.In.ReadLineAsync` is not asynchronous: it ran a
blocking read inline on a thread D-Bus needed and stopped the entire Bluetooth tier while logging
nothing. A loop that is alive but wedged looks exactly like a loop that is working.

## The mesh beacon

| Constant | Value | Why |
|---|---|---|
| Epoch | 15 min | Matched to the LE private-address rotation window, so it adds no linkability the radio does not already have |
| Epoch tolerance | &plusmn;1 | 45 minutes of clock skew in total |

See [[mesh-beacon]].

## Wi-Fi tier

| Constant | Value | Where |
|---|---|---|
| Heartbeat interval | 30s | `TcpTransportConnection` |
| Peer timeout | 90s | `TcpTransportConnection` |
| Hello deadline | 10s | `TcpTransportConnection` |
| Connect timeout, desktop | 6s | `Daemon.DialTimeout` |
| Connect timeout, bounded | 5s | `WiFiRouteProvider.DialTimeout`, over `TcpClient.ConnectAsync` which has **no default** |
| TCP keepalive | 15s idle, 5s interval, 3 retries | Advisory only |
| Reconcile interval | 15s | `RouteTimings`, one value for every head |
| Wi-Fi wake timeout | 15s | Both heads, deliberately the same |

The heartbeat was 10s/30s and is now 30s/90s.
**Do not shorten it again without reading the comment**: an idle socket is free, a heartbeat is
not, because every one pulls the Wi-Fi chip out of power save.
Bluetooth carries presence anyway and notices a vanished peer in 24s.

## Bluetooth tier

| Constant | Value | Where |
|---|---|---|
| Heartbeat interval | 8s | `BleProtocol` |
| Peer timeout | 24s | `BleProtocol` |
| Chunk receipt timeout | 5s | `BleProtocol.AckTimeout` |
| Scan interval | 30s | `RouteTimings`, shared by every head |
| Scan window | 12s | `RouteTimings` |
| Scan round budget | 45s | `RouteTimings` |
| BlueZ call timeout | 20s | `BlueZ.CallTimeout` - a D-Bus call that is never answered awaits for ever |
| Handshake grace | 12s | `RouteTimings`, and it is now the same value on all three |
| Refusal cooldown | 5 min | `RouteTimings` |
| Reassembly stale | 30s | `BleReassembler` |

**The scan interval was 4 seconds and ungated.**
That is most of why the radio never settled: an active scan alongside a live link contends with
it for the same antenna.
Discovery is now stopped in a `finally` between rounds rather than started once and left running
for the life of the process.

The two Bluetooth cooldowns are both needed and they are keyed differently.
The 5-minute one is keyed on the BlueZ object path, which encodes the LE address.
A phone rotates its LE address, so the identity-keyed one refuses on the hello in about a second
instead of holding the link for the full 12-second grace.
See [[ble-link-arbitration]].

## Pairing and files

| Constant | Value | Where |
|---|---|---|
| Pairing window | 3 min | `PairingWindow.DefaultDuration` |
| File answer timeout | 30s | `FileTransferSender.AnswerTimeout` |
| Incoming file stale | 5 min | `FileTransferReceiver.StaleAfter` |
| Browse timeout | 20s | `BrowseService.Timeout` |
| Keyring probe | 5s | `Daemon.ResolveProtector` |

The pairing window is long enough to scan a code, unlock a phone and let it connect, and short
enough that leaving the screen open by accident is not an open door for the afternoon.

The file answer timeout is 30s because **the offer may be the very thing that woke the peer**:
a peer on Bluetooth alone has to raise Wi-Fi before it can accept.

The keyring probe is bounded because **a locked keyring can sit waiting on a prompt the user may
never answer**. Falling back to an unwrapped key is worse than wrapping it and far better than
failing to start.

## Clipboard echo suppression

| Constant | Value | Why |
|---|---|---|
| Echo window | 10s | How long received content is remembered |
| Duplicate send window | 900ms | Collapses the burst of notifications for one copy |
| Image guard | 3s | Covers decode plus re-encode of a large screenshot |
| Capacity | 32 entries | Belt and braces against unbounded growth |

The duplicate window is deliberately **short**, so a genuine re-copy a second or two later still
syncs. The image guard is deliberately **longer**, because a received JPEG is decoded and
re-encoded on capture so its bytes never match and only a time window can catch it.
See [[echo-suppression]].

## Sizes, for completeness

| Limit | Value |
|---|---|
| TCP frame ceiling | 32 MB |
| BLE payload ceiling | 64 KB |
| BLE attribute value | 512 B, hard |
| BLE reassembly ceiling | 4 MB |
| File chunk | 1 MB |
| File ceiling | 4 GB |
| Browse listing | 500 entries |
| Notification text | 1024 B |
| Reply text | 2048 B |
| Mesh name | 40 chars |

## See also

[[protocol-tcp]] · [[protocol-ble]] · [[ble-link-arbitration]] · [[echo-suppression]]
