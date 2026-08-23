---
type: meta
updated: 2026-08-23
---

# Note schema

Every note in `features/`, `mechanisms/` and `heads/` carries this frontmatter.
Obsidian shows it in the properties panel, and a Base can query it.

```yaml
---
type: feature | mechanism | head
status: shipped | partial | in-flight | planned
platforms: [windows, android, linux, macos]
tier: wifi | ble | either | n/a
code:
  - src/CoreLib/Transport/FileTransferService.cs
updated: 2026-08-23
---
```

## The fields

**`type`** decides which folder it lives in.
A *feature* is something the owner of the device can point at.
A *mechanism* is something that has to exist for a feature to work, and that nobody asked for.
A *head* is one of the five runnable applications.

**`status`** is about this repo, not about the roadmap.

| Value | Means |
|---|---|
| `shipped` | Built, tested, and exercised on hardware |
| `partial` | Built, but missing on a platform or unverified in a way that matters |
| `in-flight` | Being written right now, possibly uncommitted |
| `planned` | Designed and deliberately not built |

**`platforms`** lists only the heads where the thing actually works.
Not where it compiles.
Not where it is stubbed.
This field is the most valuable one in the vault and the easiest to let rot.

**`tier`** is which transport carries it, and follows the arithmetic in
[[bluetooth-tier]]: at about 6.7 KB/s Bluetooth carries anything small and nothing large.

**`code`** is the two or three files that would come up in a code review of this thing.
Not every file that mentions it.
