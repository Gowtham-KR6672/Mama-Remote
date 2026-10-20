# Status

**Version 0.1.0 — 2026-10-20.** Milestone M1 (attended LAN + internet relay vertical slice) is implemented and tested in code. Parts of M3, M4 and M5 are also implemented. Two-machine manual acceptance (docs/TEST_PLAN.md) has **not** been run yet.

**Service mode (M4, "reachable right after power-on"), 2026-10-20:** implemented, builds clean and covered by automated tests, but **not yet installed or run on a real machine**. The PC it was built on has no administrator account, and the sign-in-screen behaviour needs a reboot test. See [Service mode: what is verified and what is not](#service-mode-what-is-verified-and-what-is-not).

## Platform decision

The original spec says *.NET 10 + WPF*. WPF is Windows-only, and this product must also run on macOS, so the UI uses **Avalonia 11.3**: the same XAML/MVVM model, running on Windows and macOS.

The machine this was built on has only the **.NET 8 SDK**, so projects target `net8.0`. One property in `Directory.Build.props` switches to .NET 10 (`-p:RemoteConnectTargetFramework=net10.0`). **Do this before 10 Nov 2026, when .NET 8 support ends.**

## Implemented and verified

"Verified" means covered by a passing automated test or an observed run on this Windows 11 machine.

| Feature | Evidence |
|---|---|
| Device identity (RSA-3072), 9-digit ID, DPAPI-protected storage, persists across restarts | unit tests; app relaunched → same ID |
| Mutual-TLS sessions with fingerprint pinning, TOFU for IP connections | integration tests (direct, wrong pin refused, TOFU) |
| Attended approval: Accept / View-only / Reject / timeout / busy | integration tests |
| Host-enforced permissions on every message; host revokes control mid-session; keys released on disconnect | integration tests |
| Unattended access: opt-in, PBKDF2 password, allow-list, per-device + global lockout | unit + integration tests |
| Screen streaming: tile diff + JPEG, flow control, quality presets, multi-monitor layout | unit + integration tests (synthetic screen) |
| Windows GDI capture of the real screen | opt-in test passed on this PC (`RC_TEST_REAL_CAPTURE=1`) |
| Windows `SendInput` structure layout | unit test (40-byte INPUT on x64) |
| Self-hosted server: key-proof registration, registration key, resolve, relay with end-to-end TLS, audit, admin API, health | integration tests against real Kestrel; `smoke-test.ps1` 6/6 PASS |
| Relay pairing in both join orders, single-use tokens | integration tests |
| Desktop UI: dashboard, tray, settings, remote view, request prompt, host indicator, TOFU dialog, light/dark | app launched on Windows 11, main window rendered and checked |
| Auto-start at sign-in (Windows Run key / macOS LaunchAgent) | code + plist unit test; installer sets the Run key. Not re-tested by rebooting. |
| Windows publish (self-contained x64), macOS publish (osx-arm64, osx-x64) | `publish-windows.ps1` and cross-publish succeeded; native Skia/Avalonia libs present |
| Service mode: service ↔ agent messages validated strictly (size, type, direction, unknown or missing fields, value ranges, ID must derive from the fingerprint) | unit tests (`IpcMessageTests`) |
| Service mode: agent supervisor (follows the console session, session switch, crash backoff, hang detection, service stop) | unit tests (`AgentSupervisorTests`, pure state machine) |
| Service mode: named pipe. The production ACL refuses a non-SYSTEM client; the server rejects any process but the launched agent; the agent rejects a non-SYSTEM or wrong-binary server; a second instance of the pipe name cannot be created | unit tests over real pipes on this PC (`ServiceModeWindowsTests`) |
| Service mode: install-folder check refuses user-writable or user-owned files | unit tests (temp folder + synthetic ACLs) |
| Service mode: host refuses everyone without the unattended password and never shows a prompt; the password works | integration tests (`Service_mode_*`) |
| Service mode: input-desktop thread mechanics (dedicated thread, desktop name, error propagation) and a real GDI frame captured through it | unit tests; opt-in real-capture test passed here as a normal user on the `Default` desktop |
| Service mode: published layout | `publish-windows.ps1` puts `RemoteConnect.HostService.exe` next to the app; `RemoteConnect.HostService.exe status` runs; `set-password` refuses without admin; `RemoteConnect.Desktop.exe --agent` exits with code 2 when not LocalSystem |

### Build and test evidence (2026-10-20, Windows 11 Pro 10.0.26300, .NET SDK 8.0.424)

```
dotnet build RemoteConnect.sln            → 0 warnings, 0 errors
dotnet test  RemoteConnect.sln            → UnitTests 62/62 passed, IntegrationTests 16/16 passed
after service mode (same day)             → 0 warnings; UnitTests 120/120, IntegrationTests 24/24
RC_TEST_REAL_CAPTURE=1 (after service mode) → GDI capture and capture through the input-desktop thread: 2/2 passed
RC_TEST_REAL_CAPTURE=1 (GDI capture)      → 1/1 passed
scripts\smoke-test.ps1                    → 6/6 PASS
scripts\publish-windows.ps1               → artifacts\RemoteConnect-win-x64, artifacts\RemoteConnect.Server-win-x64
dotnet publish -r osx-arm64 / osx-x64     → succeeded (not signed; signing requires a Mac)
```

## Implemented, not yet verified on real hardware

These need a person on two machines (see TEST_PLAN.md):

- **Real input injection** on Windows and on macOS. It was deliberately not run, because it would move this PC's real mouse.
- **The macOS app.** All macOS code (CoreGraphics capture, CGEvent input, permission prompts, LaunchAgent, bundle script) compiles and publishes, but has **not been run on a Mac**.
- Two-computer LAN discovery and Windows Firewall interaction.
- Clipboard sync between two real machines.
- Behaviour on lock screen / UAC, monitor hot-plug, sleep/resume.
- **Service mode** end to end (see the next section).

## Service mode: what is verified and what is not

`scripts\install-windows.ps1 -ServiceMode` installs `RemoteConnect.HostService` (LocalSystem, automatic start). The service starts `RemoteConnect.Desktop.exe --agent` in the active console session. The agent hosts with the machine's own ID and follows the input desktop, including the sign-in, lock and UAC screens. Design: [ARCHITECTURE.md](ARCHITECTURE.md#service-mode-windows). Threats: [SECURITY.md](SECURITY.md#service-mode-windows).

| Part | Verified on this PC | Still needs a real machine (administrator + reboot) |
|---|---|---|
| Build, tests, scripts | `dotnet build` 0 warnings; 120 + 24 tests pass; publish layout checked; both scripts parse; `-ServiceMode` stops with a clear message when not elevated | — |
| Service install / uninstall | not run (no administrator account here) | elevated `install-windows.ps1 -ServiceMode` and `uninstall-windows.ps1`; the service shows in `services.msc` as Automatic / LocalSystem with restart-on-failure |
| Agent launch (`CreateProcessAsUser` with a privilege-free LocalSystem token moved into the console session) | compiles; never executed | after boot the agent runs in session 1 as SYSTEM; `RemoteConnect.HostService.exe status` shows `Ready` and an ID |
| Pipe between service and agent | ACL, peer-PID and image checks exercised over real pipes as a normal user | the real SYSTEM ↔ SYSTEM handshake and heartbeats |
| Capture and input on the Winlogon desktop | desktop-switching code runs as a normal user (stays on `Default`) | sign-in screen visible remotely after a cold boot; typing the Windows password remotely signs in; lock screen and UAC prompt visible and usable |
| Session indicator after sign-in | not run | the panel appears on the user's desktop when someone signs in during a remote session; Disconnect works |
| Session switch, sign-out, fast user switching, RDP | state machine unit-tested | the agent follows the console; the old agent exits cleanly with no stuck keys |
| Per-user app in service mode | builds | shows the service's ID and state, and does not listen on port 47070 |

**Reboot test checklist.** Run it as an administrator on a test PC, with a second PC as the controller:

1. Run `.\scripts\publish-windows.ps1`. Then, elevated: `.\artifacts\RemoteConnect-win-x64\install-windows.ps1 -ServiceMode -AddFirewallRules`.
2. Run `& "$env:ProgramFiles\RemoteConnect\RemoteConnect.HostService.exe" status`. Expect `Ready`; note the device ID. Connect from the controller: it must be refused ("unattended access is not set up").
3. Elevated: `... set-password`. Connect again with the password: the session works and the indicator panel is visible on the host.
4. Restart the host PC and do **not** sign in. Connect by ID: the sign-in screen is shown. Sign in remotely: the session continues and the indicator panel appears on the desktop.
5. Lock (Win+L) and unlock remotely. Trigger a UAC prompt and answer it remotely. Sign out: the controller is disconnected, and reconnecting shows the sign-in screen again.
6. `... disable-unattended`: new connections are refused. Elevated `uninstall-windows.ps1`: the service, files and HKLM keys are gone.
7. Check Event Viewer (Application, source `RemoteConnect.HostService`) and `C:\Windows\System32\config\systemprofile\AppData\Local\RemoteConnect\logs` for errors.

## Not implemented yet (planned)

| Item | Milestone | Notes |
|---|---|---|
| Ctrl+Alt+Del (Secure Attention Sequence) | M4 | Service mode can see and operate the sign-in, lock and UAC screens, but cannot send Ctrl+Alt+Del yet. That needs `SendSAS` from the service plus a new, narrowly validated IPC message. It only matters where policy requires Ctrl+Alt+Del at sign-in. |
| Service mode: settings from the app's UI, clipboard | M4 | Today an administrator runs `RemoteConnect.HostService.exe set-password` and similar commands; an elevated helper on the Settings page is planned. Clipboard sync is off in service mode. |
| Service mode in the Inno Setup installer and single-file build | M4 | Only `install-windows.ps1 -ServiceMode` installs the service. `deployment/Windows/RemoteConnect.iss` is per-user only, and `publish-single-file.ps1` does not include the service. |
| Hardware H.264 + DXGI Desktop Duplication / ScreenCaptureKit | M2 | Current codec is tile-JPEG over TLS. It's fine on LAN and moderate on WAN, with higher bandwidth than H.264 for full-motion video. |
| WebRTC ICE/STUN hole punching + coturn TURN | M3 | The built-in WebSocket relay covers connectivity today; direct internet P2P is not attempted. |
| File transfer | M5 | Permission flag exists, but is never granted until implemented. |
| User accounts, organizations, RBAC, MFA, OIDC, PostgreSQL/EF Core | M4/M5 | The server uses a shared registration key + file-based registry/audit. The spec's full identity backend is not built. |
| Address book / device groups, session recording | M5 | Recording intentionally absent. |
| Signed auto-update | M6 | Installer is signable; no updater. |
| Separate projects named in the spec (Media.Native/Interop, Server.Application/Domain/Infrastructure, E2E/Security test projects) | — | Not created as empty placeholders, per spec §5. They will be added with the features above. `RemoteConnect.HostService` and `RemoteConnect.LocalIpc` now exist. The session agent is `RemoteConnect.Desktop.exe --agent`, not a separate project, so the always-visible session panel can reuse the app's UI. |

## Known limitations and security considerations

- First contact by ID through a **compromised server** could be man-in-the-middled, because the client trusts the server's fingerprint for a never-seen ID. Pins detect it on later connections. See SECURITY.md.
- 9-digit IDs are short. Grinding an RSA key to collide with an *unpinned* LAN device's ID is theoretically possible at very high cost.
- Elevated Windows apps don't accept remote input (UIPI). macOS needs explicit permissions. Both are by design.
- `CGDisplayCreateImage` is deprecated by Apple; ScreenCaptureKit migration is planned.
- Settings are user-writable. Local malware running as the user is out of scope.

## Next milestone (recommended order)

1. **Two-machine acceptance runs** (Win↔Win, Win↔Mac) using TEST_PLAN.md; fix findings. *Needs physical machines.*
2. **Windows service mode (M4): verify on a real machine** with the reboot checklist above (needs an administrator) and fix what it finds. Then add Ctrl+Alt+Del via `SendSAS`, an elevated settings helper in the app, and the service option in the Inno Setup installer.
3. **M2 media:** DXGI Desktop Duplication + Media Foundation H.264, and ScreenCaptureKit + VideoToolbox, behind `IScreenCapturer` / new `IVideoEncoder`.
4. **Retarget to .NET 10** and update Avalonia/SkiaSharp.
