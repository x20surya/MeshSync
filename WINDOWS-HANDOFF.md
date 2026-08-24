# Windows handoff: v0.4 connection refactor

You are picking up the last unexercised platform of a finished refactor.
Everything here builds and passes; nothing on Windows has been near a radio.

Read this file, then [AGENTS.md](AGENTS.md) and [HANDOFF.md](HANDOFF.md).
[docs/mechanisms/peer-link.md](docs/mechanisms/peer-link.md) and
[docs/mechanisms/mesh-beacon.md](docs/mechanisms/mesh-beacon.md) are the two notes worth reading
before touching anything.

## Get the code

```powershell
git fetch origin
git checkout v0.4-connection-refactor
```

Branch is `v0.4-connection-refactor`, 13 commits ahead of `master`, already pushed.
**Do not merge to master.** The branch is not finished until this work is done.

## What changed, in one paragraph

The Wi-Fi tier was a mesh and the Bluetooth tier never was: every radio link in the project was a
nullable field, so a device held exactly one whatever the peer count, and every question about
connectivity was asked of the app rather than of a peer.
`CoreLib.Transport.Fabric` now holds one `PeerLink` per paired device owning every route to it,
with one `LinkSupervisor` where five loops used to signal each other, and
`CoreLib.Transport.Ble` holds one scheduler over one adapter with a mesh beacon that tells this
mesh from anyone else's before a connection is opened.

Wire version 4, `peers.json` version 2, **no re-pair** - verified on hardware.

## Your job

**Verify the Windows head on a real radio, against the phone.**
It compiles with zero warnings and runs the same shared layer that Android and Linux are now
verified on. It has never been run.

Windows-only files on this branch:

| File | State |
|---|---|
| `src/WinDaemon/WindowsBleRadio.cs` | **new, never run** |
| `src/WinDaemon/WindowsBleServerRoute.cs` | **new, never run** |
| `src/WinDaemon/WindowsBleCentral.cs` | heavily rewritten - the scan was split out of it |
| `src/WinDaemon/WindowsBleTransport.cs` | modified - capability byte, second-subscriber warning |
| `src/WinDaemon/Program.cs` | heavily rewritten - runs on the fabric now |
| `src/WinDaemon/MainWindow.xaml.cs` | device list asks per peer |

## Why this is not a formality

Plugging the phone in on Linux turned up **seven defects in the first ten minutes**, every one in
code the 451 tests cover and pass.
Five were distributed races; none was visible from one side's logs.

**Three of the seven were in files whose Windows equivalents you are about to run**, and an eighth
was found afterwards by re-reading the Windows code that had never been executed:

- `HasLinkTo` compared a fingerprint against a Bluetooth address - values that can never be equal -
  so the "already linked" filter never fired and a **second GATT link** was opened to the same
  device on every scan round. Fixed in `f8c8792`, still unexercised.

Assume the same density of defects here. The unit tests will not find them.

## Build and run

Kill any running instance first or the build fails on a locked `CoreLib.dll`.
It relaunches on its own because run-on-startup is enabled.

```powershell
Get-Process WinDaemon -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project src/WinDaemon/WinDaemon.csproj
```

Tests, which must stay at 451 and zero warnings:

```powershell
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
dotnet build src/WinDaemon/WinDaemon.csproj -t:Rebuild -warnaserror
```

Logs. The daemon is a `WinExe` with no console, so nothing reaches stdout:

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 40
```

Identity and pairing state, which an upgrade must not disturb:

```
%LOCALAPPDATA%\MeshSync\device.key
%LOCALAPPDATA%\MeshSync\peers.json
```

## Before you start: record the before-state

The migration claim is "no re-pair". Prove it rather than assuming it.

```powershell
Get-FileHash "$env:LOCALAPPDATA\MeshSync\device.key" -Algorithm SHA256
Get-Content "$env:LOCALAPPDATA\MeshSync\peers.json"
```

`peers.json` will be **Version 1**. After the first v0.4 run it must be **Version 2**, with the
same mesh name, the same peers, a new 32-byte `MeshKey`, and **`device.key` unchanged byte for
byte**. That is exactly what happened on the phone and on Linux.

**While you have it open, look for two peers holding the same `LastAddress`.** That is not a
curiosity, it is the defect described in section 2, and a registry that has outlived a DHCP lease
is the normal way to get one.

```powershell
(Get-Content "$env:LOCALAPPDATA\MeshSync\peers.json" | ConvertFrom-Json).Peers |
  Group-Object LastAddress | Where-Object Count -gt 1
