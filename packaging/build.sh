#!/usr/bin/env bash
#
# Builds the distributable Linux packages: an AppImage that runs on most distributions, a .deb
# for Debian and Ubuntu, and a plain tarball for everything else.
#
# Needs only the .NET 10 SDK and dpkg-deb. appimagetool is fetched on first use and cached.
# Nothing here needs root.
set -euo pipefail

ARCH="${ARCH:-x64}"
RID="linux-${ARCH}"
DEB_ARCH="$([ "$ARCH" = "x64" ] && echo amd64 || echo arm64)"
APPIMAGE_ARCH="$([ "$ARCH" = "x64" ] && echo x86_64 || echo aarch64)"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
OUT="$HERE/out"
TOOLS="$HERE/.tools"
ASSETS="$REPO/src/assets/design_handoff_mesh_sync_brand"

VERSION="$(grep -oP '(?<=<ApplicationDisplayVersion>)[^<]+' "$REPO/src/AndroidClient/AndroidClient.csproj" 2>/dev/null || echo 1.0)"
VERSION="${VERSION:-1.0}"

rm -rf "$OUT"; mkdir -p "$OUT" "$TOOLS"

echo "==> Publishing $RID, self-contained"
PUB="$OUT/publish"
dotnet publish "$REPO/src/DesktopShell/DesktopShell.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none \
    -o "$PUB" >/dev/null

# The headless daemon ships alongside, for machines with no desktop session.
dotnet publish "$REPO/src/LinuxDaemon/LinuxDaemon.csproj" \
    -c Release -r "$RID" --self-contained false \
    -p:DebugType=none \
    -o "$OUT/daemon" >/dev/null || true

echo "==> Assembling the AppDir"
APPDIR="$OUT/MeshSync.AppDir"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/scalable/apps" \
         "$APPDIR/usr/share/metainfo"

cp -r "$PUB/." "$APPDIR/usr/bin/"
cp "$HERE/AppRun" "$APPDIR/AppRun"
cp "$HERE/meshsync.desktop" "$APPDIR/meshsync.desktop"
cp "$HERE/meshsync.desktop" "$APPDIR/usr/share/applications/meshsync.desktop"
# The brand PNG paints a cream plate behind the mark; a desktop icon wants the mark alone.
[ -f "$HERE/icons/meshsync-256.png" ] || python3 "$HERE/make-icons.py" >/dev/null
cp "$HERE/icons/meshsync-256.png" "$APPDIR/meshsync.png"
cp "$HERE/icons/meshsync-256.png" "$APPDIR/.DirIcon"
for px in 16 24 32 48 64 128 256 512; do
    mkdir -p "$APPDIR/usr/share/icons/hicolor/${px}x${px}/apps"
    cp "$HERE/icons/meshsync-$px.png" "$APPDIR/usr/share/icons/hicolor/${px}x${px}/apps/meshsync.png"
done
cp "$HERE/dev.meshsync.desktop.metainfo.xml" "$APPDIR/usr/share/metainfo/" 2>/dev/null || true

echo "==> AppImage"
TOOL="$TOOLS/appimagetool-$APPIMAGE_ARCH.AppImage"
if [ ! -x "$TOOL" ]; then
    curl -sSL -o "$TOOL" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$APPIMAGE_ARCH.AppImage"
    chmod +x "$TOOL"
fi

# --appimage-extract-and-run so this works on a machine with no FUSE, such as a CI runner
# or a container, where the tool would otherwise refuse to start.
ARCH="$APPIMAGE_ARCH" "$TOOL" --appimage-extract-and-run \
    "$APPDIR" "$OUT/MeshSync-$VERSION-$APPIMAGE_ARCH.AppImage" >/dev/null 2>&1 \
    || ARCH="$APPIMAGE_ARCH" "$TOOL" --appimage-extract-and-run \
        "$APPDIR" "$OUT/MeshSync-$VERSION-$APPIMAGE_ARCH.AppImage"

echo "==> .deb"
DEB="$OUT/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/meshsync" "$DEB/usr/bin" \
         "$DEB/usr/share/applications"

cp -r "$PUB/." "$DEB/opt/meshsync/"
cp "$HERE/meshsync.desktop" "$DEB/usr/share/applications/meshsync.desktop"
for px in 16 24 32 48 64 128 256 512; do
    mkdir -p "$DEB/usr/share/icons/hicolor/${px}x${px}/apps"
    cp "$HERE/icons/meshsync-$px.png" "$DEB/usr/share/icons/hicolor/${px}x${px}/apps/meshsync.png"
done
ln -sf /opt/meshsync/meshsync "$DEB/usr/bin/meshsync"

INSTALLED_KB="$(du -sk "$DEB/opt" | cut -f1)"

cat > "$DEB/DEBIAN/control" <<EOF
Package: meshsync
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Installed-Size: $INSTALLED_KB
Maintainer: x20surya <suryanshuc659@gmail.com>
Homepage: https://github.com/x20surya/MeshSync
Depends: libc6, libx11-6, libice6, libsm6, libfontconfig1
Recommends: wl-clipboard | xclip
Description: Local-first universal clipboard for your own devices
 Copy on one device and paste on another, with no cloud, no server and no
 account. Carries text, images and files, mirrors phone notifications, and
 can make a lost device sound an alarm.
 .
 Every connection agrees its own key, so a paired device cannot read traffic
 meant for another pair.
EOF

# The clipboard needs a helper on X11; say so once at install time rather than leaving the
# user to discover a silently inert feature.
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    if ! command -v wl-paste >/dev/null 2>&1 && ! command -v xclip >/dev/null 2>&1; then
        echo "Mesh Sync: install wl-clipboard (Wayland) or xclip (X11) to turn on clipboard sync."
    fi
fi
EOF
chmod 755 "$DEB/DEBIAN/postinst"

dpkg-deb --root-owner-group -Zxz --build "$DEB" "$OUT/meshsync_${VERSION}_${DEB_ARCH}.deb" >/dev/null

echo "==> tarball"
TARDIR="$OUT/meshsync-$VERSION-$RID"
mkdir -p "$TARDIR"
cp -r "$PUB/." "$TARDIR/"
cp "$HERE/meshsync.desktop" "$TARDIR/"
cp "$HERE/icons/meshsync-256.png" "$TARDIR/meshsync.png"
cp "$HERE/INSTALL.txt" "$TARDIR/" 2>/dev/null || true
tar -C "$OUT" -caf "$OUT/meshsync-$VERSION-$RID.tar.xz" "$(basename "$TARDIR")"

rm -rf "$PUB" "$APPDIR" "$DEB" "$TARDIR" "$OUT/daemon"

echo
echo "Built:"
ls -lh "$OUT" | tail -n +2 | awk '{printf "  %-44s %s\n", $9, $5}'
