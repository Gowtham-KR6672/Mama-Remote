# RemoteConnect

A self-hosted, AnyDesk-style remote desktop for **Windows and macOS**.

Install it on a computer and it starts automatically when you sign in. It waits in the tray (menu bar on Mac), shows a 9-digit address, and is ready for incoming connections. Type another computer's address and click **Connect** to see and control it.

```
┌───────────── This desk ─────────────┐   ┌──────────────── Remote desk ────────────────┐
│ Your address   533 921 644          │   │ [ 482 913 207              ]  [ Connect ]   │
│ ● Ready — online via server         │   │ Recent sessions · Local network · Settings  │
└─────────────────────────────────────┘   └──────────────────────────────────────────────┘
```

## What it does

| | |
|---|---|
| **Auto-start** | Registers itself to start at sign-in: the HKCU *Run* key on Windows, a LaunchAgent on macOS. It starts hidden in the tray and is ready to accept requests. You can turn this off in Settings, Task Manager or Login Items. |
| **Attended access** | The person at the remote computer gets a prompt with **Accept / View only / Reject**. It auto-rejects after 30 s. |
| **Unattended access** | Off by default. You opt in with a password, and you can limit it to an allow-list of device IDs. Lockout after failed attempts. |
| **Always visible** | During a session the host sees an always-on-top panel with who is connected, how long, and their permissions. It has **Disconnect** and toggles for mouse/keyboard and clipboard. |
| **LAN, no server needed** | Devices find each other by UDP broadcast, or you connect by IP address. |
| **Internet** | Uses your own server (`RemoteConnect.Server`). It handles ID lookup, tries direct LAN endpoints first, then relays through the server. |
| **Encryption** | Every session is **end-to-end mutual TLS** between the two devices. Each device has its own RSA-3072 key, and the key's fingerprint is pinned. The relay only sees ciphertext. |
| **Screen and input** | Multi-monitor support, Windows ⇄ Mac in any direction, mouse/wheel/keyboard, text clipboard sync, and quality presets. |

See [docs/STATUS.md](docs/STATUS.md) for exactly what is implemented, what was verified, and what remains (honest list).

## Quick start (Windows)

You need the [.NET SDK](https://dotnet.microsoft.com/download): 8.0 is installed here, and .NET 10 is recommended.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish-windows.ps1                       # → artifacts\RemoteConnect-win-x64
powershell -ExecutionPolicy Bypass -File .\artifacts\RemoteConnect-win-x64\install-windows.ps1
```

The installer is per-user and needs no admin rights. It copies the app, creates a Start-menu entry, enables start-at-sign-in and launches RemoteConnect. Add `-AddFirewallRules` from an elevated prompt to pre-allow LAN connections, and `-ServerUrl https://remote.example.com` to pre-configure your server. An Inno Setup script for a classic `Setup.exe` is in [deployment/Windows](deployment/Windows/RemoteConnect.iss).

## Quick start (macOS)

On a Mac with the .NET SDK and Xcode command-line tools:

```bash
./deployment/macOS/build-macos-app.sh            # Apple Silicon; use osx-x64 for Intel
./artifacts/macos-osx-arm64/install-macos.sh
```

Then allow **Screen Recording** and **Accessibility** for RemoteConnect in *System Settings → Privacy & Security*. The app has a **Grant permissions…** button for this. See [docs/MACOS_INSTALL.md](docs/MACOS_INSTALL.md).

## Try it on a LAN (two computers, no server)

1. Install RemoteConnect on both computers.
2. On computer A, look under **Local network**: B appears within a few seconds. Or type B's 9-digit address, or B's IP address.
3. Click **Connect**. B shows the request: click **Accept**. A now sees B's screen and can control it.
4. B can end the session at any time from the panel in the bottom-right corner.

Details and firewall notes: [docs/LOCAL_SETUP.md](docs/LOCAL_SETUP.md).

## Across the internet

Run the server on any machine reachable from both computers, behind HTTPS:

```powershell
.\scripts\run-server.ps1 -RegistrationKey "<long random key>"        # development
# production on Windows Server: artifacts\RemoteConnect.Server-win-x64\install-server-windows.ps1
```

In each client go to **Settings → Server** and enter your `https://` address and the registration key. Connections by ID then work from anywhere, relayed if no direct path exists. See [docs/INTERNET_SETUP.md](docs/INTERNET_SETUP.md).

## Repository layout

```
src/Shared/RemoteConnect.Protocol      wire messages, framing, frame updates, signaling contracts
src/Shared/RemoteConnect.Security      device identity (keys/ID), fingerprints, TLS settings, password hashing
src/Engine/RemoteConnect.Platform      screen capture + input injection + auto-start (Windows GDI/SendInput, macOS CoreGraphics/CGEvent)
src/Engine/RemoteConnect.Session       host & controller sessions, codec, transports (TCP/WebSocket + TLS), LAN discovery, signaling client
src/Desktop/RemoteConnect.Desktop      Avalonia UI app (Windows + macOS): dashboard, tray, prompts, remote view
src/Server/RemoteConnect.Server        ASP.NET Core: device registry, SignalR signaling, WebSocket relay, audit, health, admin API
tests/                                 unit + integration tests (real TLS sessions on loopback and through the relay)
scripts/                               build, test, run, publish, install/uninstall, smoke test, icons
deployment/                            macOS bundle, Windows installer script, server reverse-proxy/systemd examples
docs/                                  architecture, security, API, setup guides, test plan, status
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| Device not listed under *Local network* | Both machines must be on the same subnet, with UDP 47071 allowed. Connect by IP instead (TCP 47070). |
| Windows asks to allow network access | Allow it on **Private** networks, or install with `-AddFirewallRules`. |
| Mac shows only the wallpaper | Screen Recording permission is missing. Grant it, then quit and reopen RemoteConnect. |
| Mac ignores mouse/keyboard | Accessibility permission is missing. |
| Can't type into an admin window / UAC prompt on Windows | Windows blocks input to elevated windows from a normal app (UIPI). See STATUS → known limitations. |
| "Security warning: different identity key" | The device was reinstalled, or someone is impersonating it. If you are sure it was reinstalled, click **I trust the new identity — reconnect**. |
| Server status "refused" | Wrong registration key, or the ID is bound to another key on the server. |

Logs are in `%APPDATA%\RemoteConnect\logs` on Windows and `~/Library/Application Support/RemoteConnect/logs` on macOS. **Settings → Open settings and logs folder** opens them.
