# Deploying the server on Render

Render runs the server as an always-on Docker web service. HTTPS on `https://<name>.onrender.com` and WebSockets are included, so the clients can use it from any network.

**Why not Vercel:** Vercel only runs short-lived serverless functions. RemoteConnect needs long-lived WebSocket connections: every device stays connected to receive requests, and the relay streams the session for as long as it lasts. Use Render, Railway, Fly.io, Azure App Service or a small VM instead.

## Plan choice

| Plan | Behaviour | Use for |
|---|---|---|
| **Free** (`render.yaml` default; no card needed) | Spins down after 15 minutes with **no** inbound traffic. Render counts WebSocket messages on open connections as traffic, and every running RemoteConnect app sends a keep-alive every 15 s. So the server stays awake while at least one of your devices is on. If every device is off, it sleeps; the first device to come back wakes it in about a minute and reconnects on its own. | Personal use and small teams. |
| Starter (paid) | Always on; persistent disk available. | Guaranteed availability, larger teams. |

**Free plan limits** (see https://render.com/docs/free):
- **750 instance hours per month** per workspace, enough for one service running all month. Don't run other free services in the same workspace, or the hours may run out and the service is suspended until next month.
- **Outbound bandwidth is limited.** Relayed sessions use roughly 0.2–8 Mbit/s while active; sessions on the same LAN connect directly and use none. Without a payment method on file, Render **suspends free services for the rest of the month** when the allowance is exceeded.
- There's no persistent disk. Device bindings are rebuilt automatically after a restart.

To switch plans, change `plan:` in `render.yaml` and push.

## Steps

1. **Put the `Code` folder in a Git repository** on GitHub or GitLab (private is fine). Render deploys from Git. `render.yaml` must be at the repository root, next to `RemoteConnect.sln`.
2. In Render go to **New → Blueprint**, connect the repository and select it. Render reads `render.yaml` and creates the service `remoteconnect-server`. Optionally set the region nearest you in `render.yaml` first (singapore, frankfurt, oregon, ohio or virginia).
3. Enter any **Blueprint Name** (e.g. `remoteconnect`). When asked for `RemoteConnect__RegistrationKey`, enter a long random value, for example from PowerShell: `[guid]::NewGuid().ToString() + [guid]::NewGuid().ToString()`. Keep it; every computer needs it.
4. Click **Apply** and wait for the first build and deploy (a few minutes).
5. Verify in a browser:
   - `https://<your-service>.onrender.com/health/ready` → `Healthy`
   - `https://<your-service>.onrender.com/api/v1/info` → `"registrationKeyRequired": true`
6. On **every** computer, open RemoteConnect and go to **Settings → Server**:
   - Server address: `https://<your-service>.onrender.com`
   - Registration key: the key from step 3

   Click **Save**. The left panel turns green: "Ready — online via server".
7. Connect by 9-digit address from anywhere. The session toolbar shows **Relay** when it goes through Render.

## Settings used on Render

Defined in `render.yaml` and `deployment/Server/Dockerfile`:

| Variable | Value | Why |
|---|---|---|
| `PORT` | `10000` (set by Render) | The server listens on `$PORT`. |
| `RemoteConnect__RegistrationKey` | entered by you | Only your devices can register. |
| `RemoteConnect__AdminApiKey` | generated | Admin API (`/api/v1/admin/*`, header `X-Admin-Key`). Copy it from the Render dashboard → Environment. |
| `RemoteConnect__TrustForwardedHeaders` | `true` | Render's proxy is the only way in; rate limits and the audit log then see the real client IPs. |
| `RemoteConnect__DataDirectory` | `/var/data` | Device bindings and audit files. |

**Persistence:** without a disk, device ID bindings are rebuilt automatically when devices reconnect after a restart or deploy, and audit events also appear in Render's **Logs** tab. To keep the files across deploys, uncomment the `disk:` block in `render.yaml` (paid plans).

## Verify the deployed server end to end (optional)

From a development machine with the source:

```powershell
$env:RC_TEST_SERVER_URL = "https://<your-service>.onrender.com"
$env:RC_TEST_SERVER_KEY = "<registration key>"
$env:RC_TEST_ADMIN_KEY  = "<admin key from Render>"     # optional
dotnet test tests\RemoteConnect.IntegrationTests --filter "FullyQualifiedName~ServerRelayTests"
```

This registers two temporary test devices, looks one up, and runs a complete session through the Render relay with end-to-end TLS. It uses a synthetic screen and injects no real input.

## Updating

Push to the repository's main branch; Render rebuilds and redeploys automatically (`autoDeploy: true`). Connected devices reconnect on their own within about a minute.

## Same container elsewhere

```bash
docker build -f deployment/Server/Dockerfile -t remoteconnect-server .
docker run -d -p 5080:10000 -e RemoteConnect__RegistrationKey=<key> remoteconnect-server
```

Put HTTPS in front (Caddy, nginx or the platform's proxy). Clients only accept `http://` for local and private-network addresses.
