#!/usr/bin/env bash
# Installs Asobu for you alone, with no root needed — which is what a Steam Deck, or any system
# whose own folders are read-only, has to have. Run it from the unpacked download:
#
#     bash ~/Downloads/asobu/install.sh
#
# The app goes into ~/.local/share/asobu, a launcher into ~/.local/bin/asobu, and an entry with
# its icon into the app menu. Running it again from a newer download updates it. With
# --uninstall it takes all of that away again. Your instances and settings live in ~/.asobu and
# are left alone either way.

set -euo pipefail

SRC=$(cd "$(dirname "$0")" && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}
APP=$DATA/asobu
BIN=$HOME/.local/bin
ID=cc.asobu.Asobu

remove() {
  rm -rf "$APP"
  rm -f "$BIN/asobu" "$DATA/applications/$ID.desktop"
  find "$DATA/icons/hicolor" -name "$ID.png" -delete 2>/dev/null || true
}

if [ "${1:-}" = "--uninstall" ]; then
  remove
  echo "Asobu is removed. Your instances and settings are still in ~/.asobu."
  exit 0
fi

if [ "$SRC" = "$APP" ]; then
  echo "Run this from the unpacked download, not from the installed copy." >&2
  exit 1
fi

if [ ! -x "$SRC/Asobu.App" ] && [ ! -f "$SRC/Asobu.App" ]; then
  echo "This folder doesn't look like an unpacked Asobu download." >&2
  exit 1
fi

# Replaced rather than copied over, so nothing an older version left behind lingers.
remove
mkdir -p "$APP" "$BIN" "$DATA/applications"
cp -a "$SRC/." "$APP/"
chmod 755 "$APP/Asobu.App"
ln -sfn "$APP/Asobu.App" "$BIN/asobu"

(cd "$SRC/icons" && find hicolor -name "$ID.png") | while read -r icon; do
  install -Dm644 "$SRC/icons/$icon" "$DATA/icons/$icon"
done

# The packaged entry points at /usr/bin/asobu; this copy lives somewhere else.
EXEC=${APP//&/\\&}
sed -e "s|^Exec=.*|Exec=\"$EXEC/Asobu.App\"|" -e "s|^TryExec=.*|TryExec=$EXEC/Asobu.App|" \
  "$SRC/$ID.desktop" > "$DATA/applications/$ID.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$DATA/applications" >/dev/null 2>&1 || true
fi

echo "Asobu is installed. You'll find it in your app menu under Games."
