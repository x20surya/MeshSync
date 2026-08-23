---
type: mechanism
status: partial
platforms: [windows, android, linux]
tier: n/a
code:
  - src/CoreLib/Identity/IKeyProtector.cs
  - src/WinDaemon/WindowsKeyProtector.cs
  - src/DesktopCore/Platform/SecretServiceKeyProtector.cs
updated: 2026-08-23
---

# Key at rest

The [[device-identity]] private key is wrapped before it reaches the disk.
`IKeyProtector` is the seam, and each platform supplies one.

## Which platform uses what

| Platform | Mechanism | File |
|---|---|---|
| Windows | DPAPI | `src/WinDaemon/WindowsKeyProtector.cs` |
| Android | a Keystore-held AES key | in the Android project |
| Linux | the desktop keyring, over `org.freedesktop.secrets` | `src/DesktopCore/Platform/SecretServiceKeyProtector.cs` |
| macOS | **none** | the Keychain equivalent belongs with the rest of the Mac work |

On Linux a 32-byte key lives in the keyring - KWallet on KDE, gnome-keyring on GNOME - and the
device key is sealed with it using the project's own AES-256-GCM.
That is deliberate: the blob then matches what Android writes, and `DeviceIdentity` already reads
it.

A machine with no keyring falls back to an unwrapped key rather than refusing to start, and a key
written before this existed upgrades itself on the next run without costing a re-pair.
A wrapped key begins `MSK1`.

## The known gap, stated plainly

**The key is wrapped, not hardware-generated.**
DPAPI and the Keystore both keep the private key off the disk in the clear, and it still exists in
process memory, so code already running as this user can read it while the app is up.

Fixing that means generating the key inside the Keystore and doing ECDH through `KeyAgreement`,
which forks the key agreement between platforms.
**Not worth it for a clipboard, and worth it for a vault** - so it is carried by
[[password-vault]] rather than scheduled on its own.

## What is still open

**The keyring is only exercised against KWallet.**
gnome-keyring serves the same interface and has not been tried.

## The finding that cost the most

D-Bus aligns every dict entry and every struct to eight bytes and nothing here does it for you.
A hand-rolled `a{ss}` with two pairs is malformed, and `dbus-daemon` answers a malformed message
by closing the connection with nothing said.
Both directions are avoided rather than solved: every dictionary written here holds exactly one
entry with a second property set afterwards through the Properties interface, and the secret is
read with `Secret.Item.GetSecret`, which returns the struct alone at the start of the body where
it is aligned already.

The same trap governs [[dbus-ipc]], which learned it from here.

## See also

[[device-identity]] · [[session-keys]] · [[password-vault]] · [[dbus-ipc]]
