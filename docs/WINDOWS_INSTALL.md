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

**Setup.exe instead of a script:** build [deployment/Windows/RemoteConnect.iss](../deployment/Windows/RemoteConnect.iss) with Inno Setup 6, then sign it with `signtool`.

## Uninstall

```powershell
& "$env:LOCALAPPDATA\Programs\RemoteConnect\uninstall-windows.ps1" [-RemoveUserData]
```

This removes the app, auto-start, shortcut and uninstall entry. It removes firewall rules if run elevated. `-RemoveUserData` also deletes the device identity, so a reinstall gets a new ID.

## Windows-specific behaviour

- **Sign-in screen, lock screen and UAC prompts are not visible or controllable remotely**, because the app runs in your user session without elevation. A Windows service with a session agent for pre-sign-in access is planned (see STATUS.md).
- **Elevated windows** (apps run as administrator) do not accept remote input unless RemoteConnect itself runs elevated. This is Windows UIPI protection.
- **Ctrl+Alt+Del** cannot be sent (Windows reserves it). The toolbar offers **Ctrl+Shift+Esc**, which opens Task Manager.
- Mixed-DPI multi-monitor setups are supported: the app is per-monitor DPI aware.