```

Note also that **this machine's own record on the phone currently has no address at all.** That is
deliberate: the phone dialled `10.137.49.172` for this machine, the Linux laptop answered, and the
address was forgotten as provably wrong. It heals the first time this daemon connects or announces
one, and needs nothing from you - but if you were expecting the phone to dial you first, it will
not until then.

## What to verify, in order

### 1. It starts and the migration is lossless

The identity fingerprint in the log must be the one it had before, and the paired devices must
survive. If either changes, stop - that is a re-pair and the whole claim is wrong.

### 2. Wi-Fi to the phone, and no glare storm

This is where the worst Linux defect lived. Watch for this line:

```
[Fabric] WiFi to <fingerprint>: a second link of the same kind to one peer; dropping the other.
```

A handful at startup is normal and correct - both ends dial, that is the design.
**A steady stream is the bug.** On Linux it was 136 in two minutes and climbing, with routes
logging `established` and `lost` in the same millisecond. After the fix it is 2-3, all at startup,
then nothing.

Count them:

```powershell
(Select-String -Path "$env:LOCALAPPDATA\MeshSync\daemon.log" -Pattern "a second link of the same kind").Count
```

If it keeps climbing, there are **two different causes and they look identical from here.** Tell
them apart before you touch anything.

**Glare**, the original defect: both ends log collisions, routes log `established` and `lost` in
the same millisecond, and sockets open far faster than the reconcile interval. The rule is
`PeerLink.SettleSameKind` and it must depend on **direction alone** - never on whether a route has
finished handshaking, because that makes it non-deterministic and the two ends then kill each
other's links.

**A misdirected dial**, found on hardware after the branch was handed over: exactly **one** new
socket per reconcile interval, no faster, for ever. The giveaway is that the end whose link keeps
dying never dials - it only ever *receives* - so every guard on the dialling side reads as innocent
and `MayOpen` is never even consulted for that peer. The dial is being made for a **different**
peer whose stored address now belongs to this one, and the arriving link is adopted under whoever
answered, which drops the healthy one as a same-kind collision.

Since `664d4c2` that case announces itself, and either line means your registry held a duplicate:

```
[Fabric] Not dialling <A> at <addr>: <B> is established there. Forgetting the address.
[Fabric] Dialled <A> at <addr> and <B> answered. Forgetting that address: it is the other device's now.
```

Seeing one of those once is the fix working and the registry healing. Seeing the second one
repeatedly for the same pair is a bug: the address should have been forgotten the first time.

### 3. Bluetooth: does a session actually get agreed

The failure mode to expect, because three platforms hit variants of it:

```
[BleLink]  Negotiated MTU 517
[BleLink]  Announced this device over Bluetooth
...12 seconds later...
[Ble]      <name> produced no session; ignoring it for 5 minutes
```

while the **other** device logs `Peer identified` and looks perfectly healthy.
That means one side's hello never arrived or was never sent. On Linux the notification dispatch
was registered after subscribing; on Android the peripheral refused to send a 273-byte hello
because its MTU callback never fired.

**Windows was checked and is correct on the first of those** - `WindowsBleCentral` subscribes
`ValueChanged` *before* enabling notifications, at `WindowsBleCentral.cs:183`. Do not "fix" it.

The second is open. `WindowsBleTransport.MaxNotificationSize()` reads `SubscribedClients` live at
send time rather than caching an MTU, which *should* mean it does not have Android's problem -
but that is reasoning, not evidence. Watch for any hello that is logged as too large to send.

Success looks like this, from `meshsyncctl`-style output or the log:

```
[BleLink] Radio link up to "S21 FE" (AC83-…)
[Ble]     Radio link up to AC83-…. 1 of 4.
[Ble]     Announced this device at 10.x.x.x over Bluetooth
```

### 4. Both tiers to one peer at once

The end state on Linux, which Windows should reach too:

```
PEER            ROUTE            STATE          SINCE
S21 FE          wifi             Established    00:03
                ble-central      Established    00:18
