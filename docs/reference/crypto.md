---
type: reference
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/CryptoEngine.cs
  - src/CoreLib/Identity/SessionKeys.cs
  - src/CoreLib/Identity/DeviceIdentity.cs
updated: 2026-08-23
---

# Cryptography

Exact parameters, read from the source.
`SECURITY.md` is the threat model; this is the mechanism.

## The envelope

AES-256-GCM. `src/CoreLib/CryptoEngine.cs`.

```
[nonce 12B][tag 16B][ciphertext...]
```

| Constant | Value |
|---|---|
| `KeySize` | 32 (256 bit) |
| `NonceSize` | 12 (96 bit) |
| `TagSize` | 16 (128 bit) |
| `Overhead` | 28 |
| `TaggedOverheadBytes` | 29 (`Overhead` + the content type byte) |

`EncryptTagged` puts the one-byte content type **inside** the ciphertext, so the type itself is
authenticated. `TaggedOverheadBytes` is exposed so a caller can size a payload before it knows
which key it will be encrypted with, which matters now that the key depends on the peer.

Everything is written straight into the output buffer.
The previous implementation allocated separate nonce, tag and ciphertext arrays and then copied
all three, which cost three extra full-size copies of every screenshot.
The intermediate tagged plaintext is wiped with `CryptographicOperations.ZeroMemory` in a
`finally`.

## Key agreement

`src/CoreLib/Identity/SessionKeys.cs`. **One key per connection.**

```
key = HKDF-SHA256(
    ikm  = ECDH(ephemeral_local, ephemeral_peer)   // forward secrecy
        || ECDH(static_local,    static_peer),     // authentication
    salt = sorted(fingerprint_local, fingerprint_peer),
    info = "MeshSync/session-key/v2")
```

Curve **P-256** (`ECCurve.NamedCurves.nistP256`) throughout, both static and ephemeral.

This is the shape of Noise's `KK` handshake, deliberately, so it can be reviewed against a
known-good pattern rather than assessed as a bespoke invention.
It is **not** a full Noise implementation and does not claim to be.

An attacker can complete the ephemeral half with anybody, because it is unauthenticated by
construction. They cannot complete the static half without a private key this device has paired
with, so the two ends derive different keys and AES-GCM refuses the payload.

**The context string binds the derivation to this app**, so a secret agreed here cannot be replayed
into another protocol using the same curve and keys.
The `v2` marks the move from static-static to the mixed agreement.

**Sort the fingerprints into the salt.** Unsorted, the two ends mix the same bytes in different
orders, derive different keys, and every payload fails to decrypt with nothing on the wire to say
why.

Both raw secrets and the concatenated material are zeroed in a `finally`.

## Identity

`src/CoreLib/Identity/DeviceIdentity.cs`.

- Keypair: P-256 ECDH, persisted as PKCS#8.
- Public key: base64 SubjectPublicKeyInfo, about 120 bytes.
- **Fingerprint: SHA-256 of the base64 public key string, lowercase hex.**
  Note it hashes the base64 text, not the raw DER.
- Short form: first 16 hex characters, grouped in fours, uppercased. `AC83-492B-684F-4263`.

`RawSecretWith` is `internal` **on purpose**. On its own it is exactly the static-static agreement
that had no forward secrecy, so nothing outside `SessionKeys` may reach it.

## Key at rest

A wrapped key file begins with the four bytes `MSK1` (`ProtectedMagic`).

Without that marker a protected blob and a legacy plaintext PKCS#8 key are told apart only by
trying to parse one as the other, which works right up until a wrapped blob happens to parse and
the device silently adopts a garbage identity.

`IKeyProtector` has three outcomes and they are not the same:

| Situation | Result | May the file be replaced? |
|---|---|---|
| Unwrapped fine | Identity loaded | n/a |
| Wrapped, no protector present | Refuse | **No** |
| Wrapped, unwrap failed | New identity | Yes |
| Unwrapped plaintext, protector present | Load, then rewrite wrapped in place | No re-pair |

The "no" row is the one a test caught.
The first attempt returned null for both, so a Keystore briefly unavailable would overwrite a
working identity with a fresh one and silently cost every pairing on the device.
When it refuses, the run continues on a temporary identity that syncs with nobody, which is loud.

The key file is written to `path + ".tmp"` and moved into place, so an interrupted write cannot
leave a half-file that reads as a corrupt identity.
`peers.json` is written the same way.
On non-Windows it is chmod 600; on Windows it inherits the user-private profile ACL.

## Argon2id

Present for [[password-vault]] and used by nothing today.

| Parameter | Value |
|---|---|
| Memory | 65536 KB (64 MB) |
| Iterations | 3 (OWASP) |
| Parallelism | 4 |
| Output | 32 bytes |

Roughly 200 ms. **Never call it on a UI thread.**

## The two standing prohibitions

Both are in `AGENTS.md` because both have been broken before.

**Never reintroduce a key that is not agreed per connection.**
`DeriveKey("MasterPassword123", "Salt")` made every install of this app interchangeable.
A per-pair key that never changes made every past session recoverable from one stolen private key.

**Do not cache a session key against a peer.**
It belongs to the connection.
`PeerSession.IsUsable` asks the registry on **every payload**, because a session holds its own copy
of the key and a forgotten device would otherwise keep working until its link happened to drop.

## See also

[[session-keys]] · [[device-identity]] · [[key-at-rest]] · [[protocol-tcp]] · [[protocol-ble]]
