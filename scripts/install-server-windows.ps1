<#
.SYNOPSIS
  Installs the RemoteConnect server as a Windows service (Windows Server 2022 / Windows 10/11). Run elevated.

.DESCRIPTION
  - Copies the published server to %ProgramFiles%\RemoteConnect Server
  - Runs it as the virtual account "NT SERVICE\RemoteConnectServer" (least privilege), automatic start,
    restart on failure
  - Stores data (device registry, audit logs) in %ProgramData%\RemoteConnect Server (ACL: SYSTEM, Administrators, service)
  - Passes configuration through service-scoped environment variables (not visible to normal users)
  - Opens the listening port in Windows Firewall

  For internet use, terminate TLS in front of it (IIS/ARR, Caddy, nginx) or configure a Kestrel HTTPS
  certificate — see docs\INTERNET_SETUP.md.

.EXAMPLE
  .\install-server-windows.ps1 -Url http://0.0.0.0:5080 -RegistrationKey (New-Guid).Guid -AdminApiKey (New-Guid).Guid
#>
param(
    [string]$Source = $PSScriptRoot,
    [string]$Url = 'http://0.0.0.0:5080',
    [Parameter(Mandatory = $true)][string]$RegistrationKey,
    [string]$AdminApiKey = '',
    [string]$ServiceName = 'RemoteConnectServer'
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated PowerShell (Run as administrator).' }
if (-not (Test-Path (Join-Path $Source 'RemoteConnect.Server.exe'))) { throw "RemoteConnect.Server.exe not found in '$Source'." }
if ($RegistrationKey.Length -lt 16) { throw 'Use a registration key of at least 16 characters, e.g. (New-Guid).Guid' }

$installDir = Join-Path $env:ProgramFiles 'RemoteConnect Server'
$dataDir = Join-Path $env:ProgramData 'RemoteConnect Server'
$exe = Join-Path $installDir 'RemoteConnect.Server.exe'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
}

New-Item -ItemType Directory -Force $installDir, $dataDir | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $installDir -Recurse -Force

if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'RemoteConnect Server' `
        -Description 'Self-hosted RemoteConnect signaling and relay server.' -StartupType Automatic | Out-Null
}

# Least privilege: virtual service account.
& sc.exe config $ServiceName obj= "NT SERVICE\$ServiceName" | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

# Data directory: only SYSTEM, Administrators and the service account.
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($who in 'NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', "NT SERVICE\$ServiceName") {
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($who, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $dataDir -AclObject $acl

# Service-scoped environment (REG_MULTI_SZ). Kestrel endpoint config wins over ASPNETCORE_URLS, so set it explicitly.
$envVars = @(
    "Kestrel__Endpoints__Http__Url=$Url",
    "RemoteConnect__DataDirectory=$dataDir",
    "RemoteConnect__RegistrationKey=$RegistrationKey",
    'DOTNET_ENVIRONMENT=Production'
)
if ($AdminApiKey) { $envVars += "RemoteConnect__AdminApiKey=$AdminApiKey" }
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Value $envVars -Type MultiString

$port = ([Uri]($Url -replace '0\.0\.0\.0', 'localhost')).Port
Get-NetFirewallRule -DisplayName 'RemoteConnect Server*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName "RemoteConnect Server (TCP $port)" -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null

Start-Service $ServiceName
Start-Sleep -Seconds 2
try {
    $health = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 "http://localhost:$port/health/ready"
    Write-Host "Service '$ServiceName' is running. Health: $($health.StatusCode)"
} catch {
    Write-Warning "Service started but the health check failed: $($_.Exception.Message). See Event Viewer > Application."
}
Write-Host "Clients: Settings -> Server = your public https:// address, Registration key = the key you chose."
