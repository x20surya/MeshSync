---
type: reference
status: shipped
platforms: [windows, android, linux]
tier: n/a
code:
  - packaging/site.sh
  - packaging/site/index.html
  - .github/workflows/apt.yml
updated: 2026-08-27
---

# Download page

`https://x20surya.me/MeshSync/` - one page, one job: get the right file onto the machine of
whoever is reading it.
Live since 2026-08-26. `x20surya.me/meshsync` reaches it too, by a redirect, for the reason below.
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

`--check` asks for **the first byte** of all seven URLs the page advertises.

Not a plain `GET`: that downloads every asset the page names, about 210 MB for a release, on every
publish, to look at three digits and throw the body away.
Not `HEAD` either, because the release URL redirects to object storage and a store that declined
`HEAD` would fail this for a file that downloads perfectly.
A served range answers `206` and a server that ignores `Range` answers `200`; both mean the object
is there and readable, so both pass, and a missing one still answers `404`.
Seven links take about five seconds.
The page names its assets by hand, so a rename in `release.yml` that nobody carried across
produces a page that looks perfect and downloads nothing; CI runs with `--check` for that reason
and refuses to publish a page with a dead link in it.

**Which means a new artifact goes onto the page in the same change that starts building it, and
the page is not republished until a release actually carries it.** `--check` runs against the
newest release, so a page naming a file that release does not have fails - correctly. That is the
one thing to know before adding a download: the `v*` tag comes first, and republishing the site on
its own in between will stop.

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

## The path is case-sensitive, and lowercase is what people type

GitHub Pages serves a project site at the repository's name **spelled exactly as the repository
spells it**.
The repository is `MeshSync`, so `/MeshSync/` answers and `/meshsync/` and `/Meshsync/` both
return GitHub's own 404 page - which reads as "the site is down" rather than as "you typed it in
the wrong case".

`x20surya.me/meshsync` works anyway, because `meshsync/index.html` in the **user site repo**
(`x20surya/x20surya.github.io`) redirects to `/MeshSync/`.
That is the non-obvious part: the user site answers for every path under the domain that no
project repository has claimed, so it can hold an alias for one that has.

**It is deliberately not a rename.**
Renaming the repository to `meshsync` would fix the case in one step and move the Pages path with
it, and GitHub does not redirect an old project path.
Every `sources.list` already written says `/MeshSync`, so the rename that saves one redirect file
breaks `apt update` on every machine that has installed the `.deb`.

**The redirect is for browsers only.**
It is a `<meta refresh>` with a `location.replace` behind it, and apt can follow neither.
The canonical apt URL stays `/MeshSync`, which is what every published snippet says.

A product domain removes the whole class of problem, because there is no path left to get wrong.

## The one thing it promises that CI has to keep

The page links `SHA256SUMS`, which `release.yml` attaches from `sha256sum` over the built
artifacts.
Telling somebody to click through a security warning without giving them a way to check the file
first is not advice.

Releases made before 2026-08-26 have no such file, so `--check` fails against them and the publish
stops rather than shipping a page whose checksum link is a 404.

**Backfill by hashing what is already published, not by re-running the Release workflow.**
A re-run rebuilds, and a rebuild is not bit-identical, so the checksums it attaches would describe
bytes that nobody downloaded - every copy taken before the re-run would now fail verification
against the file that claims to describe it.

```bash
gh release download <tag> --repo x20surya/MeshSync --pattern 'MeshSync-*'
sha256sum MeshSync-* > SHA256SUMS
gh release upload <tag> SHA256SUMS --repo x20surya/MeshSync --clobber
```

v0.5.1 was backfilled that way on 2026-08-26.
