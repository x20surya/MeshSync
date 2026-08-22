#!/usr/bin/env bash
#
# Installs Mesh Sync for the current user, with no root.
#
# Puts the AppImage somewhere stable, registers a .desktop entry and an icon, and refreshes the
# menu caches so the launcher finds it straight away rather than at the next login.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

APPIMAGE="$(ls "$HERE"/out/MeshSync-*.AppImage 2>/dev/null | head -1 || true)"
[ -n "$APPIMAGE" ] || { echo "No AppImage built yet. Run packaging/build.sh first." >&2; exit 1; }

BIN="$HOME/.local/bin"
APPS="$HOME/.local/share/applications"
THEME="$HOME/.local/share/icons/hicolor"
mkdir -p "$BIN" "$APPS"

install -m755 "$APPIMAGE" "$BIN/MeshSync.AppImage"

# Regenerated rather than shipped, so the icon always matches the mark in the brand handoff.
[ -f "$HERE/icons/meshsync-256.png" ] || python3 "$HERE/make-icons.py" >/dev/null

# Every size, as PNG. A lone scalable SVG is legal and several launchers quietly skip it, which
# is how an entry ends up listed with no icon beside it.
for px in 16 24 32 48 64 128 256 512; do
    mkdir -p "$THEME/${px}x${px}/apps"
    install -m644 "$HERE/icons/meshsync-$px.png" "$THEME/${px}x${px}/apps/meshsync.png"
done

# A theme directory with no index.theme is not a theme. GTK is lenient about that and finds the
# icons anyway; KDE's icon loader is not, and silently ignores the whole directory - so the app
# shows up in the launcher and the taskbar with no icon at all, while every file is present and
# correct. The system hicolor index declares every standard size, so it is reused verbatim.
if [ ! -f "$THEME/index.theme" ] && [ -f /usr/share/icons/hicolor/index.theme ]; then
    install -m644 /usr/share/icons/hicolor/index.theme "$THEME/index.theme"
fi

# Also outside the theme, which is where a few older launchers still look first.
mkdir -p "$HOME/.local/share/pixmaps"
install -m644 "$HERE/icons/meshsync-256.png" "$HOME/.local/share/pixmaps/meshsync.png"

# Exec is the absolute path: the launcher does not necessarily have ~/.local/bin on PATH.
sed "s|^Exec=meshsync$|Exec=$BIN/MeshSync.AppImage|" "$HERE/meshsync.desktop" > "$APPS/meshsync.desktop"
chmod 644 "$APPS/meshsync.desktop"

update-desktop-database "$APPS" 2>/dev/null || true
gtk-update-icon-cache -f -t "$THEME" 2>/dev/null || true
# KDE keeps its own menu cache and will not notice a new entry until this is rebuilt.
# KDE caches resolved icons; a newly installed one is not picked up until this is cleared.
rm -f "$HOME/.cache/icon-cache.kcache" 2>/dev/null || true
command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 --noincremental >/dev/null 2>&1 || true

echo "Installed. Search your launcher for \"Mesh Sync\"."
echo "  binary   $BIN/MeshSync.AppImage"
echo "  entry    $APPS/meshsync.desktop"
