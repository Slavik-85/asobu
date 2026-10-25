#!/usr/bin/env bash
# Builds the .deb, the .rpm and the tarball from a linux-x64 publish folder.
#
#     bash packaging/linux/build-packages.sh 0.1.41 publish dist
#
# All three install the same files: the app under /opt/asobu, /usr/bin/asobu pointing at it, a
# desktop entry, AppStream metadata and icons. A "package-type" file beside the executable tells
# Asobu it was installed by a package manager, which then owns updating it, so the launcher only
# says when a new version is out. User data never goes near /opt; it lives in ~/.asobu.
#
# Needs dpkg-deb and rpmbuild, both from Ubuntu's own archive (apt-get install rpm).

set -euo pipefail

# Everything written below is root's to install and everyone's to read, whatever the runner's
# own umask happens to be.
umask 022

VERSION=$1

# A version is digits, letters, dots, plus and minus. Anything else is refused rather than pasted
# into sed and the package metadata.
if ! [[ $VERSION =~ ^[0-9A-Za-z.+-]+$ ]]; then
  echo "Refusing version '$VERSION'" >&2
  exit 1
fi
PUBLISH=$(realpath "$2")
mkdir -p "$3"
OUT=$(realpath "$3")

HERE=$(cd "$(dirname "$0")" && pwd)
ID=cc.asobu.Asobu

# 0.2.0-beta becomes 0.2.0~beta, which both dpkg and rpm sort before 0.2.0, as it should.
PKG_VERSION=${VERSION//-/\~}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Lays the install out under a root, the way it will land on disk.
stage() {
  local root=$1 type=$2

  install -d "$root/opt/asobu" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/metainfo"
  cp -a "$PUBLISH/." "$root/opt/asobu/"

  # Velopack's own markers, should the publish folder ever carry them: a package must never look
  # to the app like a Velopack install it could update itself.
  rm -f "$root/opt/asobu/UpdateNix" "$root/opt/asobu/sq.version"
  echo "$type" > "$root/opt/asobu/package-type"

  find "$root/opt/asobu" -type d -exec chmod 755 {} +
  find "$root/opt/asobu" -type f -exec chmod 644 {} +
  chmod 755 "$root/opt/asobu/Asobu.App"
  if [ -f "$root/opt/asobu/createdump" ]; then chmod 755 "$root/opt/asobu/createdump"; fi

  # A link is enough: the app finds its own folder through /proc/self/exe.
  ln -s /opt/asobu/Asobu.App "$root/usr/bin/asobu"

  install -m644 "$HERE/$ID.desktop" "$root/usr/share/applications/"
  sed "s/@VERSION@/$VERSION/;s/@DATE@/$(date -u +%F)/" "$HERE/$ID.metainfo.xml" \
    > "$root/usr/share/metainfo/$ID.metainfo.xml"

  (cd "$HERE/icons" && find hicolor -name "$ID.png") | while read -r icon; do
    install -Dm644 "$HERE/icons/$icon" "$root/usr/share/icons/$icon"
  done
}

# --- .deb ------------------------------------------------------------------------------------

DEB=$WORK/deb
stage "$DEB" deb
SIZE=$(du -sk "$DEB" | cut -f1)
install -d "$DEB/DEBIAN"

# Library names that change with the release (ICU, OpenSSL) are recommended rather than
# required: one wrong name in Depends makes the package uninstallable on that release, and every
# desktop has both anyway.
cat > "$DEB/DEBIAN/control" <<EOF
Package: asobu
Version: $PKG_VERSION
Architecture: amd64
Maintainer: Asobu <Slavik-85@users.noreply.github.com>
Installed-Size: $SIZE
Section: games
Priority: optional
Homepage: https://asobu.cc
Depends: libfontconfig1, libx11-6, libice6, libsm6, xdg-utils, ca-certificates
Recommends: libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66, libssl3t64 | libssl3 | libssl1.1, libgl1, x11-xserver-utils
Suggests: libsecret-1-0
Description: Minecraft: Java Edition launcher
 Isolated instances, every mod loader, mods from Modrinth and CurseForge,
 and crashes explained in words.
EOF

# xz rather than dpkg's default zstd, which Debian 11 cannot read.
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$OUT/Asobu-linux-x64.deb"

# --- .rpm ------------------------------------------------------------------------------------

RPM=$WORK/rpm
stage "$RPM/root" rpm
mkdir -p "$RPM/build"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS} "$RPM/out"

rpmbuild -bb "$HERE/asobu.spec" \
  --define "_topdir $RPM/build" \
  --define "_rpmdir $RPM/out" \
  --define "asobu_version $PKG_VERSION" \
  --define "asobu_root $RPM/root"

cp "$RPM"/out/x86_64/asobu-*.rpm "$OUT/Asobu-linux-x64.rpm"

# --- tarball ---------------------------------------------------------------------------------

TAR=$WORK/tar/asobu
mkdir -p "$TAR"
cp -a "$PUBLISH/." "$TAR/"
rm -f "$TAR/UpdateNix" "$TAR/sq.version"
echo tar > "$TAR/package-type"
cp -r "$HERE/icons" "$TAR/icons"
cp "$HERE/$ID.desktop" "$TAR/"

# For installing without root, which a Steam Deck needs: into ~/.local, with a menu entry.
install -m755 "$HERE/install.sh" "$TAR/install.sh"

# The store metadata too, filled in. The Flatpak is this tarball repackaged, and takes the app's
# desktop entry, icons and metadata from it rather than from anywhere else.
sed "s/@VERSION@/$VERSION/;s/@DATE@/$(date -u +%F)/" "$HERE/$ID.metainfo.xml" > "$TAR/$ID.metainfo.xml"

# The same modes the packages get, and owned by root inside the archive. Left as copied, every
# file carried the CI runner's own user id, and unpacking as root into /opt handed the whole app
# to whichever account on the machine had that id.
find "$TAR" -type d -exec chmod 755 {} +
find "$TAR" -type f -exec chmod 644 {} +
chmod 755 "$TAR/Asobu.App" "$TAR/install.sh"
if [ -f "$TAR/createdump" ]; then chmod 755 "$TAR/createdump"; fi

tar --owner=0 --group=0 --numeric-owner -C "$WORK/tar" -czf "$OUT/Asobu-linux-x64.tar.gz" asobu

ls -la "$OUT"
