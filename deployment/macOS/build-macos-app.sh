#!/usr/bin/env bash
# Builds RemoteConnect.app (and a .dmg) on macOS.
#
#   ./deployment/macOS/build-macos-app.sh                 # Apple Silicon (osx-arm64), ad-hoc signed
#   ./deployment/macOS/build-macos-app.sh osx-x64         # Intel Macs
#   SIGN_IDENTITY="Developer ID Application: Your Org (TEAMID)" ./deployment/macOS/build-macos-app.sh
#
# Requirements: macOS 12+, .NET SDK (same major version as the solution), Xcode command line tools.
# Screen Recording and Accessibility permissions are tied to the signed app bundle, so always run the
# bundle (not `dotnet run`) when testing hosting on a Mac.
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/artifacts/macos-$RID"
APP="$OUT/RemoteConnect.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"   # "-" = ad-hoc (local use); a Developer ID enables notarization

echo "Publishing $RID ($VERSION)…"
rm -rf "$OUT"
dotnet publish "$ROOT/src/Desktop/RemoteConnect.Desktop/RemoteConnect.Desktop.csproj" \
  -c Release -r "$RID" --self-contained true -p:UseAppHost=true -o "$OUT/publish" -nologo

echo "Assembling bundle…"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$ROOT/deployment/macOS/Info.plist" > "$APP/Contents/Info.plist"
cp "$ROOT/deployment/macOS/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
chmod +x "$APP/Contents/MacOS/RemoteConnect.Desktop"

echo "Signing ($SIGN_IDENTITY)…"
SIGN_ARGS=(--force --timestamp=none --sign "$SIGN_IDENTITY")
if [[ "$SIGN_IDENTITY" != "-" ]]; then
  SIGN_ARGS=(--force --timestamp --options runtime --entitlements "$ROOT/deployment/macOS/entitlements.plist" --sign "$SIGN_IDENTITY")
fi
# Sign nested native libraries first, then the bundle.
find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -111 \) -print0 | while IFS= read -r -d '' f; do
  codesign "${SIGN_ARGS[@]}" "$f"
done
codesign "${SIGN_ARGS[@]}" "$APP"
codesign --verify --deep --strict "$APP"

cp "$ROOT/deployment/macOS/install-macos.sh" "$ROOT/deployment/macOS/uninstall-macos.sh" "$OUT/"
chmod +x "$OUT/install-macos.sh" "$OUT/uninstall-macos.sh"

echo "Creating disk image…"
hdiutil create -volname "RemoteConnect" -srcfolder "$APP" -ov -format UDZO "$OUT/RemoteConnect-$VERSION-$RID.dmg" >/dev/null

echo
echo "Done:"
echo "  $APP"
echo "  $OUT/RemoteConnect-$VERSION-$RID.dmg"
if [[ "$SIGN_IDENTITY" != "-" ]]; then
  echo "Notarize with: xcrun notarytool submit \"$OUT/RemoteConnect-$VERSION-$RID.dmg\" --keychain-profile <profile> --wait"
fi
