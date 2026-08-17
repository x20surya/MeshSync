# Security

Mesh Sync moves your clipboard between your own devices with no server in the middle.
This document says what that does and does not protect you from, in the terms an attacker would
use rather than the ones marketing would.

If you find something wrong here, please open a GitHub issue.
There is no bug bounty and no formal disclosure process.

## What it is built out of

- **Identity.** Each device holds a P-256 keypair. Its identity is the SHA-256 fingerprint of the
  public key. Nothing else identifies a device: not its name, not its address.
- **Session keys.** Each connection agrees its own AES-256 key. Both ends mint an ephemeral P-256
  keypair, announce it in the hello, and mix two ECDH secrets through HKDF-SHA256:

  ```
  key = HKDF-SHA256(
      ikm  = ECDH(ephemeral_local, ephemeral_peer)   // forward secrecy
          || ECDH(static_local,    static_peer),     // authentication
      salt = sorted(fingerprint_local, fingerprint_peer),
      info = "MeshSync/session-key/v2")
  ```

  This is the shape of the Noise framework's `KK` handshake. It is not a Noise implementation and
  does not claim to be one - it is written this way so a reviewer can check it against a known
  pattern instead of assessing an invention. It is the first thing worth auditing.
- **Payloads.** AES-256-GCM. Decryption failing is the same statement as authentication failing;
  a payload that will not open is dropped rather than acted on.
- **Storage.** The private key is wrapped before it reaches the disk: DPAPI at `CurrentUser` scope
  on Windows, an AES key held in the Android Keystore on Android.

## What it protects against

- **Reading your clipboard off the wire.** Both tiers carry the same encrypted payload. Bluetooth
  is not a weaker path; it carries the same bytes.
- **A device you have not paired with.** A listener refuses any peer whose key it does not hold,
  before any payload is processed.
- **One paired device reading another pair's traffic.** Keys are per connection, so a device that
  is in your mesh still cannot read what two other devices send each other.
- **Traffic captured today being read later.** Ephemeral keys are destroyed with the connection.
  Recovering a device's private key afterwards does not open sessions that have already closed.
- **A key file copied off the machine.** Wrapped to the user on Windows and to the Keystore on
  Android, so the bytes alone are not enough.

## What it does not protect against

These are real and stated deliberately.

- **Code already running as you.** On Windows, anything running as the signed-in user can ask
  DPAPI to unwrap the key, exactly as the app does. On Android the key is unwrapped in process
  memory while the app runs. Neither is hardware-generated. This is the largest remaining gap and
  the reason the password vault is not built yet.
- **Someone at your unlocked device.** Pairing needs physical presence at the screen showing the
  code, which is also all it needs.
- **Traffic analysis.** The size and timing of what you copy are visible to anyone watching the
  network or the radio, even though the contents are not.
- **A malicious paired device.** Pairing is a full trust decision. A device in your mesh receives
  everything you copy. There is no partial trust and no per-device permission.
- **Denial of service.** Anything within Bluetooth range can occupy the GATT server or make
  connection attempts. Nothing rate-limits that.

## Pairing, and the thing it used to get wrong

Pairing shows a QR code containing an address, a public key and the mesh name. One device scans
it. That authenticates the device being scanned, and says nothing about the device doing the
scanning.

Early versions treated the code being on screen as the whole check: while it was up, the first
stranger to connect was trusted. That loses to anyone already on the network who wins the race to
connect.

It now takes two steps. Showing the code says somebody was invited. Comparing the four-group
fingerprint shown on both devices says it is the right somebody. Winning the race no longer helps,
because the attacker's fingerprint is not the one on the other screen.

## Known weaknesses in the history

Development versions before the identity work derived every key from a literal
`DeriveKey("MasterPassword123", "Salt")`, which is still visible in the git history. Every install
of those builds shared one key and the listener accepted anything that reached it.

It is left in history on purpose. A security project that force-pushes its past away invites more
suspicion than one that points at the commit where it was fixed, and no key that ever protected
real user data is involved - there were no users.

## Reporting

Open an issue on GitHub. Please include what you were doing, what you expected, and what happened.
Log files live at `%LOCALAPPDATA%\MeshSync\daemon.log` on Windows and under `adb logcat -s MeshSync`
on Android; they contain device names and fingerprints but never clipboard contents.
