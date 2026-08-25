#!/usr/bin/env bash
#
# Builds a signed APT repository out of a directory of .deb files.
#
# WHY THIS EXISTS RATHER THAN A DOWNLOAD LINK. A .deb attached to a release installs once and is
# never heard from again: nothing tells the machine a newer one exists, so every upgrade is a
# person remembering to go and look. An apt repository is the same package plus the two files
# that make `apt upgrade` work - an index of what is available, and a signature over that index.
#
# WHY IT IS A SCRIPT AND NOT ONLY A WORKFLOW. It is the whole of the publishing logic, so it can
# be run against a throwaway key on a laptop and the result checked with a real apt before it is
# ever pointed at the real one. A publishing step that can only be exercised by publishing is a
# publishing step nobody can test.
#
# Usage:
#   packaging/apt-repo.sh <deb-directory> <output-directory> [--key <fingerprint-or-email>]
#
# Signing key: taken from --key, or $APT_SIGNING_KEY, or the default gpg secret key.
set -euo pipefail

DEBS="${1:?usage: apt-repo.sh <deb-directory> <output-directory> [--key <id>]}"
OUT="${2:?usage: apt-repo.sh <deb-directory> <output-directory> [--key <id>]}"
shift 2

KEY="${APT_SIGNING_KEY:-}"
while [ $# -gt 0 ]; do
    case "$1" in
        --key) KEY="${2:?--key needs a value}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

# One suite and one component. A project with a single package and one architecture does not need
# the machinery for more, and every extra name is another thing a person has to type correctly
# into their sources.list.
SUITE=stable
COMPONENT=main
ARCH=amd64
ORIGIN="Mesh Sync"
LABEL="Mesh Sync"
DESCRIPTION="Local-first universal clipboard for your own devices"
SITE="https://github.com/x20surya/MeshSync"

# WHERE THIS REPOSITORY IS SERVED FROM, and why it is http.
#
# The account's Pages site carries a custom domain, x20surya.me, so every project site under it is
# served from x20surya.me/<repo> rather than x20surya.github.io/<repo> - and github.io 301s to it,
# so writing the github.io URL into a sources.list only buys a redirect.
#
# It is http because GitHub has not issued a certificate for that domain: the DNS is right (apex A
# records on the four Pages addresses) but the cert served is still *.github.io, so https fails to
# verify. Removing and re-adding the custom domain in the user site's Pages settings re-triggers
# issuance; when it works, set this to https and nothing else changes.
#
# http is not a hole here. apt authenticates a repository by the GPG signature over Release, not
# by the transport - which is why Debian's own mirrors are http. What plain http costs is privacy:
# somebody watching the network sees that this machine fetched meshsync.
BASE_URL="${APT_BASE_URL:-http://x20surya.me/MeshSync}"

command -v dpkg-deb >/dev/null 2>&1 || { echo "apt-repo.sh needs dpkg-deb." >&2; exit 2; }
command -v gpg      >/dev/null 2>&1 || { echo "apt-repo.sh needs gpg." >&2; exit 2; }
command -v python3  >/dev/null 2>&1 || { echo "apt-repo.sh needs python3." >&2; exit 2; }

