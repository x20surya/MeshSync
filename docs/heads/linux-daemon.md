---
type: head
status: shipped
platforms: [linux, macos]
tier: either
code:
  - src/LinuxDaemon/Program.cs
  - src/LinuxDaemon/Shell.cs
updated: 2026-08-23
---

# Linux daemon

The same [[desktop-core]] with a terminal in front of it.

It exists for two reasons: so the transport can be exercised with no desktop session and no
clipboard helper, and so **two devices can be run on one machine**, which is the only way to test a
third device without a third piece of hardware.

```bash
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj
```

## The shell

Read from `src/LinuxDaemon/Shell.cs`, which carries more than the root docs list:

| Command | |
|---|---|
| `pair`, `uri` | Show the pairing code, and the raw `meshsync://` URI |
| `join`, `confirm`, `reject`, `forget` | The other half of pairing, and revoking |
| `peers`, `status` | Who is paired, and what is connected over what |
| `send`, `clip`, `clipset` | Send text, send the clipboard, set the clipboard |
| `ring`, `unring` | [[find-my-device]], both ways |
| `bt`, `bluetooth` | Radio state |
| `transport` | `both`, `wifi` or `ble`, applied without a restart |
| `name` | The [[mesh-name]] |
| `help`, `quit` | |

`TerminalQr.cs` draws the [[pairing]] code in the terminal.

## The flags

`--data`, `--port`, `--name`, `--no-shell`, `--quiet`, `--help`.

**`--no-shell`** holds the links open with nobody to take commands from.
That is what a service manager wants, and what to reach for when driving it from a script.

**`--data` and `--port` together run a second device on one machine.**
Both are needed: two devices can share neither a data directory nor a listening port.

```bash
dotnet run --project src/LinuxDaemon/LinuxDaemon.csproj -- --data ~/dev2 --port 45002
```

This setup is what found the `MeshLinks` port bug: the second device dialled its peer's bare
address on its own listening port, which is itself, and logged
`Refusing a connection from this device's own identity` in a loop.
See [[wifi-tier]].

## The worst finding in the project lives here

`Shell.ReadLineOnItsOwnThread` exists because **awaiting a `Console.In` read wedged the D-Bus
thread and killed the entire Bluetooth tier, silently, forever**.
The full explanation is in [[desktop-core]], and it is the first thing to read if the Bluetooth
tier ever appears to stop finding devices for no reason.

`--no-shell` and `--quiet` change the timing enough to hide it, which is what made it look
intermittent.

## See also

[[desktop-core]] · [[desktop-shell]] · [[dbus-ipc]] · [[transport-preference]]
