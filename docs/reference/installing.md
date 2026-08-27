---
type: reference
status: shipped
platforms: [linux, windows, android]
tier: n/a
code:
  - packaging/build.sh
  - packaging/install-user.sh
  - packaging/INSTALL.txt
  - packaging/windows/build.ps1
  - packaging/windows/MeshSync.wxs
updated: 2026-08-27
---

# Installing

What to do on a machine that is not this one. For the repository that serves the `.deb`, see
[[apt-repository]]; for building rather than installing, [[building]].

For a person rather than a note, send them to the [[download-page]] at
`https://x20surya.me/MeshSync/`: it names the file for whichever machine is asking, carries the
checksums, and says why Windows warns about the download.

## Debian, Ubuntu, and anything derived from them

Mint, Pop!_OS, Zorin, elementary, Kali and Raspberry Pi OS on amd64 all take the same three
commands.

```bash
sudo install -d -m 0755 /usr/share/keyrings
curl -fsSL https://x20surya.me/MeshSync/meshsync.gpg \
  | sudo tee /usr/share/keyrings/meshsync.gpg > /dev/null

echo "deb [arch=amd64 signed-by=/usr/share/keyrings/meshsync.gpg] https://x20surya.me/MeshSync stable main" \
  | sudo tee /etc/apt/sources.list.d/meshsync.list > /dev/null

sudo apt update && sudo apt install meshsync
```

It lands in `/opt/meshsync` with a launcher entry, the [[plasma-widget]], the icons and the D-Bus
activation file. `apt upgrade` carries it forward from there.

## Everything else - Fedora, Arch, openSUSE, NixOS

The AppImage from any [release](https://github.com/x20surya/MeshSync/releases).

```bash
chmod +x MeshSync-v0.6.1-linux-x86_64.AppImage
./MeshSync-v0.6.1-linux-x86_64.AppImage
```

**AppImages need FUSE 2, which Ubuntu 22.04 and later and current Fedora no longer install.** The
failure names `libfuse.so.2` and reads like a corrupt download. Either install `libfuse2`, or skip
FUSE entirely:

```bash
./MeshSync-v0.6.1-linux-x86_64.AppImage --appimage-extract-and-run
```

`packaging/install-user.sh` does a proper install with **no root**: the AppImage into
`~/.local/bin`, the icon and desktop entry, the Plasma widget, `meshsyncctl`, and the D-Bus service
file that lets the widget start the app rather than only report that it is not running.

## How old a machine this works on

**GLIBC_2.27**, which is Ubuntu 18.04 and Debian 10. Measured from the shipped binaries rather
than assumed - `libSkiaSharp`, `libcoreclr`, `libclrjit` and `libclrgcexp` are the four that ask
for it; everything else in the payload is happy with 2.16 or older.

Declared dependencies are `libc6`, `libx11-6`, `libice6`, `libsm6` and `libfontconfig1`, all of
which any desktop install already has. There is no .NET runtime to install: the publish is
self-contained, which is most of why the package is 33 MB.

## amd64 only, and what it would take to change

Every published artifact is x86-64. A Raspberry Pi, an arm64 server or an Asahi Mac gets nothing
from either route.

**The build script already handles it** - `packaging/build.sh` takes `ARCH=arm64` and derives the
RID, the Debian architecture and the AppImage architecture from it. What is missing is that
`.github/workflows/release.yml` calls it once with no `ARCH`, so only x64 is ever built. A matrix
over the two, and `arch=amd64,arm64` in the sources.list line, is the whole change - though it
wants running rather than assuming.

## The clipboard needs nothing on Wayland

Mesh Sync speaks `ext-data-control` to the compositor itself, so it is told when the selection
changes rather than polling for it. X11 falls back to `xclip` or `xsel`, which the package
recommends rather than depends on: with neither, the app still pairs, holds links and sends - it
just cannot read the clipboard by itself. See [[clipboard-sync]].

## Windows

Two files are attached to every release, both self-contained, so neither wants a .NET runtime.

**`MeshSync-vX.Y.Z-windows-x64.msi`** is the one to hand somebody.
It installs per machine into `Program Files\Mesh Sync`, adds a Start Menu entry and an Installed
apps entry that uninstalls cleanly, and opens TCP 45001 to the local subnet so the app never
raises Windows' own firewall prompt - which needs an administrator to answer and writes *block*
rules if it is dismissed.
It asks for elevation once, at install, and for nothing after that: the identity, the paired
devices, the settings and the run-on-startup entry are all per user, exactly as they were.

An upgrade closes the running copy before replacing it, which matters because the app enables
run-on-startup on its first run and is therefore always running when the next installer arrives.

**`MeshSync-vX.Y.Z-windows-x64.exe`** is the same app as one file, for a machine you would rather
not install anything on. It is the same self-contained build with the native libraries bundled
instead of copied, so first launch pays a moment to unpack itself into the temp directory. It adds
nothing to the Start Menu and no firewall rule, so Windows will ask about the network the first
time it listens.

Neither is code-signed. See the [[download-page]] for what SmartScreen shows and how to check the
file you got.

## Android

The signed `.apk` is attached to every release. It is signed with the CI debug key, which means
Android will refuse to install it over a copy signed with any other key - uninstall first if you
have built one yourself. There is no store listing.

## See also

[[apt-repository]] · [[building]] · [[clipboard-sync]] · [[plasma-widget]] · [[platform-matrix]]
