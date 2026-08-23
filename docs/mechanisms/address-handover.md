---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Transport/SyncContent.cs
  - src/CoreLib/Transport/TcpDiscoveryService.cs
updated: 2026-08-23
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

`src/CoreLib/Transport/TcpDiscoveryService.cs` is UDP discovery.
It was built on both sides and consumed by neither.

Handover over an existing link does the job better: no multicast, and it works on networks with
client isolation.
`HANDOFF.md` records the open decision that `TcpDiscoveryService` should probably be deleted, and
`IDiscoveryService.cs` is the seam it sits behind.

## The parsing rule

**An address that arrives from a peer is checked as an IP, never believed.**
It arrives inside an authenticated payload from a paired device and it is still parsed.

Related: a dual-stack listener reports IPv4 peers as `::ffff:192.168.0.103`, which parses as an
address, reads fine in a log, and can never be dialled back.
Unwrapped both where addresses are recorded and where they are dialled.
See [[wifi-tier]].

## See also

[[content-types]] · [[wifi-tier]] · [[peer-registry]]
