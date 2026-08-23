---
type: mechanism
status: shipped
platforms: [windows, android, linux]
tier: ble
code:
  - src/CoreLib/Transport/BleLinkArbiter.cs
  - src/DesktopCore/Daemon.cs
  - src/WinDaemon/Program.cs
  - src/AndroidClient/SyncManager.cs
updated: 2026-08-23
---

# BLE link arbitration

**Should this device be scanning at all, and which of two links to one peer dies.**

This is the sharpest edge in the project.
If you are about to touch the Bluetooth tier, read this note first.

## The problem

Every install advertises the same service UUID and every device also scans for it.
So two devices in range each dial the other, both links come up carrying the same peer, and
**the clipboard crosses twice** - the [[echo-suppression]] is on the sending side, so the receiver
has no defence at all.

A duplicate link is not cosmetic.

## Where it lives

`src/CoreLib/Transport/BleLinkArbiter.cs`, over `BleRoleRules` in `BleRole.cs`.
Two entry points:

| Call | When |
|---|---|
| `ShouldDialAnyPeer` | before scanning |
| `KeepFor` | to settle a collision when two devices dial inside the same moment |

All three platforms go through it:

- **Windows** gates its scan loop - `src/WinDaemon/Program.cs`.
- **Android** repairs the collision afterwards in `ResolveBleCollision` - `src/AndroidClient/SyncManager.cs`.
- **Linux** does both - `src/DesktopCore/Daemon.cs`.

**Advertising is never gated. Only scanning.**
A peer that cannot advertise depends on this device staying findable.

## How it got here

Windows had prevented this all along and Android repaired it afterwards.
The Linux head did neither, and five comments in `DesktopCore` claimed the roles were "settled per
link by `BleRoleRules`" while describing behaviour the code did not have.
`BleRoleRules` was named in five comments and called by none of them.

That was the largest of the eighteen connection defects fixed in v0.2.3.

## The two rules

Both are in `AGENTS.md`.

**Never scan without asking `BleLinkArbiter` first.**
The cheapest refusal is not scanning at all: a device whose role is the peripheral has no business
dialling anybody.

**Never let both radio halves carry one peer.**

## Report a capability honestly or this is worse than useless

The Linux box claims it can advertise and then BlueZ refuses the exported GATT tree, so
`LinuxBlePeripheral` stands aside.
Telling the arbiter `BleCapability.Both` anyway makes it answer "you advertise" - and the device
then **neither advertises nor scans**, which is a deadlock rather than a degraded state.

`Daemon` sets the capability from whether the peripheral actually *started*, not from what the
adapter claimed.
See [[ble-role-negotiation]].

## Refusing other people's meshes

The service UUID is shared by every install, so a scan finds every Mesh Sync device in range and
not only the ones in this mesh.
This is not hypothetical: a laptop here held a Bluetooth link to a phone in somebody else's mesh
for as long as both were in range.

Refusing is not enough on its own.
A refusal that is not remembered is a reconnection four seconds later, forever.

| Guard | Value |
|---|---|
| Time to produce a session before the link is dropped | 12 seconds |
| How long a refusing device is left alone | 5 minutes |
| Scan cadence | every 30 seconds, 12-second window |

Eleven connect attempts in ninety seconds became one.

**The cooldown must be keyed on the fingerprint, not only the address.**
It was keyed on the BlueZ object path, which encodes the LE address, and a phone rotating its LE
address arrives under a path nothing has refused.
Keying on identity cannot stop the connection, because nothing knows who a device is until its
hello arrives, but it refuses on the hello in one second rather than after the full twelve-second
grace.

**BlueZ keeps a device object for every LE address it has ever seen**, so most are ghosts still
carrying the service UUID they advertised at the time.
RSSI is the discriminator: BlueZ publishes it only while a device is being seen in the current
discovery session.

**An active scan alongside a live link contends for the same antenna**, which is most of why an
established link felt rough rather than merely duplicated.
The old cadence was 4 seconds and ungated.

## See also

[[ble-role-negotiation]] · [[bluetooth-tier]] · [[link-state]] · [[echo-suppression]]
`HANDOFF.md` under "The Linux and Mac port".
