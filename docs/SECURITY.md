# Security

## Principles

1. **Consent and visibility.** Someone at the host must accept each attended session. Unattended access is **off by default**: the local user has to set a password and enable it explicitly, and can restrict it to listed device IDs. Every active session shows an always-on-top panel the controller cannot hide. From that panel the host user can disconnect, or turn off control or clipboard, instantly.
2. **End-to-end encryption.** Every session is mutual TLS (1.2+, and 1.3 where the OS offers it) directly between the two devices. Through the relay the server only forwards TLS records. No proprietary cryptography is used.
3. **Strong device identity.** Each device has an RSA-3072 key. Peers pin the SHA-256 SPKI fingerprint, and the human-readable ID is just an address.
4. **Least privilege.**
   - The desktop app runs as the signed-in user with `asInvoker`.
   - On Windows it does not bypass UAC, the secure desktop or the lock screen, and windows running elevated reject its input (UIPI).
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
| **Privilege escalation via the app** | No service, no elevation, no "run command" message. Input goes through documented OS APIs and respects UIPI and secure-desktop rules. | — |
| **Stuck input after disconnect** | The host releases held keys and buttons on disconnect, permission change and monitor switch; liveness timeout is 30 s. | — |
| **Local malware on the host** | Out of scope. Malware running as the user could edit settings (including the unattended password hash) or drive the UI, as with any desktop software. | Planned: OS keychain or DPAPI-protected settings, plus a signed-binary check. |
| **Tampered installer or update** | Installer scripts are signable (Inno Setup + signtool; codesign + notarization on macOS). | Auto-update with signature verification is not built yet. |

## Data at rest

| Item | Windows | macOS |
|---|---|---|
| Device private key | `%APPDATA%\RemoteConnect\identity.dpapi` (DPAPI, CurrentUser) | `~/Library/Application Support/RemoteConnect/identity.p12` (0600) |
| Server registration key | `registration.key` (DPAPI) | `registration.key` (0600) |
| Settings, pinned keys, recent sessions | `settings.json` | `settings.json` (0600) |
| Unattended password | PBKDF2 hash only, in settings | same |

The server stores ID→fingerprint bindings and audit events. Audit events record who connected to whom, when, from which address and the relay byte count — never content.

## Security review checklist (before a release)

- [ ] `dotnet list package --vulnerable` is clean; dependency inventory updated (Avalonia, SkiaSharp, CommunityToolkit.Mvvm, SignalR client, System.Security.Cryptography.ProtectedData, Microsoft.Extensions.Hosting.WindowsServices).
- [ ] Server: HTTPS only (reverse proxy or Kestrel certificate), `RegistrationKey` set, `AdminApiKey` set or empty (disabled), `TrustedProxies` correct.
- [ ] Binaries signed (Authenticode / Developer ID + notarization).
- [ ] Manual checks: unattended is off after a fresh install; the indicator stays visible and topmost; Disconnect works mid-drag (no stuck buttons); a host-side revoke takes effect immediately.
- [ ] Logs reviewed for secrets.
