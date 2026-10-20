# Security

## Principles

1. **Consent and visibility.** Someone at the host must accept each attended session. Unattended access is **off by default**: the local user has to set a password and enable it explicitly, and can restrict it to listed device IDs. Every active session shows an always-on-top panel the controller cannot hide. From that panel the host user can disconnect, or turn off control or clipboard, instantly. In [service mode](#service-mode-windows) nobody can answer a prompt at the sign-in screen, so the opt-in unattended password is the only way in. An administrator has to turn it on, and can turn it off again with one command.
2. **End-to-end encryption.** Every session is mutual TLS (1.2+, and 1.3 where the OS offers it) directly between the two devices. Through the relay the server only forwards TLS records. No proprietary cryptography is used.
3. **Strong device identity.** Each device has an RSA-3072 key. Peers pin the SHA-256 SPKI fingerprint, and the human-readable ID is just an address.
4. **Least privilege.**
   - The desktop app runs as the signed-in user with `asInvoker`.
   - On Windows it does not bypass UAC, the secure desktop or the lock screen, and windows running elevated reject its input (UIPI).
   - Service mode is a separate, explicit, administrator-installed option. Its session agent runs as LocalSystem, because only LocalSystem may use the Winlogon desktop, but with **every privilege removed** from its token. See [Service mode](#service-mode-windows).
   - On macOS it needs the user's explicit Screen Recording and Accessibility grants.
   - The server runs as a virtual service account.
5. **No secrets in code or logs.** Registration and admin keys come from environment variables or user secrets. Logs never contain passwords, tokens, clipboard text or screen data.

## Where trust comes from

| Path | How the controller knows it reached the right device | How the host knows who connects |
|---|---|---|
| ID via server | The server bound the ID to a key, and the device proved possession by signing a challenge. The controller pins that fingerprint. | The client certificate in TLS; the ID is derived from it, so a claimed ID must match. |
| ID via LAN discovery | Announcement must be self-consistent (ID = f(fingerprint)), and the TLS key must match the announced fingerprint. Previously pinned keys take precedence. | Same as above. |
| IP address | Trust on first use: the user confirms the ID shown on the other screen. The key is then pinned, and a later change produces a blocking warning. | Same as above. |
| Relay | Same pinning as "ID via server"; the relay cannot read or modify the stream. | The host also checks that the TLS peer equals the controller the server announced. |

## Threat model (summary)

| Threat | Mitigation | Residual risk |
|---|---|---|
| **Session hijacking** (taking over an established session) | All traffic is inside mutual TLS. Relay tokens are random, single-use, hash-stored and short-lived. | None known at the protocol level. |
| **Device spoofing on the LAN** (fake announcements) | Announcements are only hints. The TLS key must match the fingerprint, which must match the ID, and pinned keys win. | An attacker who grinds an RSA key whose 30-bit ID collides with a target can impersonate an *unpinned* device on the LAN. That costs on the order of 2³⁰ RSA-3072 key generations (CPU-years). Mitigations: pin by connecting once, or use the server path. |
| **Malicious or compromised server** | The server never sees session content. Pins from earlier connections detect a key swap. | A malicious server could answer `Resolve` for a *never-before-seen* ID with its own key: a man-in-the-middle on first contact. Planned: show the fingerprint on first connect by ID, plus organisation-signed device certificates. |
| **Malicious relay** | It sees only ciphertext and byte counts; TLS integrity protects against modification. | Traffic analysis such as timing and volume. |
| **Replay** | TLS protects sessions. Server registration uses a single-use, 60-second challenge. Relay tokens are single-use. | — |
| **Password brute force** (unattended) | PBKDF2-SHA256 with 310k iterations. 5 failures per device ID → 5-minute lockout. 20 failures globally per window → global lockout, because new IDs are cheap to mint. Passwords travel only inside TLS to the pinned host. | An attacker can cause a 5-minute global lockout (accepted trade-off). |
| **ID scanning / spam** | Server registration key, per-device rate limits on `Resolve`/`RequestRelay`, per-IP connection limits. | — |
| **Privilege escalation via the app** | Per-user install: no service, no elevation, no "run command" message. Input goes through documented OS APIs and respects UIPI and secure-desktop rules. Service mode: see the next section. | — |
| **Stuck input after disconnect** | The host releases held keys and buttons on disconnect, permission change and monitor switch; liveness timeout is 30 s. | — |
| **Local malware on the host** | Out of scope. Malware running as the user could edit settings (including the unattended password hash) or drive the UI, as with any desktop software. | Planned: OS keychain or DPAPI-protected settings, plus a signed-binary check. |
| **Tampered installer or update** | Installer scripts are signable (Inno Setup + signtool; codesign + notarization on macOS). | Auto-update with signature verification is not built yet. |

## Service mode (Windows)

`install-windows.ps1 -ServiceMode` (administrator only) installs `RemoteConnect.HostService`. It runs as LocalSystem and starts `RemoteConnect.Desktop.exe --agent` in the active console session. The agent hosts sessions with the machine's own device key and follows the input desktop: sign-in, lock and UAC screens, then the user's desktop. It is the only part of RemoteConnect that runs with more than user rights, so it is built to be narrow:

- **The service never runs anything it is told to.** It starts one fixed program from its own folder with one fixed argument. Nothing received over IPC, from the network or from a user changes what it starts or how.
- **The service ↔ agent pipe** (`\\.\pipe\RemoteConnect.HostService.v1`) carries only two message types, heartbeat and status. Status flows agent → service only. Every frame is size-limited and strictly decoded: unknown types, unknown or missing fields, numeric enums and out-of-range values all drop the connection. The service uses a status message only as a sign of life, and copies its non-secret fields into the public status.
- **Least privilege for the agent.** The agent's token is the service's LocalSystem token run through `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)`, so it has no SeDebug, SeTcb, SeImpersonate, SeAssignPrimaryToken, SeBackup or similar privileges. It keeps the LocalSystem identity, which the Winlogon desktop requires. The agent exits as soon as its pipe to the service closes, and refuses to start unless it is LocalSystem and has reached the genuine service.
- **Consent and visibility are unchanged.** No approval prompt is ever shown in service mode, so an accidental click at the sign-in screen cannot grant access. Unattended access is off until an administrator runs `set-password`, honours the allow-list and the per-device and global lockouts, and is turned off with `disable-unattended` or by uninstalling. The always-on-top session panel appears on the user's desktop as soon as someone signs in. Every session start and end is logged.

| Threat | Mitigation | Residual risk |
|---|---|---|
| **Privilege escalation by replacing the agent binary or a DLL** (it runs as LocalSystem) | Service mode installs only to `%ProgramFiles%\RemoteConnect`. The installer sets the owner to Administrators and resets the folder to inherited Program Files permissions. Before every launch the service checks the folder and every file in it: if anything is owned by, or writable by, anyone other than SYSTEM, Administrators or TrustedInstaller, it refuses to start the agent and logs why. A per-user or development folder always fails this check. | An administrator can still replace the files (administrators are trusted). Binaries are not yet Authenticode-signed or verified (planned). |
| **Pipe squatting**: another process creates the pipe name first, to receive the agent's connection or to impersonate it | The service creates the pipe with `FILE_FLAG_FIRST_PIPE_INSTANCE` and a single instance; if the name is already taken it refuses to launch the agent and logs a warning. The agent connects with `SecurityIdentification`, so a squatter can never impersonate the LocalSystem client. Before sending anything, the agent verifies the server via `GetNamedPipeServerProcessId`: it must run as LocalSystem from `RemoteConnect.HostService.exe` in the agent's own folder. | A process already running as LocalSystem could interfere, but it already controls the machine. |
| **A rogue client on the pipe** (pretending to be the agent) | The pipe ACL allows only LocalSystem and explicitly denies network logons. The service accepts only the exact process ID it launched, from the expected image path (`GetNamedPipeClientProcessId` + `QueryFullProcessImageName`). Even the real agent can only send heartbeat and status. | — |
| **Remote attacker at the sign-in screen** | The same mutual TLS, key pinning and device identity as every session. There is no prompt to trick anyone into. Only the unattended password grants access: PBKDF2, 5 tries per device, then a global lockout, plus an optional allow-list of controller IDs. | Anyone who has the password can reach the sign-in screen, and from there needs a Windows password to go further. Choose a strong password and use the allow-list. |
| **Settings tampering** (turning on unattended access, swapping the hash or the server) | Settings, device key and logs live in the LocalSystem profile (`C:\Windows\System32\config\systemprofile\AppData\Local\RemoteConnect`), which only SYSTEM and Administrators can open. Standard users can neither read the password hash nor pre-create the folder. A malformed settings file loads as defaults with unattended access off. Changes go through `RemoteConnect.HostService.exe`, which requires an elevated prompt. | Administrators are trusted. |
| **Fake public status** (a user making the desktop app show a wrong ID) | Status is published under `HKLM\SOFTWARE\RemoteConnect\Service`, which only SYSTEM and Administrators can write. | — |
| **Orphaned or stuck agent** | The agent exits when its pipe closes or the service goes quiet for 30 s; the service terminates an agent that stops sending heartbeats or ignores a graceful stop, and on start ends stale LocalSystem agents from its own path. Graceful stops end sessions so held keys and buttons are released. | A forced stop (hung agent) cannot release keys the agent was holding. |
| **Session switch leaks the next user's desktop** | The agent follows the console session. On a switch the old agent is stopped, which ends its sessions, before a new one starts. Sign-out ends the session too. | The new console session (sign-in screen) is reachable again with the password, as intended. |

## Data at rest

| Item | Windows | macOS |
|---|---|---|
| Device private key | `%APPDATA%\RemoteConnect\identity.dpapi` (DPAPI, CurrentUser) | `~/Library/Application Support/RemoteConnect/identity.p12` (0600) |
| Server registration key | `registration.key` (DPAPI) | `registration.key` (0600) |
| Settings, pinned keys, recent sessions | `settings.json` | `settings.json` (0600) |
| Unattended password | PBKDF2 hash only, in settings | same |

Service mode keeps its own data, separate from any user's, in `C:\Windows\System32\config\systemprofile\AppData\Local\RemoteConnect`, which only SYSTEM and Administrators can open:

| Item | Protection |
|---|---|
| Machine device private key (`identity.dpapi`) | DPAPI for the LocalSystem account (only LocalSystem can decrypt it) |
| Server registration key (`registration.key`) | DPAPI LocalMachine scope (written by an administrator, read by LocalSystem) plus the folder ACL |
| Settings with the unattended password hash (`service-settings.json`) | PBKDF2 hash only, plus the folder ACL |
| Agent logs (`logs\`) | folder ACL; no secrets, as in the app |

The server stores ID→fingerprint bindings and audit events. Audit events record who connected to whom, when, from which address and the relay byte count — never content.

## Security review checklist (before a release)

- [ ] `dotnet list package --vulnerable` is clean; dependency inventory updated (Avalonia, SkiaSharp, CommunityToolkit.Mvvm, SignalR client, System.Security.Cryptography.ProtectedData, Microsoft.Extensions.Hosting.WindowsServices).
- [ ] Server: HTTPS only (reverse proxy or Kestrel certificate), `RegistrationKey` set, `AdminApiKey` set or empty (disabled), `TrustedProxies` correct.
- [ ] Binaries signed (Authenticode / Developer ID + notarization).
- [ ] Manual checks: unattended is off after a fresh install; the indicator stays visible and topmost; Disconnect works mid-drag (no stuck buttons); a host-side revoke takes effect immediately.
- [ ] Service mode: unattended is off after `-ServiceMode` install; connections are refused until `set-password`; `disable-unattended` takes effect without a restart; `%ProgramFiles%\RemoteConnect` is Administrators-owned and not user-writable; the service logs "Not starting the session agent" if those permissions are loosened; the agent process holds no privilege except SeChangeNotifyPrivilege (Process Explorer → Security tab); the session panel appears after sign-in during a session.
- [ ] Logs reviewed for secrets.
