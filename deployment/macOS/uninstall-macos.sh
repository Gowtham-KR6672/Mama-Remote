#!/usr/bin/env bash
# Removes RemoteConnect: quits it, removes the login LaunchAgent and the app.
#   ./uninstall-macos.sh [--remove-data]     (--remove-data also deletes settings, device identity and logs)
set -euo pipefail

LABEL="com.remoteconnect.desktop"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

launchctl bootout "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || true
osascript -e 'tell application "RemoteConnect" to quit' >/dev/null 2>&1 || true
pkill -x RemoteConnect.Desktop >/dev/null 2>&1 || true
rm -f "$PLIST"
rm -rf "/Applications/RemoteConnect.app"

if [[ "${1:-}" == "--remove-data" ]]; then
  rm -rf "$HOME/Library/Application Support/RemoteConnect"
  echo "User data removed."
fi

# Permission entries can be reset too (optional):
#   tccutil reset ScreenCapture com.remoteconnect.desktop
#   tccutil reset Accessibility com.remoteconnect.desktop
echo "RemoteConnect removed. It will no longer start at login or accept connections."
