# Status

**Version 0.1.0 — 2026-10-20.** Milestone M1 (attended LAN + internet relay vertical slice) is implemented and tested in code. Parts of M3, M4 and M5 are also implemented. Two-machine manual acceptance (docs/TEST_PLAN.md) has **not** been run yet.

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

### Build and test evidence (2026-10-20, Windows 11 Pro 10.0.26300, .NET SDK 8.0.424)

```
dotnet build RemoteConnect.sln            → 0 warnings, 0 errors
dotnet test  RemoteConnect.sln            → UnitTests 62/62 passed, IntegrationTests 16/16 passed
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

## Not implemented yet (planned)

| Item | Milestone | Notes |
|---|---|---|
| Access at the Windows sign-in / lock screen; reboot without sign-in | M4 | Needs `RemoteConnect.HostService` (Session-0 service) + per-session agent + authenticated named-pipe IPC, per spec §4. Today the host runs after the user signs in. |
| UAC / secure-desktop control, Ctrl+Alt+Del | M4 | Requires the privileged service design above (SendSAS); never via shortcuts. |
| Hardware H.264 + DXGI Desktop Duplication / ScreenCaptureKit | M2 | Current codec is tile-JPEG over TLS. It's fine on LAN and moderate on WAN, with higher bandwidth than H.264 for full-motion video. |
| WebRTC ICE/STUN hole punching + coturn TURN | M3 | The built-in WebSocket relay covers connectivity today; direct internet P2P is not attempted. |
| File transfer | M5 | Permission flag exists, but is never granted until implemented. |
| User accounts, organizations, RBAC, MFA, OIDC, PostgreSQL/EF Core | M4/M5 | The server uses a shared registration key + file-based registry/audit. The spec's full identity backend is not built. |
| Address book / device groups, session recording | M5 | Recording intentionally absent. |
| Signed auto-update | M6 | Installer is signable; no updater. |
| Separate projects named in the spec (HostService, SessionAgent, Media.Native/Interop, Server.Application/Domain/Infrastructure, LocalIpc, E2E/Security test projects) | — | Not created as empty placeholders, per spec §5. They will be added with the features above. |

## Known limitations and security considerations

- First contact by ID through a **compromised server** could be man-in-the-middled, because the client trusts the server's fingerprint for a never-seen ID. Pins detect it on later connections. See SECURITY.md.
- 9-digit IDs are short. Grinding an RSA key to collide with an *unpinned* LAN device's ID is theoretically possible at very high cost.
- Elevated Windows apps don't accept remote input (UIPI). macOS needs explicit permissions. Both are by design.
- `CGDisplayCreateImage` is deprecated by Apple; ScreenCaptureKit migration is planned.
- Settings are user-writable. Local malware running as the user is out of scope.

## Next milestone (recommended order)

1. **Two-machine acceptance runs** (Win↔Win, Win↔Mac) using TEST_PLAN.md; fix findings. *Needs physical machines.*
2. **Windows service mode (M4):**
   - `RemoteConnect.HostService`: SCM install/uninstall, `WTSGetActiveConsoleSessionId` / `CreateProcessAsUser` agent launch.
   - `RemoteConnect.LocalIpc`: named pipe with an ACL for the SYSTEM + user SID, and a peer-process identity check.
   - `OpenInputDesktop` / `SetThreadDesktop` for sign-in screen capture and input.
   - An explicit installer option for this mode.
3. **M2 media:** DXGI Desktop Duplication + Media Foundation H.264, and ScreenCaptureKit + VideoToolbox, behind `IScreenCapturer` / new `IVideoEncoder`.
4. **Retarget to .NET 10** and update Avalonia/SkiaSharp.
