---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - tests/CoreLib.Tests
  - plasma/check.sh
updated: 2026-08-25
---

# What the tests cover

**33 files, 386 `[Fact]`/`[Theory]` attributes, 452 cases** once the theories expand.
Verified by running it on 2026-08-25: `Failed: 0, Passed: 452`, 4 s.

Everything in that suite is `CoreLib`. **One head now has a check**, and it is not an xUnit one.

## `plasma/check.sh` - the Linux head's only executable check

Starts a scratch daemon on its own `--data` and `--port`, loads **the real `MeshBus.qml`** under
`plasmawindowed`, calls every function on it once, and reads `dbus-monitor`.

**It asserts bytes on the wire, not the absence of an exception**, and that is the whole point.
Every defect it was written for produces a call that is dispatched, answered, and logged as an
ordinary failure:

- a `signature` set on a QML `DBusMessage` makes the binding send an **empty body**;
- `DBus.string(x)` without `new` throws inside the *caller*, so nothing is sent at all;
- a daemon that does not declare `org.freedesktop.DBus.Properties` makes Qt drop the arguments to
  `Get` and `Set`, because Qt introspects before it marshals.

None of the three is visible to a test that asks "did it throw", and none can be caught by
`meshsyncctl`: `gdbus` encodes arguments correctly regardless, so the shell tool passes against a
surface no Qt client can use. Counting the body catches all three. It went **1/18 before the fixes
on 2026-08-25 and 20/20 after**, the last two being liveness checks.

`plasma/preview.sh` is the other half - the working tree in one window, against whichever daemon
is running.

## What is still uncovered

**No head has an xUnit test**, and three of the four have no automated check at all. That is the
shape of the risk: the shared core is well covered and every platform edge is not, which is
exactly why `HANDOFF.md` records four defects that only hardware found and no test could have -
and why the Plasma widget shipped with twelve controls that did nothing.

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
| `MeshLinksTests` | 10 | Several peers, collisions, broadcast, isolation, dropped handshakes |
| `BleLinkArbiterTests` | 9 | Whether to scan at all |
| `SessionKeysTests` | 8 | Agreement from both sides, per-connection keys |
| `BleRoleRulesTests` | 8 | Capability-first role selection |
| `DeviceIdentityTests` | 6 | Fingerprints, persistence, validation |
| `SyncActivityLogTests` | 5 | Bounds, previews, location |
| `KeyProtectionTests` | 5 | Wrap, migrate, refuse-to-replace |
| `SyncContentTests` | 3 | **Every content type is accounted for** |
| `RoutePolicyTests` | 14 | Wi-Fi demand per peer, roles, when to scan, when to advertise |
| `BleRadioSchedulerTests` | 19 | When to scan, who to try, what to remember, who yields a slot, adapter recovery |
| `MeshBeaconTests` | 17 | Build, verify, rotation, the 31-byte budget, pairing, and the rule |
| `CapabilityExchangeTests` | 14 | The capability byte on both wires, and forward compatibility |
| `MeshKeyTests` | 12 | Minting, lowest-key-wins, and a version 1 registry still loading |
| `MeshDiscoveryTests` | 13 | Ours, unknown and foreign; pairing beacons; adoption |
| `MeshHealthTests` | 11 | Per peer, why a route is not up, and two devices with one name |
| `PeerLinkTests` | 11 | The handshake deadline, both collision rules, route preference, backoff |
| `MeshFabricTests` | 9 | Three peers, links that arrive before identity, revocation |
| `LinkSupervisorTests` | 9 | Reconciling, idempotence, and the watchdog over a wedged pass |
| `MeshFabricLoopbackTests` | 4 | **Three devices over real sockets, through the fabric** |

## The tests that encode a rule rather than a behaviour

These are the ones to read before changing the thing they guard.

- `A_third_device_cannot_read_traffic_between_the_other_two` - the reason the key is per pair.
- `The_same_pair_agrees_a_different_key_on_every_connection` - forward secrecy, stated as a test.
- `A_payload_from_an_earlier_connection_does_not_open_on_a_later_one` - the same, from the
  attacker's side.
- `Forgetting_a_device_revokes_a_live_session` - why `PeerSession.IsUsable` asks on every payload.
- `A_peer_that_answers_but_never_identifies_is_dropped_at_the_grace` - the whole of the defect
  where a device from somebody else's mesh held the standing link. See [[peer-link]].
- `A_radio_link_to_one_peer_does_not_suppress_wifi_to_another` - why Wi-Fi demand is per peer.
- `Two_links_to_two_different_peers_are_both_kept` - why the collision rule is scoped to one
  `PeerLink`.
- `A_pass_that_never_returns_is_abandoned_and_counted` - a loop that is alive but wedged.
- `The_mesh_key_never_reaches_a_session_key` - the beacon is a filter, not a credential, and this
  is the assertion that keeps it one.
- `The_advertisement_fits_in_the_legacy_limit` - 31 bytes exactly, so a future field cannot break
  discovery silently.
- `A_silent_advertisement_is_still_tried_just_not_first` - why the beacon is a ranking rather than
  a gate: treating silence as a refusal would partition the mesh.
- `A_route_already_being_opened_is_not_opened_again` - found by running two daemons, not by
  reading.
- `A_handshake_dropped_mid_flight_does_not_promote_itself_afterwards` - why `DisconnectAll` has to
  clear the pending table as well as the link table.
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

**`MeshLinksTests`, `TcpTransportConnectionTests` and `MeshFabricLoopbackTests` use real loopback
sockets**, not fakes.
That is why they catch split frames and desynchronisation at all.

They share one xUnit collection, `LoopbackCollection`, so they never run concurrently.
Three classes each standing up several devices with listeners and dial loops contend for the
thread pool rather than for anything they are testing, and under that load two of them failed
together on a run that had passed three times in a row on an idle machine.
A flaky test is worse than a missing one, because it teaches you to re-run rather than to look.

**Everything above the transports is tested through fakes**, in `tests/CoreLib.Tests/Fakes`.
`FakeRoute` drives the route state machine by hand, `FakeBleRadio` scripts advertisements, and
`FakeClock` moves time - so a case covering a twelve-second grace or a five-minute cooldown runs
in microseconds and nothing in the suite sleeps.

`FakeBleRadio` is the highest-value piece. Every finding in `HANDOFF.md` under "Bluetooth" is a
scripted scenario there: a device that answers pings and never identifies itself, a ghost object
with no RSSI, a phone that rotates its address mid-cooldown, a foreign mesh sitting closer than
your own.
`FakeRoute.Establish()` throws unless a session has been agreed first, because there is no
legitimate path from a connected link to a usable one that skips the key agreement - a fake that
allowed it would let the fix silently regress.

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
