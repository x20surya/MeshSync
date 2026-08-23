---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/SyncContent.cs
  - src/CoreLib/Transport/Fabric/WiFiRouteProvider.cs
updated: 2026-08-24
---

# Address handover

"This is where I am reachable."
A device announces its current LAN address to its peers over whichever link is already up, as
content type `Address` (0x02).

## The problem it solves

[[pairing]] pinned the address baked into the QR code, so a DHCP lease change broke it until the
code was rescanned.
Now whichever link is up carries the new address the moment it changes.

Verified across a real hotspot subnet change: the subnet moved from `10.178.251.x` to
`10.137.49.x` across a reboot and both sides re-announced and reconnected unaided.

## Why it replaced multicast discovery

`TcpDiscoveryService` was UDP discovery.
It was built on both sides and consumed by neither, and it was **deleted in v0.4** rather than
left alongside its replacement.

Handover over an existing link does the job better: no multicast, and it works on networks with
client isolation.
`IDiscoveryService.cs` remains as the seam, because `AndroidBleDiscovery` still sits behind it.

The one case handover cannot cover is both devices changing address at once, because it needs a
link that already exists.
A mesh-scoped LAN beacon carrying the same tag as [[bluetooth-tier]]'s advertisement is the
designed replacement for that, and it is deliberately outside the numbered v0.4 phases.

## The parsing rule

**An address that arrives from a peer is checked as an IP, never believed.**
It arrives inside an authenticated payload from a paired device and it is still parsed.

Related: a dual-stack listener reports IPv4 peers as `::ffff:192.168.0.103`, which parses as an
address, reads fine in a log, and can never be dialled back.
Unwrapped both where addresses are recorded and where they are dialled.
See [[wifi-tier]].

## See also

[[content-types]] · [[wifi-tier]] · [[peer-registry]]
