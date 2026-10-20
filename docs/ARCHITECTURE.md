# Architecture

## Processes

| Process | Runs on | Role |
|---|---|---|
| `RemoteConnect.Desktop` | every Windows/macOS computer, as the signed-in user | UI, tray, and both the **controller** and **host** roles. Starts at sign-in. |
| `RemoteConnect.Server` | any server (Windows service, Linux systemd, or `dotnet run`) | Device registry, ID lookup, signaling (SignalR), WebSocket **relay**, audit log, admin API. Optional for LAN use. |
| `RemoteConnect.HostService` | Windows, **service mode only** (opt-in), Session 0 as LocalSystem | Starts at boot and keeps the session agent running in the active console session. Also the administrator CLI for service-mode settings. |
| `RemoteConnect.Desktop --agent` | Windows, **service mode only**, the console session as privilege-free LocalSystem | Headless host (HostAgent + SignalingClient + LanDiscovery) with the machine's own ID. It follows the input desktop, so the sign-in, lock and UAC screens work. No main window or tray; only the session panel. |

By default the host runs inside the signed-in user's session, the same way AnyDesk runs when it is not installed as a service. [Service mode](#service-mode-windows) adds the spec's Session-0 service and per-session agent, so the computer is reachable from power-on.

## Libraries

```
RemoteConnect.Protocol   ← no dependencies. Message framing, message types, FrameUpdate, signaling DTOs.
RemoteConnect.Security   ← device identity (RSA-3072 + self-signed X.509), 9-digit ID derivation,
                           SPKI fingerprints, TLS options (mutual TLS + pinning), PBKDF2 password hashing,
                           DPAPI/0600 secret storage.
RemoteConnect.Platform   ← IScreenCapturer / IInputInjector / IAutoStart and their implementations:
                           Windows: GDI BitBlt capture, SendInput, HKCU Run key
                           macOS:   CGDisplayCreateImage capture, CGEvent input, LaunchAgent
RemoteConnect.Session    ← HostAgent/HostSession, ControllerSession, Connector, TileEncoder/RemoteFramebuffer,
                           PeerConnection/PeerConnector (TCP or WebSocket + TLS), LanDiscovery, SignalingClient.
RemoteConnect.LocalIpc   ← service ↔ agent pipe: two typed messages (heartbeat, status) with strict validation,
                           pipe ACL + peer-process checks, service-mode paths/settings, public status (HKLM).
RemoteConnect.Desktop    ← Avalonia 11 UI (MVVM, CommunityToolkit.Mvvm), composition root (AppHost);
                           --agent: service-mode composition root (AgentHost).
RemoteConnect.HostService← Windows service: agent supervisor (pure state machine + Win32 launcher), admin CLI.
RemoteConnect.Server     ← ASP.NET Core minimal API + SignalR hub + relay broker.
```

## Identity

Each installation generates an RSA-3072 key pair on first run and stores it as PKCS#12. On Windows it is protected with DPAPI (CurrentUser); on macOS it is a mode-0600 file.

- **Fingerprint** = SHA-256(SubjectPublicKeyInfo), hex. This is the value that gets pinned.
- **Device ID** = first 8 bytes of that same hash, mapped to 100000000–999999999. The ID is an *address*, not a credential: anyone can learn it. Because it is derived from the key fingerprint, an (ID, fingerprint) pair can be checked for consistency without the certificate.

## Connection flow (controller → host)

```
user types 482 913 207
        │
        ├─► LAN discovery has it? ──► direct TCP + TLS, pinned to the announced fingerprint
        │                              (announcement must be self-consistent: ID == f(fingerprint))
        ├─► server online? ──► Resolve(id) → fingerprint + LAN endpoints
        │        ├─► try LAN endpoints in parallel (2 s) ──► direct TCP + TLS, pinned
        │        └─► RequestRelay(id) → server sends RelayOffer to host
        │                 controller ─ws─► /relay/{session} ◄─ws─ host
        │                 TLS runs end-to-end inside the relayed byte stream (host = TLS server)
        └─► IP address typed ──► direct TCP + TLS; first time: user confirms ID/fingerprint (TOFU), then pinned
```

After TLS, both sides know each other's key. The host derives the controller's device ID from the controller's client certificate, and rejects a `Hello` whose claimed ID differs from it.

## Session protocol

Framing is `[u32 length][u8 type][payload]` inside TLS, with a maximum of 16 MB per message. Control messages are JSON; hot-path messages are fixed binary.

```
C → H  Hello {version, deviceId, alias, platform, requested permissions}
H → C  HelloResult PendingApproval {unattendedAvailable}
           … the host user is asked (30 s), or the controller sends PasswordAuth (unattended) …
H → C  AuthStatus {success, attemptsRemaining, lockedOut}        (password path only)
H → C  HelloResult Accepted {granted permissions}  |  Rejected / TimedOut / Busy / NotAllowed / …
H → C  ScreenLayout {monitors, active}
H → C  FrameUpdate {seq, w, h, rects[x,y,w,h,JPEG]}    ◄── at most 2 unacknowledged
C → H  FrameAck {seq}
C → H  Pointer {action, button, x/65535, y/65535} | Scroll | Key {HID usage, down}
C ↔ H  ClipboardText (only if granted), Ping/Pong (RTT + liveness), Disconnect {reason}
H → C  PermissionsChanged {granted}                     (host toggled control/clipboard)
C → H  SelectMonitor, SetQuality
```

