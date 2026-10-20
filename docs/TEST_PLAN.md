# Test plan

## Automated (CI-executable)

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\smoke-test.ps1
```

| Suite | What it covers |
|---|---|
| `RemoteConnect.UnitTests` (62) | **Identity:** ID derivation and format, fingerprint/ID consistency, PKCS#12 round-trip, registration signature, password hashing and strength.<br>**Protocol:** framing (round-trip, oversize, truncation, malformed JSON), binary input messages, FrameUpdate bounds checks.<br>**Codec:** tile diff, edge clipping, rect merging, JPEG round-trip, key frames, geometry fit.<br>**Input:** key map completeness and uniqueness, `SendInput` struct layout, virtual-desktop math with negative monitor origins.<br>**Parsing and policy:** connect-target and server-address parsing, unattended lockout (per device + global), host unattended policy, LAN announcement validation (spoofed ID rejected), LaunchAgent plist escaping. |
| `RemoteConnect.IntegrationTests` (16) | Real TCP + mutual TLS on loopback. **Sessions and permissions:** approved session (frames, pointer/key/scroll arrive, disconnect releases keys), view-only (no input injected), host revokes control mid-session, reject, busy.<br>**Identity and unattended access:** wrong pinned fingerprint refused, unattended password (wrong → attempts left, right → limited permissions), unattended refused when off and the request times out, TOFU pin then silent reconnect.<br>**Server (real Kestrel on a random port):** health/info, admin auth, wrong registration key rejected, resolve unknown ID.<br>**Relay:** full session through the relay with end-to-end TLS, frames and input; audit contains no secrets. Relay alone: both join orders, token rejection and single use. |
| Opt-in | `RC_TEST_REAL_CAPTURE=1`: captures one real frame with GDI and checks its size and that it is non-black. The frame stays in memory only. |

Tests never inject real input: they use a recording injector and a synthetic screen.

## Manual two-machine acceptance

Record each run as: **date, machines/OS, network, steps, actual result, verdict**.

| # | Setup | Steps | Expected |
|---|---|---|---|
| 1 | Win↔Win, same LAN, no server | A connects to B's ID (found under *Local network*); B clicks Accept | B's screen on A within 2 s; A's mouse/keyboard control B; B's indicator visible with timer |
| 2 | same | B clicks **View only** | A sees the screen; clicks and keys have no effect |
| 3 | same | During a session B unticks *Allow mouse & keyboard* | A shows "View only"; input stops immediately |
| 4 | same | B clicks **Disconnect** while A holds a mouse button / Shift | Session ends on both; no stuck button or key on B |
| 5 | same | A connects by B's IP for the first time | Trust prompt shows B's ID; after accepting, the next connect has no prompt |
| 6 | Win↔Mac and Mac↔Win | Repeat 1 | Correct colours and pointer position on Retina and on scaled Windows; ⌘/Win and ⌥/Alt map physically; text typing works |
| 7 | Mac host, first run | Connect without permissions granted | Wallpaper-only image and ignored input, and the host UI shows the *Grant permissions* hint; after granting and restarting, works |
| 8 | Different networks + server | A (home) → B (office) by ID | Toolbar shows **Relay**; latency acceptable; server audit has RelayRequested/Started/Ended |
| 9 | same | Server stopped mid-session | Session continues if direct; relayed session ends cleanly within 30 s; the client sidebar shows the server offline and retries |
| 10 | B with unattended **off** | Nobody accepts | Auto-rejected after 30 s; A sees "did not respond" |
| 11 | B with unattended **on** + password | A enters a wrong password 5×, then the right one | Attempts count down, lockout message; after 5 min the correct password connects; B shows "Unattended access (password)" in the indicator |
| 12 | B: allow-list = A's ID only | C tries the password | C is told unattended is not enabled for its device |
| 13 | Reboot B (auto-start on) | Sign in to B, don't open anything | Tray icon present; A can connect (attended or unattended) |
| 14 | Multi-monitor B (one with negative offset, different DPI) | Switch monitor in A's toolbar | Image switches; clicks land exactly under A's pointer on every monitor |
| 15 | B locks screen / UAC prompt | – | Image freezes (secure desktop is not captured); input is refused by Windows; session recovers after unlock |
| 16 | Clipboard | Copy text on A → paste on B and vice versa | Works only while Clipboard is allowed; text present before the session is not sent |
| 17 | Reinstall B (`-RemoveUserData`) | A connects to B's new ID; then the old ID | New ID works; the old ID is reported unknown/offline, never connected to a different key silently |
| 18 | Uninstall B | – | No tray icon after next sign-in; B unreachable; Run key / LaunchAgent removed |

## Failure scenarios to exercise

- Server offline at client start (LAN must still work).
- Expired or invalid server TLS certificate (the client must refuse, never ignore the error).
- Strict NAT on both sides (relay path).
- Packet loss and high latency (`clumsy` on Windows, Network Link Conditioner on macOS): frame rate adapts, no growing lag.
- Host sign-out mid-session.
- Display resolution change and monitor unplug mid-session.
- DPI mismatch between controller and host.
- Two controllers at once: the second gets "busy".
- Device key rotation (delete the identity file): the controller shows the identity-change warning.
