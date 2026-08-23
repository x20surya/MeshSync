---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - tests/CoreLib.Tests
updated: 2026-08-23
---

# What the tests cover

**21 files, 243 `[Fact]`/`[Theory]` attributes, 296 cases** once the theories expand.
Verified by running it on 2026-08-23: `Failed: 0, Passed: 296`, 670 ms.

> **The root documents say 286.** They are right about the last commit and wrong about the working
> tree: the uncommitted notification-reply work adds ten cases across `NotificationProtocolTests`,
> `SyncContentTests` and `PeerRegistryTests`. Update `README.md`, `AGENTS.md`, `CLAUDE.md` and
> `HANDOFF.md` in the commit that lands it.

Everything tested is in `CoreLib`.
**No head has a test.** That is the shape of the risk: the shared core is well covered and every
platform edge is not, which is exactly why `HANDOFF.md` records four defects that only hardware
found and no test could have.

## By file

| File | `[Fact]`/`[Theory]` | Covers |
|---|---|---|
| `WireFormatTests` | 21 | Both hellos, frame discrimination, extended frames |
| `FileTransferTests` | 19 | Offer, ack, chunk, hash, refusal, stale, progress |
| `PeerSecurityTests` | 18 | Authorisation, the pairing queue, revocation |
| `NotificationProtocolTests` | 18 | Round trips, caps, replies, forward compatibility |
| `SharedFoldersTests` | 17 | Traversal refusal, ids, listing, symlinks |
| `EchoSuppressorTests` | 16 | Echo, duplicates, the image guard |
| `BleFragmenterTests` | 16 | Chunking, reassembly, gaps, MTU edges |
| `PeerRegistryTests` | 14 | Persistence, addresses, the port guard |
| `TcpTransportConnectionTests` | 11 | Real loopback sockets, split frames, bad headers |
| `BrowseProtocolTests` | 11 | Request, reply, truncation, hostile names |
| `LinkStateTests` | 10 | Tier precedence, per-instance isolation |
| `CryptoEngineTests` | 10 | AES-GCM, tagging, tamper, wrong key |
| `TransportSettingsTests` | 9 | Preference, persistence, a throwing store |
| `MeshLinksTests` | 9 | Several peers, collisions, broadcast, isolation |
| `BleLinkArbiterTests` | 9 | Whether to scan at all |
| `SessionKeysTests` | 8 | Agreement from both sides, per-connection keys |
| `BleRoleRulesTests` | 8 | Capability-first role selection |
| `DeviceIdentityTests` | 6 | Fingerprints, persistence, validation |
| `SyncActivityLogTests` | 5 | Bounds, previews, location |
| `KeyProtectionTests` | 5 | Wrap, migrate, refuse-to-replace |
| `SyncContentTests` | 3 | **Every content type is accounted for** |

## The tests that encode a rule rather than a behaviour

These are the ones to read before changing the thing they guard.

- `A_third_device_cannot_read_traffic_between_the_other_two` - the reason the key is per pair.
- `The_same_pair_agrees_a_different_key_on_every_connection` - forward secrecy, stated as a test.
- `A_payload_from_an_earlier_connection_does_not_open_on_a_later_one` - the same, from the
  attacker's side.
- `Forgetting_a_device_revokes_a_live_session` - why `PeerSession.IsUsable` asks on every payload.
- `A_wrapped_key_is_not_replaced_by_a_build_that_cannot_unwrap_it` - the [[key-at-rest]] rule that
  a test caught before a person did.
- `A_stranger_that_wins_the_race_is_queued_rather_than_trusted` - the whole point of
  fingerprint comparison in [[pairing]].
- `Two_scanners_never_dial_each_other` and `A_device_that_cannot_advertise_is_always_the_central` -
  [[ble-role-negotiation]] is capability first.
- `Message_ids_never_take_the_extended_marker` - the [[protocol-ble]] invariant everything rests on.
- `Every_content_type_is_accounted_for` - **this is the one that fails when you add a content type
  and forget to handle it in both apps.**
- `Seeing_a_peer_does_not_drop_the_port_it_was_paired_with` - the `WouldLosePort` guard.
- `A_sibling_with_a_shared_prefix_is_outside` - the trailing-separator bug in `IsInside`.

## Deliberate design decisions visible in the suite

**`MeshLinksTests` and `TcpTransportConnectionTests` use real loopback sockets**, not fakes.
That is why they catch split frames and desynchronisation at all.

**`FileTransferSender` takes an `answerTimeout` override that only tests pass.**
Waiting out the real half minute to prove a timeout works would put half a minute on every run,
and a slow suite is one that stops being run.

**`ProtocolVersion` is `internal` and the tests read it.**
A copy in a test file goes stale the moment it is bumped, and a version mismatch drops a
connection in exactly the way most of those tests provoke, so they carry on passing for the wrong
reason. This happened twice before it was fixed.

**`PairingWindow` was made an instance rather than a static** because global mutable state broke
test isolation under xUnit's parallel execution before it broke anything else.

## What is not covered

| Not tested | Why it matters |
|---|---|
| Every head | All five. No UI, no platform service, no radio driver |
| The Windows, Android and Linux BLE stacks | Only `CoreLib`'s framing and rules are |
| Clipboard capture on any platform | Including the Wayland protocol client |
| D-Bus, both the keyring and [[dbus-ipc]] | `meshsyncctl` is the nearest thing to a test |
| Three devices at once | The fan-out path has only run over loopback |
| Doze survival | Only time can answer it |

`src/CryptoTest` and `src/TransportTest` are console demos kept from early development.
**They print to the screen and assert nothing.**

## Running

```bash
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

Every project holds a **zero-warning bar**, and an incremental build will not re-report warnings,
so use `-t:Rebuild` when you need to be sure.

## See also

[[_meta/vault-guide]] · [[building]] · [[crypto]] · [[protocol-ble]]