- **Permissions are enforced on the host for every message.** The controller-side checks only exist for user experience.
- **Coordinates** are normalized to the active remote monitor (0–65535). The host maps them to physical pixels on Windows (handling the virtual desktop and negative monitor offsets), or to global points on macOS. DPI and scaling differences therefore cancel out.
- **Keys** travel as USB HID usages (physical keys). They map to Windows set-1 scan codes and to macOS `kVK_*` codes, so the host's keyboard layout decides the characters.
- **Liveness:** each side pings and ends the session after 30 s of silence, so a dead transport can't keep injecting input. On every disconnect, permission change and monitor switch, the host releases any keys and buttons still held.

## Media pipeline (M1)

```
capture (GDI / CoreGraphics, scaled to the quality preset)
   → TileEncoder: 64×64 tile diff vs previous frame → merge into rects → JPEG per rect (SkiaSharp, parallel)
   → FrameUpdate over TLS → controller decodes rects straight into its framebuffer (SkiaSharp, parallel)
   → Avalonia WriteableBitmap → letterboxed render
```

A static screen costs almost nothing, and typing changes only a few tiles. Flow control (2 frames in flight plus a frame-interval cap) adapts the frame rate to the link and the decoder. The quality presets are:

| Preset | Max size | Quality | FPS cap |
|---|---|---|---|
| Speed | 1366×768 | 45 | 15 |
| Balanced | 1920×1080 | 65 | 20 |
| Quality | 2560×1600 | 85 | 25 |

Planned M2: DXGI Desktop Duplication (Windows) and ScreenCaptureKit (macOS) capture, plus hardware H.264, behind the same `IScreenCapturer`/FrameUpdate interfaces.

## Service mode (Windows)

```
power-on ─► SCM starts RemoteConnect.HostService (LocalSystem, Session 0)
              │  every 1 s + on SERVICE_CONTROL_SESSIONCHANGE: WTSGetActiveConsoleSessionId()
              ▼
        AgentSupervisorStateMachine ── LaunchAgent(session) / StopAgent(pid, graceful|force)
              │
              │ 1. install-folder ACL check (only SYSTEM / Administrators / TrustedInstaller may own or write)
              │ 2. pipe \\.\pipe\RemoteConnect.HostService.v1, first instance only, ACL: LocalSystem; deny NETWORK
              │ 3. own token → CreateRestrictedToken(DISABLE_MAX_PRIVILEGE) → TokenSessionId = console session
              │ 4. CreateProcessAsUser("…\RemoteConnect.Desktop.exe" --agent, desktop winsta0\default)
              ▼
        session agent (console session, LocalSystem without privileges)
              │ connects to the pipe (SecurityIdentification), checks the server is the service binary as LocalSystem;
              │ the service checks the client is exactly the PID it launched, from the expected path
              │ agent → service: Status {ID, fingerprint, state, input desktop, port, server, sessions, unattended ready}
              │ agent ↔ service: Heartbeat every 5 s (30 s of silence → the agent exits / the service terminates it)
              ▼
        HostAgent (password-only policy) + SignalingClient + LanDiscovery, machine ID and settings
        capture / input on dedicated threads: OpenInputDesktop → SetThreadDesktop before every call
              (Winlogon at the sign-in, lock and UAC screens; Default after sign-in)
```

- **One ID per computer.** The agent has its own key in the LocalSystem profile. When the service is installed, the per-user app stops hosting: no LAN listener, no announcements, relay offers refused. It shows the service's ID from `HKLM\SOFTWARE\RemoteConnect\Service`, so the two never compete for port 47070.
- **Password-only.** `HostOptions.AttendedApprovalAvailable = false`. Controllers that cannot use the unattended password are refused at once with `NotAllowed`; the others get `PendingApproval {PasswordOnly}` and must send the password. No prompt is ever shown.
- **Session switches.** The state machine stops the old agent first (graceful: the service closes the pipe, and the agent ends its sessions and exits; forced after 8 s), then launches into the new console session. A transient "no console session" during a switch is ignored. Crashes, failed launches and silent agents are relaunched with backoff of 2, 4, 8 … up to 60 s.
- **Settings** are administrator-only (`RemoteConnect.HostService.exe set-password | disable-unattended | allow | …`). The agent re-reads them every 5 s; network changes apply without a restart.

## Server

- **SignalHub** (`/hubs/signal`) provides:
  - `GetChallenge`, then `Register`. The client proves possession of its key by signing a single-use 60-second challenge, and the server checks that the ID equals f(certificate).
  - `UpdateEndpoints`
  - `Resolve`, rate-limited per device
  - `RequestRelay`, rate-limited per device
- **RelayBroker** (`/relay/{id}`):
  - A WebSocket pair per session, with separate single-use 256-bit tokens per side, sent in a header and stored only as SHA-256 hashes.
  - A 30-second pairing timeout, a concurrency cap and an optional byte cap.
  - Bytes are forwarded blindly; they are TLS records.
- **DeviceRegistry:**
  - The first key to register an ID owns it, persisted in `data/devices.json`.
  - Presence is kept in memory.
  - Admin revocation is available via the admin API.
- **AuditLog** writes daily JSON-lines files in `data/audit` with configurable retention. It records who, what, when and from where, never secrets or content.
- **Health:** `/health/live` and `/health/ready` (the data directory is writable).
