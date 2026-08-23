---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: wifi
code:
  - src/CoreLib/Transport/TcpAcceptor.cs
  - src/CoreLib/Transport/TcpTransportConnection.cs
  - src/CoreLib/Transport/MeshLinks.cs
  - src/CoreLib/Transport/NetworkUtil.cs
updated: 2026-08-23
---

# Wi-Fi tier

Length-prefixed TCP on port 45001, raised on demand and dropped again.
Carries anything, and is the only tier that carries images or files.

**It is not a fallback.** Neither tier is. See [[bluetooth-tier]].

## Where it lives

| Piece | File |
|---|---|
| Listener | `src/CoreLib/Transport/TcpAcceptor.cs` |
| One framed session with one peer | `src/CoreLib/Transport/TcpTransportConnection.cs` |
| One session per paired device, fanning out on send | `src/CoreLib/Transport/MeshLinks.cs` |
| Is there a network at all | `src/CoreLib/Transport/NetworkUtil.cs` |

## When Wi-Fi is wanted

Any one of these:

- the screen is on
- a send needs it
- a peer has asked for it, through a `ControlWakeWiFi` frame
- **Bluetooth is not up**

That last condition is load-bearing.
Without it, losing Bluetooth would leave a device with no link at all, and inverting the tiers
would have been a regression rather than an improvement.

The wake frame exists because a device cannot simply dial its peer on demand: either end may be
the listener.

## One session per peer, and no relaying

Every device talks to every other directly and nobody forwards anything, so there is no routing
and no loops to prevent.
The trade is that it assumes a complete graph: two devices that cannot reach each other simply do
not sync, rather than being bridged by a third.

Listening moved out of `TcpTransportConnection` into `TcpAcceptor` so that a second peer joins
instead of evicting the first.

## Collisions

Both devices listen and dial, so two can dial each other at once.
The link opened by the **lower fingerprint** survives.
Both ends compute that from values they already exchanged, so there is no negotiation round trip.

`AGENTS.md` requires that any change to connection handling stays correct when two devices collide,
and forbids adding a second rule beside this one.

## Findings worth knowing

**`MeshLinks` used one port for two different things.**
The constructor took a single `port` and used it both to bind the listener and as the port to dial
when a stored address carried none.
Those are the same number for every device in the field, so nothing ever noticed - until two
devices were run on one machine and the one listening on 45002 dialled its peer's bare address on
45002, which is itself.
There is a separate `peerPort` now, defaulting to `port`.

**`TcpClient.ConnectAsync` has no default timeout.**
With no route it waited over two minutes.
Bounded to five seconds.

**A connect timeout raises `OperationCanceledException`.**
Catching that as "the caller cancelled us" meant a phone with Wi-Fi off never reached the fallback.

**A dual-stack listener reports IPv4 peers as `::ffff:192.168.0.103`.**
That parses as an address, reads perfectly well in a log, and can never be dialled back.
Unwrapped both where addresses are recorded and where they are dialled, so a stored one self-heals.

**Mobile data counts as "a network".**
Check for a Wi-Fi or Ethernet transport specifically.
And on a phone acting as a hotspot the active transport is *cellular*, because that is how the
phone reaches the internet, so `HasUsableNetwork` has to fall through to the interface list and
recognise an access-point interface.
That bug broke Wi-Fi in the one topology where the peer was a single hop away.

**`DisconnectAll` cleared the links and left the handshakes.**
A socket that has been accepted but whose hello has not been read yet is not in the link table, so
"drop everything" was followed by it promoting itself once the hello landed - and nothing was left
to drop it again.
That is a socket held open all night under standby.
Found as a test that failed only under load.

**A heartbeat is not free, but an idle socket is.**
10s was chosen for fast drop detection before anything weighed the cost.
Now 30s with a 90s timeout.

## See also

[[bluetooth-tier]] · [[wire-formats]] · [[link-state]] · [[address-handover]] · [[file-transfer]]
