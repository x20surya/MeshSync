#!/usr/bin/env bash
#
# Run the widget straight out of the working tree, against whichever Mesh Sync is running.
#
# plasmawindowed loads one plasmoid into its own window, so there is no panel to edit, no
# plasmashell to restart, and QML warnings come straight back to this terminal instead of into
# the journal. XDG_DATA_DIRS points at a copy of the tree, so the version installed under
# ~/.local/share is left exactly as it was.
#
# Usage:  plasma/preview.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

command -v plasmawindowed >/dev/null 2>&1 || {
    echo "plasma/preview.sh needs plasmawindowed, which ships with plasma-workspace." >&2
    exit 2
}

WORK="$(mktemp -d /tmp/meshsync-preview.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT INT TERM

mkdir -p "$WORK/plasma/plasmoids"
cp -r "$HERE/dev.meshsync.desktop" "$WORK/plasma/plasmoids/"

# XDG_DATA_HOME, not just XDG_DATA_DIRS.
#
# KPackage searches GenericDataLocation, which puts XDG_DATA_HOME FIRST - so a copy already
# installed under ~/.local/share/plasma/plasmoids wins over anything added to XDG_DATA_DIRS, and
# this quietly ran the installed widget instead of the working tree. Pointing XDG_DATA_HOME at a
# directory holding only this widget, and putting the real one back at the front of
# XDG_DATA_DIRS, gives the tree first refusal and leaves everything else exactly where it was.
REAL_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"

echo "running the working tree; Ctrl-C to stop"
XDG_DATA_HOME="$WORK" \
XDG_DATA_DIRS="$REAL_DATA_HOME:${XDG_DATA_DIRS:-/usr/local/share:/usr/share}" \
    plasmawindowed dev.meshsync.desktop 2>&1 |
    grep -vE "^qt\.qml\.(import|overloadresolution|diskcache|v4)" || true
