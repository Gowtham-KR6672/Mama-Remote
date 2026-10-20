# macOS installation

Supported: macOS 12 Monterey or later, on Apple Silicon (`osx-arm64`) or Intel (`osx-x64`).

## Build the app bundle (on a Mac)

```bash
./deployment/macOS/build-macos-app.sh            # or: ./deployment/macOS/build-macos-app.sh osx-x64
```

This produces `artifacts/macos-<rid>/RemoteConnect.app` and a `.dmg`. Without a signing identity the bundle is **ad-hoc signed**, which is fine on your own Macs. For distribution, set `SIGN_IDENTITY="Developer ID Application: …"` and notarize with `xcrun notarytool` (the script prints the command).

## Install

```bash
./artifacts/macos-osx-arm64/install-macos.sh [--server https://remote.example.com] [--no-autostart]
```

The script copies the app to `/Applications` and opens it. On first start RemoteConnect writes `~/Library/LaunchAgents/com.remoteconnect.desktop.plist`, so it starts at every login and waits in the menu bar. If it crashes it restarts; if you choose **Quit**, it stays quit. macOS 13+ lists it under *System Settings → General → Login Items*, where it can be switched off.

## Permissions (required to be controlled)

macOS asks once per app. In *System Settings → Privacy & Security*:

| Permission | Needed for | Without it |
|---|---|---|
| **Screen Recording** | sharing your screen | the other side sees only the desktop wallpaper |
| **Accessibility** | remote mouse/keyboard | input is silently ignored |
| Local Network | LAN discovery and direct connections | only server connections work |

The main window shows a **Grant permissions…** button until both are granted. After granting Screen Recording, quit and reopen RemoteConnect.

Controlling *other* computers from a Mac needs no special permission.

## Uninstall

```bash
./uninstall-macos.sh [--remove-data]
```

## macOS-specific notes

- Capture uses `CGDisplayCreateImage`. Apple has deprecated it in favour of ScreenCaptureKit, the planned M2 upgrade. On macOS 15, the system may periodically re-confirm the Screen Recording permission.
- Retina screens are captured at full resolution and scaled to the quality preset. Pointer coordinates are mapped in points, so clicks land correctly at any scale.
- The Windows key on a PC keyboard maps to ⌘ Command, and Alt maps to ⌥ Option (physical-key mapping).
- The mouse pointer is drawn into the shared image, because macOS capture omits it.
- **The macOS code paths compile and are packaged for both architectures, but have not yet been run on a Mac in this build.** See STATUS.md for the manual test checklist.