ls "$DEBS"/*.deb >/dev/null 2>&1 || { echo "No .deb files in $DEBS." >&2; exit 2; }

# ─────────────────────────────────────────────────────────────── the pool
#
# pool/main/m/meshsync is where Debian itself would put it: component, then the first letter of
# the source name, then the source name. Nothing requires that layout - Packages records the real
# path - but a repository laid out the usual way is one a person can read.

POOL="$OUT/pool/$COMPONENT/m/meshsync"
DIST="$OUT/dists/$SUITE/$COMPONENT/binary-$ARCH"

rm -rf "$OUT"
mkdir -p "$POOL" "$DIST"
cp "$DEBS"/*.deb "$POOL/"

# ─────────────────────────────────────────────────────────────── Packages and Release
#
# WHY THIS IS NOT apt-ftparchive. That tool lives in apt-utils, which is not installed on a plain
# desktop - so the publishing step could only ever be exercised on a CI runner, which is the one
# place a mistake is expensive. A binary Packages stanza is the package's own control block plus
# where it is, how big it is and what it hashes to; dpkg-deb already reads the first part and
# there is no third-party format to get wrong.

python3 - "$OUT" "$SUITE" "$COMPONENT" "$ARCH" "$ORIGIN" "$LABEL" "$DESCRIPTION" <<'PYEOF'
import email.utils, gzip, hashlib, os, subprocess, sys

out, suite, component, arch, origin, label, description = sys.argv[1:8]

pool = os.path.join(out, "pool", component, "m", "meshsync")
dist = os.path.join(out, "dists", suite)
index_dir = os.path.join(dist, component, "binary-" + arch)

def digest(path, algorithm):
    h = hashlib.new(algorithm)
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()

stanzas = []
for name in sorted(os.listdir(pool)):
    if not name.endswith(".deb"):
        continue

    path = os.path.join(pool, name)
    control = subprocess.run(["dpkg-deb", "-f", path],
                             check=True, capture_output=True, text=True).stdout.strip()

    # Filename is relative to the repository root, because that is what apt joins onto the URL in
    # sources.list. An absolute path here indexes correctly and downloads nothing.
    relative = os.path.relpath(path, out)

    stanzas.append("\n".join([
        control,
        f"Filename: {relative}",
        f"Size: {os.path.getsize(path)}",
        f"MD5sum: {digest(path, 'md5')}",
        f"SHA1: {digest(path, 'sha1')}",
        f"SHA256: {digest(path, 'sha256')}",
    ]))

packages = os.path.join(index_dir, "Packages")
with open(packages, "w", encoding="utf-8") as f:
    f.write("\n\n".join(stanzas) + "\n")

with open(packages, "rb") as raw, gzip.GzipFile(packages + ".gz", "wb", mtime=0) as gz:
    gz.write(raw.read())

# The Release file. apt ignores an index whose checksum is not listed here, and refuses a
# repository that does not declare the architectures and components it carries - both quietly,
# which is why a repository can look perfect and install nothing.
lines = [
    f"Origin: {origin}",
    f"Label: {label}",
    f"Suite: {suite}",
    f"Codename: {suite}",
    f"Version: 1.0",
    f"Date: {email.utils.formatdate(usegmt=True)}",
    f"Architectures: {arch}",
    f"Components: {component}",
    f"Description: {description}",
]

indices = [os.path.join(component, "binary-" + arch, n) for n in ("Packages", "Packages.gz")]

for field, algorithm in (("MD5Sum", "md5"), ("SHA1", "sha1"), ("SHA256", "sha256")):
    lines.append(field + ":")
    for relative in indices:
        absolute = os.path.join(dist, relative)
        lines.append(f" {digest(absolute, algorithm)} {os.path.getsize(absolute):>16} {relative}")

with open(os.path.join(dist, "Release"), "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")

print(f"indexed {len(stanzas)} package(s)")
PYEOF

PACKAGE_COUNT="$(grep -c '^Package: ' "$DIST/Packages" || true)"

# ─────────────────────────────────────────────────────────────── the signature
#
# Both forms, because both are still met. InRelease is the signed-and-inline file apt prefers;
# Release.gpg is the detached signature older clients look for. Writing only one of them works
# until it does not.

# --pinentry-mode loopback is not optional on a machine with no terminal, and it is needed even
# for a key with NO passphrase: without it gpg still tries to reach a pinentry and fails with
# "Inappropriate ioctl for device", which reads like a permissions problem and is not one.
SIGN=(gpg --batch --yes --no-tty --armor --pinentry-mode loopback)
[ -n "$KEY" ] && SIGN+=(--local-user "$KEY")
[ -n "${APT_GPG_PASSPHRASE_FILE:-}" ] && SIGN+=(--passphrase-file "$APT_GPG_PASSPHRASE_FILE")

"${SIGN[@]}" --clearsign  --output "$OUT/dists/$SUITE/InRelease"    "$OUT/dists/$SUITE/Release"
"${SIGN[@]}" --detach-sign --output "$OUT/dists/$SUITE/Release.gpg" "$OUT/dists/$SUITE/Release"

# The public half, dearmored, because that is the form `signed-by=` wants. Handing people an
# armored .asc and telling them to put it in /usr/share/keyrings is the most common way this is
# got wrong, and it fails with a message about a missing key rather than a wrong format.
EXPORT=(gpg --export)
[ -n "$KEY" ] && EXPORT+=("$KEY")
"${EXPORT[@]}" > "$OUT/meshsync.gpg"

[ -s "$OUT/meshsync.gpg" ] || { echo "The exported public key is empty - is $KEY a key this gpg has?" >&2; exit 1; }

# ─────────────────────────────────────────────────────────────── the landing page
#
# Somebody who pastes the repository URL into a browser gets the two commands rather than a
# directory listing or a 404, because that URL is what ends up in an issue thread.

NEWEST="$(ls -1 "$POOL"/*.deb | sed 's|.*/meshsync_||; s|_amd64\.deb$||' | sort -V | tail -1)"