RADIO  1/4 links
```

`RADIO 0/4` beside an established `ble-central` is a bug, not a display quirk - it means the link
never entered the budget, so the four-link cap is not enforced and rotation never runs. That exact
defect was found and fixed on the shared path; if it reappears on Windows the cause is local.

### 5. The device list asks per peer

`MainWindow` used to mark at most one device connected and guess which by comparing names, which
broke outright with two devices called the same thing. It calls `Program.IsConnectedTo` and
`IsWiFiConnectedTo` now. With two paired devices, only the reachable one should show connected.

### 6. The stranger case

If any other Mesh Sync device is in range - another phone, another install - it must be refused
and then **left alone for five minutes**, not retried every thirty seconds. Look for:

```
[Ble] <name> produced no session; ignoring it for 5 minutes.
```

Once both devices in your mesh hold the same mesh key, a foreign device should be skipped without
connecting at all: the scan summary goes from `N seen, N ours` to `N seen, 0 ours`.

## Traps specific to this platform

- **Kill `WinDaemon` before building** or `MSB3021` on a locked `CoreLib.dll`.
- **`Brush`, `MessageBox` and `Timer` are ambiguous** because WinForms is referenced for the tray
  icon. Qualify them.
- **`LinkState` is ambiguous too** - WinForms has one. `Program.cs` aliases it explicitly.
- **Windows keeps one outstanding notification per characteristic.** The four-byte chunk receipt
  in our own protocol exists for that; do not replace it with indications, which went unconfirmed
  on this stack and tore the link down with `GATT status 19`.
- **Notifying a characteristic reaches every subscriber.** Payloads are sealed per peer, so the
  server must address one subscriber - `SendPayloadToAsync`, not a broadcast.
- **A GATT server here serves one central at a time.** One reassembler, one session. It logs when
  a second subscribes rather than corrupting silently. That is a known limit, written down in
  `AGENTS.md`, not something to fix in passing.
- **The 30-second link churn is not ours.** Windows drops an accepted BLE link at almost exactly
  30 seconds and the phone reconnects in about a second. `GattSession.MaintainConnection` does not
  stop it. See `HANDOFF.md`.
- **A stale beacon usually means a dangling connection, not broken rotation.** On Linux the radio
  reported one device advertising the service and none of them in this mesh, and the beacon on the
  air verified against the right mesh key but for an epoch forty-five minutes old. Rotation was
  fine. A previous connection to the phone's old random address was still open without ever having
  become a route, the phone had stopped advertising because a central was connected, and the stack
  was serving the last advertisement it had received. Check for an existing connection before
  believing an old beacon. Hard-killing the daemon mid-session is a good way to create one.

## Rules you must not break

These are in `AGENTS.md` and they are load-bearing:

- **A route becomes usable only through a session, and the handshake has a deadline.** Do not add
  a path that reports a route usable before its session exists. That is the whole defect this
  refactor is named after.
- **The mesh beacon decides who to *try*, never who is let in.** The mesh key must never enter a
  session key derivation. `The_mesh_key_never_reaches_a_session_key` asserts it.
- **The beacon is a ranking, not a gate.** A missing beacon means "try after anything that
  verified". Windows publishes no beacon - a `GattServiceProvider` has no room for manufacturer
  data beside a 128-bit service UUID - so if you make silence a refusal, **you partition Windows
  out of every mesh.**
- **Two links to one peer are a collision; two links to two peers are a mesh.** The rule lives
  inside one `PeerLink`. Do not lift it out.
- **Diagnostics go through `CoreLib.Diagnostics.Log`**, never `Console.WriteLine`.

## If you find something

Follow what this branch already does:

1. Reproduce it end to end first - that is the project rule and it is what found all eight.
2. Write the test **before** the fix, and check it fails against the old behaviour by reverting.
   Every fix on this branch was verified that way.
3. Fix it in `CoreLib` if the defect is shared, and only in `src/WinDaemon` if it is genuinely
   Windows. Three of the eight were shared and had been written twice more, differently.
4. Record the finding in `HANDOFF.md` under **What the phone found**, in the same voice: what it
   looked like, why it was invisible, what it cost.
5. Keep the zero-warning bar and the test count moving in one direction.

**Add a log line before you add a theory.** The eighth defect was found by one, and the seventh was
only findable because a diagnostic written for an unrelated reason was already there. A path that
returns silently is the failure mode this project keeps rediscovering.

## State when this was handed over

- Branch `v0.4-connection-refactor`, clean tree, pushed to `origin`.
- **452 tests**, zero warnings across all nine projects.
- All three heads build on Linux - Windows via `-p:EnableWindowsTargeting=true`.
- Verified on hardware: Android and Linux together, both tiers to one peer at once, no re-pair.
- **Not verified:** anything Windows, on a radio. Also no third device at once, so the four-link
  budget and rotation have never had a fifth peer to act on.

**Read `664d4c2` before you start.** It landed after the first version of this handoff, and it is
the only defect on this branch that was found by running the finished thing rather than by reading
it. A stale duplicate address in the registry tore the Wi-Fi link down and rebuilt it every fifteen
seconds, indefinitely, and both logs read as though the two devices simply could not hold a
connection. This machine's record is the one that was stale, so you are walking into the exact
registry that produced it.
