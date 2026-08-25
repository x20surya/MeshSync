---
type: reference
status: shipped
platforms: [linux]
tier: n/a
code:
  - packaging/apt-repo.sh
  - .github/workflows/apt.yml
updated: 2026-08-25
---

# APT repository

`http://x20surya.me/MeshSync` - the `.deb` from the last three releases, indexed and
signed, so a Debian or Ubuntu machine installs and **upgrades** Mesh Sync like anything else.

```bash
sudo install -d -m 0755 /usr/share/keyrings
curl -fsSL http://x20surya.me/MeshSync/meshsync.gpg \
  | sudo tee /usr/share/keyrings/meshsync.gpg > /dev/null

echo "deb [arch=amd64 signed-by=/usr/share/keyrings/meshsync.gpg] http://x20surya.me/MeshSync stable main" \
  | sudo tee /etc/apt/sources.list.d/meshsync.list > /dev/null

sudo apt update && sudo apt install meshsync
```

## Why the URL is that, and why it is http

The account's Pages site carries a custom domain, `x20surya.me`, so **every project site under the
account is served from `x20surya.me/<repo>`** rather than `x20surya.github.io/<repo>` - and the
github.io address 301s to it, so writing that one into a `sources.list` buys a redirect and
nothing else.

It is `http` because GitHub has not issued a certificate for the domain. The DNS is correct - apex
`A` records on the four Pages addresses, `www` a CNAME to the apex - but the certificate actually
served on `x20surya.me:443` is still `*.github.io`, so `https` fails to verify. **Removing and
re-adding the custom domain** in the user site's Pages settings re-triggers issuance; once
`https://x20surya.me` verifies, set `APT_BASE_URL` and change the one line in the README.

**`http` is not a hole here.** apt authenticates a repository by the GPG signature over `Release`,
not by the transport, which is why Debian's own mirrors are `http` to this day. What plain `http`
costs is privacy: somebody watching the network can see that this machine fetched `meshsync`. It
cannot let them change what it fetched.

## Why a repository and not a download link

A `.deb` attached to a release installs once and is never heard from again. Nothing tells the
machine a newer one exists, so every upgrade is a person remembering to go and look - and the
people least likely to remember are the ones running the oldest build.

A repository is the same package plus the two files that make `apt upgrade` work: an index of what
is available, and a signature over that index.

## What is published

| | |
|---|---|
| Suite / component | `stable` / `main` |
| Architecture | `amd64` only - that is the only one `packaging/build.sh` produces |
| Contents | the `.deb` from the **last three releases**, so a pin to an older version still resolves |
| Signature | `InRelease` and `Release.gpg`, both, because both are still met in the wild |
| Key | `meshsync.gpg` at the root, **dearmored** - the form `signed-by=` wants |

Handing people an armored `.asc` and telling them to put it in `/usr/share/keyrings` is the most
common way this is got wrong, and it fails with a message about a missing key rather than about a
wrong format.

## It is called by the release, not triggered by it

`release.yml` calls `apt.yml` as a reusable workflow once the release exists and its assets are
attached.

**`release: [published]` does not work here**, and fails in the worst possible shape. The release
is created by `release.yml` using `GITHUB_TOKEN`, and GitHub deliberately does not let a
token-raised event start another workflow - it is their loop guard. So the apt job simply never
ran: the release page showed the new version while the repository quietly kept serving the
previous one, with nothing failing anywhere. Caught on v0.5.1 by checking the run list rather than
by anything going red.

## Rebuilt, never appended to

`.github/workflows/apt.yml` derives the whole repository from GitHub Releases on every run: it
downloads the `.deb` from the most recent three and builds from scratch. Nothing accumulates,
nothing drifts, and a repository that has gone wrong is fixed by running the workflow again rather
than by working out what state it got into.

It deploys as a **Pages artifact**, not a commit to a `gh-pages` branch. A 33 MB `.deb` per release
committed to a branch is 33 MB in the repository's history for ever.

**Bandwidth is the limit worth knowing.** GitHub Pages is a soft 100 GB/month and a 1 GB site.
Three releases is about a hundred megabytes of site, which is fine; at roughly 33 MB a download,
sustained popularity is what would eventually need a real mirror.

## `packaging/apt-repo.sh`

The whole of the publishing logic, so it can be run against a throwaway key on a laptop and the
result checked with a real `apt` before it is ever pointed at the real one. **A publishing step
that can only be exercised by publishing is a publishing step nobody can test.**

```bash
gpg --quick-generate-key "Test <test@example.invalid>" rsa2048 sign never
packaging/apt-repo.sh path/to/debs /tmp/repo --key test@example.invalid
```

**It does not use `apt-ftparchive`.** That tool lives in `apt-utils`, which is not installed on a
plain desktop, so the publishing step could only ever have been exercised on a CI runner - which
is the one place a mistake is expensive. A binary `Packages` stanza is the package's own control
block plus where it is, how big it is and what it hashes to; `dpkg-deb` already reads the first
part, and there is no third-party format left to get wrong.

## Three ways this goes wrong quietly

**`Filename:` is relative to the repository root**, because that is what apt joins onto the URL in
`sources.list`. An absolute path indexes perfectly and downloads nothing - `apt update` succeeds
and `apt install` 404s. The script generates it with `os.path.relpath` against the output root for
that reason.

**apt silently ignores an index whose checksum is not in `Release`**, and refuses a repository
that does not declare the architectures and components it carries. Either way the package simply
is not there, with no error naming the cause.

**A passphrase-protected key cannot be used unattended** without loopback pinentry. The failure is
gpg asking for input on a runner where nothing can answer, so the job hangs rather than fails.

The workflow's last step before deploying is a real `apt-get update`, `apt-cache policy` and
`apt-get download` against the tree it is about to publish, in a sandbox. All three of the above
are caught there rather than by a person whose `apt install` 404s.

## The signing key

```
64B7 9912 F802 21C1 0E3A 341D 4C84 A1AE A04A B302
Mesh Sync <suryanshuc659@gmail.com>   rsa4096, no expiry
```

Check what you downloaded against that before trusting it:

```bash
gpg --show-keys --with-fingerprint /usr/share/keyrings/meshsync.gpg
```


Held as the `APT_GPG_PRIVATE_KEY` repository secret, with `APT_GPG_PASSPHRASE` if it has one. The
workflow refuses to publish without it rather than producing an unsigned repository, which every
apt client rejects anyway.

**It is the root of trust for everyone who installs this.** Losing it means every user has to
remove the old key and add a new one by hand; leaking it means somebody else can serve them
packages. It belongs in a password manager, not only in a repository secret.

## See also

[[installing]] · [[building]] · [[testing]] · [[desktop-shell]] · [[linux-daemon]]
