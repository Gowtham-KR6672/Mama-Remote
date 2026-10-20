# Internet setup (self-hosted server)

Computers on different networks find each other through **your** RemoteConnect server.

- The server first offers the computers each other's LAN endpoints; when there is no direct path, it **relays** the encrypted stream.
- Both computers only make outbound HTTPS/WebSocket connections, so no inbound ports are needed on the client side and nothing like RDP or SMB is exposed.

## 1. Pick a machine and a name

You need a machine reachable from both sides: a small VPS, Windows Server 2022, or a Linux box. You also need a DNS name for it, e.g. `remote.example.com` (replace it with your own).

## 2. Publish the server

```powershell
.\scripts\publish-windows.ps1                   # → artifacts\RemoteConnect.Server-win-x64
dotnet publish src\Server\RemoteConnect.Server -c Release -r linux-x64 --self-contained -o artifacts\RemoteConnect.Server-linux-x64
```

## 3a. Windows Server 2022 (no Docker)

From an elevated PowerShell on the server:

```powershell
$key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))   # or (New-Guid).Guid
.\install-server-windows.ps1 -Url http://127.0.0.1:5080 -RegistrationKey $key -AdminApiKey (New-Guid).Guid
```

Then terminate HTTPS in front of it, using either:

- **IIS + Application Request Routing**, with WebSocket protocol enabled, rewriting `https://remote.example.com/*` to `http://127.0.0.1:5080/{R:1}`; or
- **Caddy for Windows**, using [deployment/Server/Caddyfile](../deployment/Server/Caddyfile). It obtains Let's Encrypt certificates automatically.

Alternatively, let Kestrel serve HTTPS directly by adding a certificate endpoint in `appsettings.Production.json`:

```json
"Kestrel": { "Endpoints": { "Https": { "Url": "https://0.0.0.0:443",
  "Certificate": { "Path": "C:\\certs\\remote.pfx", "Password": "<from env: Kestrel__Endpoints__Https__Certificate__Password>" } } } }
```

## 3b. Linux

See [deployment/Server/remoteconnect-server.service](../deployment/Server/remoteconnect-server.service) for a systemd unit, and [Caddyfile](../deployment/Server/Caddyfile) or [nginx.conf](../deployment/Server/nginx.conf) for TLS. Put `RemoteConnect__RegistrationKey=...` in `/etc/remoteconnect-server.env` (mode 600).

## 4. Firewall

Open **TCP 443** (and 80 for certificate issuance) to the reverse proxy only. Port 5080 should listen on 127.0.0.1.

## 5. Configure clients

In each client go to **Settings → Server** and enter:

- Address: `https://remote.example.com`
- Registration key: your key

Click **Save**. The sidebar should show "Ready — online via server". Or pre-configure at install time:

```powershell
install-windows.ps1 -ServerUrl https://remote.example.com
```

```bash
install-macos.sh --server https://remote.example.com
```

The key is entered once in Settings and stored protected.

## 6. Verify

```powershell
Invoke-RestMethod https://remote.example.com/health/ready
Invoke-RestMethod https://remote.example.com/api/v1/info        # registrationKeyRequired should be true
```

Connect by ID from a different network. The session toolbar shows **Relay** or **Direct**.

## Notes

- The relay carries only end-to-end TLS. Its bandwidth is roughly that of the screen stream: 0.2–8 Mbit/s per active session depending on activity and quality.
- Plain `http://` server addresses are accepted by clients only for localhost and private-network IPs (LAN trials).
- NAT hole punching (WebRTC ICE/STUN) and a coturn TURN server are planned (M3). The built-in relay already covers every network that allows outbound HTTPS.