cat > "$OUT/index.html" <<HTMLEOF
<!doctype html>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mesh Sync &middot; APT repository</title>
<style>
  :root { color-scheme: light dark; }
  body { max-width: 46rem; margin: 4rem auto; padding: 0 1.5rem;
         font: 16px/1.6 system-ui, sans-serif; }
  h1 { font-size: 1.6rem; margin-bottom: .2rem; }
  p.sub { color: #77726A; margin-top: 0; }
  pre { background: #F7F6F3; color: #262523; border: 1px solid #DEDBD4; border-radius: 4px;
        padding: 1rem; overflow-x: auto; font-size: .85rem; }
  @media (prefers-color-scheme: dark) {
    pre { background: #232120; color: #F0EEE9; border-color: #37342F; }
    p.sub { color: #A29C93; }
  }
  code { font-size: .9em; }
  a { color: #2F7A6B; }
</style>

<h1>Mesh Sync &middot; APT repository</h1>
<p class="sub">Current version $NEWEST &middot; amd64 &middot; <a href="$SITE">source on GitHub</a></p>

<p>Add the signing key and the repository, then install:</p>

<pre>sudo install -d -m 0755 /usr/share/keyrings
curl -fsSL $BASE_URL/meshsync.gpg \
  | sudo tee /usr/share/keyrings/meshsync.gpg > /dev/null

echo "deb [arch=amd64 signed-by=/usr/share/keyrings/meshsync.gpg] $BASE_URL stable main" \
  | sudo tee /etc/apt/sources.list.d/meshsync.list > /dev/null

sudo apt update
sudo apt install meshsync</pre>

<p>After that <code>sudo apt upgrade</code> picks up new releases like anything else.</p>

<p>The key you just added should be this one:</p>
<pre>64B7 9912 F802 21C1 0E3A 341D 4C84 A1AE A04A B302
Mesh Sync &lt;suryanshuc659@gmail.com&gt;</pre>
<p>Check with
<code>gpg --show-keys --with-fingerprint /usr/share/keyrings/meshsync.gpg</code>.</p>

<p>The clipboard needs no helper on Wayland &mdash; Mesh Sync speaks
<code>ext-data-control</code> to the compositor itself. On X11 install
<code>xclip</code> or <code>wl-clipboard</code>, which the package recommends.</p>

<p>To remove it:</p>

<pre>sudo apt remove meshsync
sudo rm /etc/apt/sources.list.d/meshsync.list /usr/share/keyrings/meshsync.gpg</pre>
HTMLEOF

echo "Built $SUITE/$COMPONENT/$ARCH with $PACKAGE_COUNT package(s) into $OUT"
