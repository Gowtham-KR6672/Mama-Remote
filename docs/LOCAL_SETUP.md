# Local network setup (no server)

1. Install RemoteConnect on each computer ([WINDOWS_INSTALL.md](WINDOWS_INSTALL.md), [MACOS_INSTALL.md](MACOS_INSTALL.md)).
2. Leave **Settings → Network** at the defaults:
   - Direct connections: on, TCP **47070**
   - Discovery: on, UDP **47071**
3. Allow RemoteConnect through the firewall on **private** networks:
   - **Windows:** accept the prompt Windows shows on first start, or install with `-AddFirewallRules` from an elevated prompt.
   - **macOS:** allow "Local Network" access when asked. The built-in firewall (if enabled) prompts for incoming connections the first time.
4. Connect from computer A using one of these:
   - **Local network** tab: click **Connect** next to B.
   - Type B's 9-digit address.
   - Type B's IP address, optionally with `:port`. The first time, confirm that the ID shown matches the address displayed on B.
5. On B, click **Accept** (or **View only**).

## Two instances on one PC (development)

Each profile has its own data folder and device ID:

```powershell
.\scripts\run-client.ps1                    # instance 1 (default profile)
.\scripts\run-client.ps1 -Profile second    # instance 2
```

Before starting the second profile, change its port: set **Settings → Network → TCP port**, for example to 47080. Then in instance 2 connect to `127.0.0.1:47070`.

> Careful: controlling your own screen from your own screen produces a "hall of mirrors", and remote input moves your real mouse. Prefer two machines or a VM.

## When discovery does not find a device

- The machines must be in the same broadcast domain (same subnet/VLAN). Guest Wi-Fi often isolates clients.
- Corporate networks may block UDP broadcast. Connect by IP address instead, or use a server.
- Check that the other device shows a green or amber dot. A red dot with "Direct LAN connections are off" means its listener is disabled or the port is taken; the reason is shown in the sidebar.
