---
type: reference
status: shipped
platforms: [windows, android, linux]
tier: n/a
code:
  - packaging/site.sh
  - packaging/site/index.html
  - .github/workflows/apt.yml
updated: 2026-08-26
---

# Download page

`https://x20surya.me/MeshSync/` - one page, one job: get the right file onto the machine of
whoever is reading it.
It is the root of the same Pages site the [[apt-repository]] is published from, rendered by
`packaging/site.sh` straight after `packaging/apt-repo.sh` has built the repository under it.

## Why it exists

Nine releases had reached thirty downloads between them.
Every binary was already being built, for five targets, by CI, and attached to a public release -
and the only address there was to hand anybody was a repository URL.
A repository is not a thing a person installs from.

The page is the one URL that can go in a post, a directory listing, a package manifest or an issue
reply.

## What it does that a README cannot

**It names the file for the visitor's own platform.**
A small script marks the matching card and points the button at it, from `navigator.userAgent`.
Every card is a working link before that runs, so a browser with no JavaScript loses a highlight
and nothing else.

**It says the Windows binary is unsigned, before the download rather than after.**
SmartScreen shows "Windows protected your PC" on a full blue screen, and the run control is hidden
behind **More info**.
Told beforehand, with the reason and a way to check the file, that warning costs a sentence.
Discovered afterwards, it costs the install - and it is the single most likely place a first-time
user gives up.

**It publishes the platform matrix rather than implying it.**
The table is [[platform-matrix]] reduced to what an installer needs to know, including the things
that do not work: Android cannot send the clipboard by itself, Linux clipboard support is partial,
macOS is not shipped at all.

## How the version gets in

Baked in at publish time, not fetched in the browser.

GitHub serves `/releases/latest/download/<name>`, which would need no version at all - but every
asset here is named after its tag, so "latest" cannot be used without already knowing the tag.
Asking `api.github.com` from the visitor's browser would work, and would also break the download
button for anyone whose network blocks it, which is precisely the audience this project is for.

The page is republished on every release anyway, by the same workflow that republishes the
repository, so a baked-in version is never stale for longer than a repository index is.

## Running it

```bash
packaging/apt-repo.sh <debs> site     # the repository, into site/ - wipes site/ first
packaging/site.sh site                # the page, into site/index.html
packaging/site.sh site --check        # ...and follow every download link it names
```

`--check` HEADs all six URLs the page advertises.
The page names its assets by hand, so a rename in `release.yml` that nobody carried across
produces a page that looks perfect and downloads nothing; CI runs with `--check` for that reason
and refuses to publish a page with a dead link in it.

`SITE_BASE_URL` and `SITE_REPO_URL` override where the page thinks it is being served from.
Moving to a product domain is those two variables and a `CNAME`, and is the whole of what that
move costs here.

## Where the two pages sit

| | |
|---|---|
| `/` | this page |
| `/apt/` | the repository's own page - the key, the two commands, and the fingerprint |
| `/dists/`, `/pool/`, `/meshsync.gpg` | the repository itself, read by apt and by nothing else |

**The repository URL did not move when this page took the root.**
apt reads `dists/` and `pool/`; a `sources.list` written against any earlier version still
resolves, which is the only reason the root was free to take.

## The one thing it promises that CI has to keep

The page links `SHA256SUMS`, which `release.yml` attaches from `sha256sum` over the built
artifacts.
Releases made before 2026-08-26 have no such file, so `--check` fails against them - re-run the
Release workflow against that tag and it re-uploads with `--clobber` and attaches one.

Telling somebody to click through a security warning without giving them a way to check the file
first is not advice.
