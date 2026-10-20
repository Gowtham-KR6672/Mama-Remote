# Architecture

## Processes

| Process | Runs on | Role |
|---|---|---|
| `RemoteConnect.Desktop` | every Windows/macOS computer, as the signed-in user | UI, tray, and both the **controller** and **host** roles. Starts at sign-in. |
| `RemoteConnect.Server` | any server (Windows service, Linux systemd, or `dotnet run`) | Device registry, ID lookup, signaling (SignalR), WebSocket **relay**, audit log, admin API. Optional for LAN use. |

The spec asks for a separate Windows service (Session 0) plus a per-session agent, so the computer is reachable at the sign-in screen. That is not built yet; see [STATUS.md](STATUS.md). Today the host runs inside the signed-in user's session, the same way AnyDesk runs when it is not installed as a service.

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
RemoteConnect.Desktop    ← Avalonia 11 UI (MVVM, CommunityToolkit.Mvvm), composition root (AppHost).
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
