---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Identity/DeviceIdentity.cs
  - src/CoreLib/Identity/PeerRegistry.cs
  - src/DesktopCore/Paths.cs
updated: 2026-08-23
---

# What is written to disk

Three files, one directory, and nothing else.
Clipboard traffic is never stored: [[activity-log]] is in memory and dies with the process.

## Where

| Platform | Directory |
|---|---|
| Windows | `%LOCALAPPDATA%\MeshSync\` |
| Linux and macOS | `$XDG_DATA_HOME/MeshSync`, else `~/.local/share/MeshSync/` |
| Android | the app-private files directory |

`--data` on [[linux-daemon]] overrides it, which is how two devices run on one machine.

## `device.key`

The P-256 keypair, PKCS#8.

Wrapped form begins with the four ASCII bytes **`MSK1`**, then whatever the platform's
`IKeyProtector` produced. Unwrapped form is bare PKCS#8, which is what a build with no protector
writes and what pre-wrapping installs still hold until their next run upgrades them in place.

Written to `.tmp` and moved into place.
chmod 600 on POSIX; on Windows it inherits the user-private profile ACL.

**Deleting it forces a re-pair**, and is the fastest way to test [[pairing]] from scratch.

## `peers.json`

`System.Text.Json`, source-generated, written indented.

```json
{
  "Version": 1,
  "MeshName": "Surya's Mesh",
  "Peers": [
    {
      "PublicKey": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...",
      "Name": "S21 FE",
      "LastAddress": "192.168.0.104",
      "LastSeenUtc": "2026-08-23T14:22:05.123+00:00",
      "IntroducedBy": null
    }
  ]
}
```

`Fingerprint` is `[JsonIgnore]` and derived from `PublicKey` on read, so it can never disagree
with the key it names.

A record whose key will not parse is **dropped on load** with a log line, because keeping it would
put an undeletable ghost in the user's device list.

Also written to `.tmp` and moved, so an interrupted write cannot leave a half-file that reads as
"no devices paired".

**`LastAddress` is a hint, never an identity.** A DHCP lease change moves it, which is precisely
why pairing is keyed on the public key.

### The `WouldLosePort` guard

`NoteSeen` refuses to overwrite a stored `host:port` with the same bare `host`.

The address a connection reports is deliberately port-less: on an accepted socket the peer's port
is its ephemeral source port, useless to dial back.
That is right for learning where an unknown peer lives and wrong for one whose `host:port` a human
supplied in a pairing code, which is exactly the first connect after joining, so the port is lost
before it is ever used.

It hides in the field because every device listens on 45001 and a bare host dials there anyway.
It does not hide with two devices on one machine, which is the arrangement this project relies on.

Only the **same** host is protected. A device that genuinely moved must still be able to record
where it moved to.
Parsing requires exactly one colon with a valid port after it, so a bare IPv6 address is never
mistaken for a host and port.

The file is written only when something durable moved, not on every heartbeat.

## `daemon.log`

Plain text, `[HH:mm:ss.fff] [Tag] message`, appended by whatever sink the head installs.
Tags in use: `Identity`, `Peers`, `Pairing`, `Transport`, `Mesh`, `Ble`, `Files`, `Browse`,
`Network`, `Daemon`, `Sync`, `Ring`, `Notify`, `Share`, `ProcessText`, `Clipboard`, `Service`.

## Transport preference

Not in this directory on Windows: it is a registry value
(`src/WinDaemon/RegistryTransportPreferenceStore.cs`).
Linux and macOS keep it in a file beside `peers.json`
(`src/DesktopCore/Platform/FileTransportPreferenceStore.cs`).
See [[transport-preference]].

## Part-files

`FileTransferReceiver` writes `{guid:N}.part` into a work directory while a transfer is in flight,
and deletes it on failure, on a hash mismatch, or after 5 minutes of silence.
A completed file is handed to the head as a **working copy to move**, because only the head knows
what Downloads means.

## See also

[[peer-registry]] · [[device-identity]] · [[key-at-rest]] · [[crypto]] · [[file-transfer]]
