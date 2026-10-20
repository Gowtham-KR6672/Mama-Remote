# Windows installation

Supported: Windows 10 (1809+) and Windows 11, x64.

## Install

```powershell
.\scripts\publish-windows.ps1
powershell -ExecutionPolicy Bypass -File .\artifacts\RemoteConnect-win-x64\install-windows.ps1 [-AddFirewallRules] [-ServerUrl https://remote.example.com] [-NoAutoStart]
```

| What | Where |
|---|---|
| Program files | `%LOCALAPPDATA%\Programs\RemoteConnect` (per user, no admin needed) |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RemoteConnect` → `"…\RemoteConnect.Desktop.exe" --autostart` |
| Start-menu shortcut | `RemoteConnect` |
| Uninstall entry | Settings → Apps → Installed apps → RemoteConnect |
| Data (identity, settings, logs) | `%APPDATA%\RemoteConnect` |

After installation RemoteConnect is running and sits in the notification area (tray icon). It starts the same way at every sign-in, with no window. Click the tray icon or the Start-menu entry to open it. Closing the window keeps it running in the tray; choose **Quit** from the tray menu to stop it.

**Single file, no installer:** `scripts\publish-single-file.ps1` builds one `RemoteConnect.exe`. Put it in a permanent folder, such as `C:\RemoteConnect`, and run it. It sets itself to start at sign-in and lists itself under **Settings → Apps → Installed apps**, so it can be uninstalled from there or from RemoteConnect's own **Settings → Uninstall RemoteConnect…**. Uninstalling:
- stops RemoteConnect;
- removes auto-start and the Installed-apps entry;
- deletes the exe;
- optionally deletes the settings and device ID.

**Setup.exe instead of a script:** build [deployment/Windows/RemoteConnect.iss](../deployment/Windows/RemoteConnect.iss) with Inno Setup 6, then sign it with `signtool`. (The Inno Setup installer is per-user only; service mode is installed with the script below.)

## Service mode: reachable right after power-on

By default RemoteConnect starts when you sign in, so a PC that is restarted and left at the sign-in screen cannot be reached. **Service mode** fixes that. A Windows service starts at boot and runs a session agent on the screen in front of the computer: the sign-in screen, the lock screen, UAC prompts and, after sign-in, the user's desktop.

> **Status:** implemented and covered by automated tests, but not yet tried on a real machine. See the checklist in [STATUS.md](STATUS.md#service-mode-what-is-verified-and-what-is-not) before relying on it.

Nobody can accept a request at the sign-in screen, so **in service mode the unattended-access password is the only way in**. It is off after installation and stays off until an administrator sets it.

```powershell
.\scripts\publish-windows.ps1
# In an elevated PowerShell ("Run as administrator"):
powershell -ExecutionPolicy Bypass -File .\artifacts\RemoteConnect-win-x64\install-windows.ps1 -ServiceMode [-AddFirewallRules] [-ServerUrl https://remote.example.com]
& "$env:ProgramFiles\RemoteConnect\RemoteConnect.HostService.exe" set-password      # turns unattended access ON
& "$env:ProgramFiles\RemoteConnect\RemoteConnect.HostService.exe" status            # state, this PC's ID
```

| What | Where |
|---|---|
| Program files | `%ProgramFiles%\RemoteConnect`, owned by Administrators; users can only read. The service refuses to start its agent if anyone else can change these files. |
| Service | `RemoteConnectHost` ("RemoteConnect Host Service"): LocalSystem, Automatic, restarts after a failure |
| Session agent | `RemoteConnect.Desktop.exe --agent`, started by the service in the console session. It has no window or tray icon, and shows the session panel only while someone is connected. |
| Machine data (device key, settings with the password hash, agent logs) | `C:\Windows\System32\config\systemprofile\AppData\Local\RemoteConnect`: the LocalSystem profile, readable only by SYSTEM and Administrators |
| Public status (state, this PC's ID; nothing secret) | `HKLM\SOFTWARE\RemoteConnect\Service` |
| Service log | Event Viewer → Windows Logs → Application, source `RemoteConnect.HostService` |
| Start-menu shortcut, uninstall entry | for all users |

The service has its own device ID, separate from any per-user ID. When the service is installed, the desktop app shows the service's ID and status on its dashboard. It stops accepting incoming connections itself, so the two never compete for port 47070 and the computer has one ID. You can still use the app to connect to other computers. The installer removes an earlier per-user install of the same user.

**Administrator commands** (`RemoteConnect.HostService.exe <command>` in an elevated prompt; changes apply within a few seconds, no restart needed):

| Command | Effect |
|---|---|
| `status` | Service and agent state, console session, this PC's ID, unattended access on/off |
| `set-password` | Asks twice for the password (at least 8 characters) and turns unattended access **on** |
| `disable-unattended` | Turns unattended access **off** and deletes the password hash. Nobody can connect through the service. |
| `allow <ID>[,<ID>…]` / `allow any` | Only these controller IDs may use the password / remove the limit |
| `allow-control on\|off` | Unattended sessions may control the PC, or only view it |
| `set-server <URL>\|none`, `set-registration-key [--clear]`, `set-alias <name>` | Server, server registration key (stored DPAPI-protected) and the name shown to controllers |

What you get, and what you don't, in service mode:

- The sign-in, lock and UAC screens are visible and can be operated remotely, for example to type the Windows password.
- Once someone signs in, the always-on-top session panel appears on their desktop. It shows who is connected, and Disconnect / Allow control work from there.
- **Ctrl+Alt+Del cannot be sent yet.** That only matters if a policy requires it at sign-in.
- Clipboard sync is off in service mode.
- The agent serves the **console** session (the physical screen). Remote Desktop (RDP) sessions are not served. Switching users or signing out ends the remote session; the controller can reconnect right away.

To remove service mode, run `uninstall-windows.ps1` **elevated**. `-RemoveUserData` also deletes the service's device identity.

## Uninstall

```powershell
& "$env:LOCALAPPDATA\Programs\RemoteConnect\uninstall-windows.ps1" [-RemoveUserData]
```

This removes the app, auto-start, shortcut and uninstall entry. It removes firewall rules if run elevated. `-RemoveUserData` also deletes the device identity, so a reinstall gets a new ID.

Alternatively, for any per-user copy, scripted or single file, use **Settings → Apps → Installed apps → RemoteConnect → Uninstall**, or **RemoteConnect → Settings → Uninstall RemoteConnect…**. These don't remove firewall rules added with `-AddFirewallRules`; that needs the elevated script.

## The session panel on the host

While someone is connected, a panel sits in the bottom-right corner of the host's screen. It shows who is connected and for how long, has **Disconnect**, and has switches for mouse/keyboard and clipboard.
- **–** shrinks it to a small bar, and **Details** expands it again. RemoteConnect remembers your choice.
- You can drag it anywhere.
- It can't be closed or hidden while a session is active, and the remote side can't remove it. Whoever sits at the computer always knows someone is connected.
- On Windows 10 2004 and later the panel is left out of the shared screen image, so it never covers what the remote side sees.

For a service-mode install, run it from an elevated prompt (`& "$env:ProgramFiles\RemoteConnect\uninstall-windows.ps1"`). It stops and deletes the service, which ends the agent and its sessions, then removes `%ProgramFiles%\RemoteConnect`, the machine-wide shortcut and entries, and `HKLM\SOFTWARE\RemoteConnect`. The service's identity and settings are kept unless you add `-RemoveUserData`. Without elevation the script refuses and changes nothing.

## Windows-specific behaviour

- **Sign-in screen, lock screen and UAC prompts are not visible or controllable remotely** in the default per-user install, because the app runs in your user session without elevation. Install [service mode](#service-mode-reachable-right-after-power-on) for that.
- **Elevated windows** (apps run as administrator) do not accept remote input unless RemoteConnect itself runs elevated. This is Windows UIPI protection.
- **Ctrl+Alt+Del** cannot be sent (Windows reserves it). The toolbar offers **Ctrl+Shift+Esc**, which opens Task Manager.
- Mixed-DPI multi-monitor setups are supported: the app is per-monitor DPI aware.
