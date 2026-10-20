#!/usr/bin/env bash
# Installs RemoteConnect.app into /Applications for the current user and starts it.
# On first start RemoteConnect registers a LaunchAgent so it starts at login and waits in the menu bar,
# ready for connections (turn off in RemoteConnect → Settings or System Settings → General → Login Items).
#
#   ./install-macos.sh [path/to/RemoteConnect.app] [--server https://remote.example.com] [--no-autostart]
set -euo pipefail

SRC=""
SERVER=""
AUTOSTART=true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --server) SERVER="$2"; shift 2 ;;
    --no-autostart) AUTOSTART=false; shift ;;
    *) SRC="$1"; shift ;;
  esac
done
if [[ -z "$SRC" ]]; then
  SRC="$(cd "$(dirname "$0")" && pwd)/RemoteConnect.app"
fi
[[ -d "$SRC" ]] || { echo "RemoteConnect.app not found at $SRC"; exit 1; }

DEST="/Applications/RemoteConnect.app"
DATA="$HOME/Library/Application Support/RemoteConnect"

osascript -e 'tell application "RemoteConnect" to quit' >/dev/null 2>&1 || true
pkill -x RemoteConnect.Desktop >/dev/null 2>&1 || true

rm -rf "$DEST"
cp -R "$SRC" "$DEST"
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true

if [[ ! -f "$DATA/settings.json" ]]; then
  mkdir -p "$DATA" && chmod 700 "$DATA"
  if [[ -n "$SERVER" ]]; then
    printf '{ "autoStart": %s, "autoStartApplied": false, "serverUrl": "%s" }\n' "$AUTOSTART" "$SERVER" > "$DATA/settings.json"
  else
    printf '{ "autoStart": %s, "autoStartApplied": false }\n' "$AUTOSTART" > "$DATA/settings.json"
  fi
  chmod 600 "$DATA/settings.json"
fi

open "$DEST"
cat <<'EOF'

RemoteConnect is installed.

To let others see and control this Mac, allow both permissions when asked
(System Settings → Privacy & Security):
  • Screen Recording  – so your screen can be shared
  • Accessibility     – so remote mouse/keyboard input works
You can also click "Grant permissions…" in the RemoteConnect window.
EOF
