# Server API

The base address is your server, for example `https://remote.example.com`. All endpoints are rate-limited per IP address (`ConnectionsPerMinutePerIp`).

## HTTP

| Method & path | Auth | Description |
|---|---|---|
| `GET /health/live` | – | Liveness. Returns 200 while the process runs. |
| `GET /health/ready` | – | Readiness. Returns 200 if the data directory is writable. |
| `GET /api/v1/info` | – | `{ product, version, protocolVersion, registrationKeyRequired, relay }` |
| `GET /api/v1/admin/devices` | `X-Admin-Key` | Registered devices: `deviceId, alias, platform, appVersion, online, firstSeen, lastSeen, fingerprint` (short form). |
| `DELETE /api/v1/admin/devices/{deviceId}` | `X-Admin-Key` | Revokes a device. It forgets the ID→key binding and disconnects the device; the event is audited. |
| `GET /api/v1/admin/audit?deviceId=&since=&limit=` | `X-Admin-Key` | Audit events, newest first. `since` is ISO-8601 (default: 7 days); `limit` is at most 1000. |

The admin endpoints return **404** when `RemoteConnect:AdminApiKey` is not configured, and **401** for a wrong key. The key comparison is constant-time.

Example:

```powershell
Invoke-RestMethod https://remote.example.com/api/v1/admin/audit?limit=20 -Headers @{ 'X-Admin-Key' = $env:RC_ADMIN_KEY }
```

Audit event types:

- `DeviceOnline` / `DeviceOffline`
- `RegisterRejected`
- `RelayRequested` / `RelayStarted` / `RelayEnded` / `RelayExpired`
- `DeviceRevoked`

## SignalR hub `/hubs/signal`

The hub uses WebSockets with the JSON protocol. DTOs are in `src/Shared/RemoteConnect.Protocol/Signaling.cs`.

| Client → server | Request | Response |
|---|---|---|
| `GetChallenge()` | – | `string`: a single-use challenge, valid for 60 s. |
| `Register(RegisterRequest)` | `deviceId, certificateBase64 (DER), challenge, signatureBase64, alias, platform, appVersion, registrationKey?, lanEndpoints[]` | `RegisterResponse { success, error, message, serverTime }` |
| `UpdateEndpoints(string[])` | up to 8 entries of the form `ip:port` | – |
| `Resolve(deviceId)` | – | `ResolveResponse { found, online, deviceId, fingerprint, alias, platform, lanEndpoints[], error }` |
| `RequestRelay(deviceId)` | – | `RelayTicket { success, sessionId, token, hostFingerprint, error }` |

`signature` = RSA-PKCS1-SHA256 over the UTF-8 string `RemoteConnect-Register|v1|{deviceId}|{challenge}`.

The server checks:

- `deviceId == f(SPKI of certificate)`
- the signature
- the challenge
- the registration key, if configured
- that the ID is not already bound to a different key

Every method except `GetChallenge`/`Register` fails until registration has succeeded on that connection.

| Server → client | Payload |
|---|---|
| `RelayOffer` | `{ sessionId, token, controllerId, controllerFingerprint, controllerAlias }`. The host joins the relay as TLS server and expects exactly that controller key. |
| `SessionReplaced` | `string`. The same device registered from another connection, so this one is closed. |

## Relay `GET /relay/{sessionId}` (WebSocket)

- The header `X-RemoteConnect-Relay-Token: <token>` is required. Each side has its own single-use token.
- Responses: `404` unknown session, `403` wrong token, `409` token already used.
- Once both sides have joined, binary frames are forwarded unchanged in both directions. When one side closes, the other is closed.
- If the peer doesn't join within `RelayPairTimeoutSeconds`, the waiting side is closed.

## Configuration reference

| Setting (`appsettings.json` / env var) | Default | |
|---|---|---|
| `Kestrel:Endpoints:Http:Url` / `Kestrel__Endpoints__Http__Url` | `http://0.0.0.0:5080` | Listening address. Put TLS in front, or configure a Kestrel HTTPS endpoint with a certificate. |
| `RemoteConnect:DataDirectory` / `RemoteConnect__DataDirectory` | `data` | Registry and audit logs. |
| `RemoteConnect:RegistrationKey` / `RemoteConnect__RegistrationKey` | empty | **Set it** for any internet-facing server. |
| `RemoteConnect:AdminApiKey` / `RemoteConnect__AdminApiKey` | empty (disabled) | Enables `/api/v1/admin/*`. |
| `RemoteConnect:RelayPairTimeoutSeconds` | 30 | |
| `RemoteConnect:MaxConcurrentRelays` | 200 | |
| `RemoteConnect:MaxRelayBytesPerSession` | 0 (unlimited) | |
| `RemoteConnect:AuditRetentionDays` | 90 | |
| `RemoteConnect:ResolvePerMinute` / `RelayRequestsPerMinute` | 30 / 10 | Per device. |
| `RemoteConnect:ConnectionsPerMinutePerIp` | 120 | |
| `RemoteConnect:TrustedProxies` | `[]` | IPs whose `X-Forwarded-For` header is trusted. |
