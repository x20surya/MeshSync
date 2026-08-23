---
type: feature
status: planned
platforms: []
tier: n/a
code: []
updated: 2026-08-23
---

# Password vault

Planned, designed, and deliberately not built.

## The gate

**It does not start unless Android autofill and a desktop browser extension are being built too.**
Without those it is not a password manager, it is a synchronised text file, and shipping that
would be worse than shipping nothing.

## Why it is architecturally separate

`AGENTS.md` names this as the second of two distinct sync engines, and keeping them apart is a
standing rule.

| | [[clipboard-sync]] | Password vault |
|---|---|---|
| Lifetime | Ephemeral | Persistent |
| Storage | None, ever | SQLite |
| Conflicts | Impossible, nothing is merged | CRDTs with logical clocks |

**Do not add SQLite or CRDTs to the clipboard path.**
That is the rule this note exists to carry.

## What it would change about the crypto

[[key-at-rest]] records that the identity key is wrapped rather than hardware-generated, which
means it still exists in process memory while the app is up.
Fixing that means generating the key inside the Android Keystore and doing ECDH through
`KeyAgreement`, which forks the key agreement between platforms.

That trade is explicitly not worth it for a clipboard **and is worth it for a vault**.
So this feature carries that work with it.

Argon2id is already in `src/CoreLib/CryptoEngine.cs` for exactly this.

## See also

[[key-at-rest]] · [[session-keys]] · [[clipboard-sync]]
