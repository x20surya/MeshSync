#!/usr/bin/env bash
#
# Renders the download page into the directory the apt repository was built into, so the Pages
# artifact carries both: a landing page at the root, and the repository under it.
#
# WHY THERE IS A PAGE AT ALL. Nine releases reached thirty downloads. Every binary was already
# built, signed by nothing and linked from nowhere: the only address to hand anybody was a
# repository, and a repository is not a thing a person installs from. The page exists so there is
# one URL to put in a post, a listing or an issue reply.
#
# WHY IT IS A TEMPLATE AND A SCRIPT RATHER THAN A GENERATOR. The page is a page - it is checked in
# as openable HTML at packaging/site/index.html and can be edited by looking at it. All this does
# is fill in the version, because the version is the only part that changes per release and the
# one part nobody should be editing by hand at three in the morning.
#
# WHY THE VERSION IS BAKED IN RATHER THAN FETCHED. GitHub publishes /releases/latest/download/NAME,
# but every asset here is named after its tag, so "latest" cannot be used without already knowing
# the tag. Asking the API from the visitor's browser would work and would also mean the download
# button is broken for anyone whose network blocks api.github.com - which is exactly the audience
# this project is for. The page is republished on every release anyway.
#
# Usage:
#   packaging/site.sh <output-directory> [--version vX.Y.Z] [--date YYYY-MM-DD] [--check]
#
#   --check   HEADs every download link. Needs network, and needs the release to exist, so it is
#             for running by hand or after publishing rather than as part of the build.
set -euo pipefail

OUT="${1:?usage: site.sh <output-directory> [--version vX.Y.Z] [--date YYYY-MM-DD] [--check]}"
shift

VERSION="${SITE_VERSION:-}"
DATE=""
CHECK=no

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
        --date)    DATE="${2:?--date needs a value}";       shift 2 ;;
        --check)   CHECK=yes;                               shift   ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="$HERE/site/index.html"
[ -f "$TEMPLATE" ] || { echo "site.sh cannot find $TEMPLATE." >&2; exit 2; }

# The same address the apt repository is served from, for the same reason: the account's Pages
# site carries x20surya.me, so every project site under it answers on x20surya.me/<repo>. Override
# it and the page renders for wherever it is actually going - which is what moving to a product
# domain will need, and is the whole of what that move costs here.
BASE_URL="${SITE_BASE_URL:-https://x20surya.me/MeshSync}"
REPO_URL="${SITE_REPO_URL:-https://github.com/x20surya/MeshSync}"

# The newest tag, if nobody said. Sorted by version rather than by date, because a hotfix tagged
# after a bigger release is still the older one.
if [ -z "$VERSION" ]; then
    VERSION="$(git -C "$HERE/.." tag --list 'v*' --sort=-v:refname | head -1 || true)"
fi
[ -n "$VERSION" ] || { echo "site.sh could not work out a version. Pass --version." >&2; exit 2; }

# The tag's own date, so the page says when the release happened rather than when it was last
# rebuilt - those differ every time the repository is republished without a new release.
if [ -z "$DATE" ]; then
    DATE="$(git -C "$HERE/.." log -1 --format=%ad --date=format:'%e %B %Y' "$VERSION" 2>/dev/null \
            | sed 's/^ *//' || true)"
fi
[ -n "$DATE" ] || DATE="$VERSION"

RELEASE_URL="$REPO_URL/releases/download/$VERSION"

mkdir -p "$OUT"

# @TOKEN@ rather than ${TOKEN}, so the template stays valid HTML that opens in a browser and so
# nothing in the page's own CSS or JavaScript can be mistaken for a substitution.
sed -e "s|@VERSION@|$VERSION|g" \
    -e "s|@DATE@|$DATE|g" \
    -e "s|@BASE_URL@|$BASE_URL|g" \
    -e "s|@REPO_URL@|$REPO_URL|g" \
    -e "s|@RELEASE_URL@|$RELEASE_URL|g" \
    "$TEMPLATE" > "$OUT/index.html"

# A token that survives is a page that advertises a file called MeshSync-@VERSION@-windows-x64.exe.
# It renders perfectly and every download 404s, so it has to fail here rather than there.
if grep -o '@[A-Z_]\{2,\}@' "$OUT/index.html" | sort -u | grep .; then
    echo "site.sh left the tokens above unsubstituted in $OUT/index.html." >&2
    exit 1
fi

echo "Rendered $OUT/index.html for $VERSION ($DATE) at $BASE_URL"

# ─────────────────────────────────────────────────────────────── the links, actually followed
#
# The page names five files by hand. A rename in release.yml that nobody carried across here
# produces a page that looks right and downloads nothing, which is the failure this catches.
if [ "$CHECK" = yes ]; then
    command -v curl >/dev/null 2>&1 || { echo "--check needs curl." >&2; exit 2; }

    # One byte of each, not all of it. A plain GET here downloads every asset the page names -
    # about 210 MB for a release - on every publish, to look at a status code and throw the body
    # away. A range request proves the same thing: that the object is there and readable.
    #
    # Range rather than HEAD because the release URL redirects to object storage, and a store that
    # declines HEAD would fail this for a file that downloads perfectly. A served range answers
    # 206; a server that ignores Range answers 200 and both mean the same thing here.
    failed=0
    for url in $(grep -o "$RELEASE_URL/[^\"]*" "$OUT/index.html" | sort -u); do
        code="$(curl -sSL -o /dev/null -w '%{http_code}' --max-time 30 -r 0-0 "$url" || echo 000)"
        printf '  %s  %s\n' "$code" "$url"
        case "$code" in 200|206) ;; *) failed=1 ;; esac
    done

    [ "$failed" = 0 ] || { echo "site.sh: the links above are not downloadable." >&2; exit 1; }
    echo "Every download link resolves."
fi
