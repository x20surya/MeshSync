---
type: mechanism
status: shipped
platforms: [windows, android, linux, macos]
tier: n/a
code:
  - src/CoreLib/Identity/SessionKeys.cs
  - src/CoreLib/Identity/PeerSession.cs
  - src/CoreLib/CryptoEngine.cs
updated: 2026-08-23
---

# Session keys

**There is one key per connection.**
Not per pair, and certainly not per mesh.
This is the single most load-bearing rule in the project and the one with the most scar tissue
behind it.

## How the agreement works

Each end mints an ephemeral P-256 keypair, announces it in the hello, and the session key mixes
**two** ECDH secrets through HKDF:

| Secret | Gives |
|---|---|
| ephemeral to ephemeral | forward secrecy |
| static to static | authentication |

An attacker can complete the first with anybody, because it is unauthenticated by construction.
They cannot complete the second without a private key this device has paired with.
So the two ends never agree, and AES-256-GCM refuses the payload.

This is the shape of Noise's `KK` handshake, deliberately, so it can be read against a known
pattern rather than assessed as an invention.

## Where it lives

- `src/CoreLib/Identity/SessionKeys.cs` - the agreement.
- `src/CoreLib/Identity/PeerSession.cs` - one connection and the key that belongs to it.
- `src/CoreLib/CryptoEngine.cs` - AES-256-GCM, and Argon2id for [[password-vault]].

**`PeerSession` is what a session key belongs to.**
Disposing it is what makes that traffic unrecoverable, so a link that closes takes its key with it.

## The two prohibitions

Both of these are in `AGENTS.md` because both have been broken before.

**Never reintroduce a key that is not agreed per connection.**
A single shared key made every install interchangeable.
A per-pair key that never changes made every past session recoverable from one stolen private key.

**Do not cache a session key against a peer.**
It belongs to the connection.
Caching it against the device is what removed forward secrecy the first time, and it also quietly
breaks revocation: the old key lived in a cache the registry could clear, so forgetting a device
stopped it syncing at once, whereas a session holding its own copy keeps working until its link
happens to drop.
`PeerSession.IsUsable` asks the [[peer-registry]] on every payload for exactly that reason.

## Findings worth knowing

**Sort the fingerprints before mixing them into the derivation.**
Unsorted, the two ends derive different keys from the same shared secret and every payload fails
to decrypt, with nothing on the wire to say why.

**Which key decrypts a payload is also the answer to who sent it.**
AES-GCM authenticates, so a payload that opens under a peer's key could only have come from that
peer.
That is what lets [[bluetooth-tier]] identify a sender without carrying identity in every frame.

## See also

[[device-identity]] · [[peer-registry]] · [[key-at-rest]] · [[wire-formats]]
`SECURITY.md` for the threat model, including what this does not protect against.
