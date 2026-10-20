<#
.SYNOPSIS  Runs the RemoteConnect server for development (HTTP on port 5080 by default).
.DESCRIPTION
  For anything beyond a LAN trial put it behind HTTPS (see docs/INTERNET_SETUP.md) and set a registration key.
.EXAMPLE   .\scripts\run-server.ps1 -RegistrationKey (New-Guid)
#>
param(
    [string]$Url = 'http://0.0.0.0:5080',
    [string]$RegistrationKey = '',
    [string]$AdminApiKey = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:Kestrel__Endpoints__Http__Url = $Url
if ($RegistrationKey) { $env:RemoteConnect__RegistrationKey = $RegistrationKey }
if ($AdminApiKey) { $env:RemoteConnect__AdminApiKey = $AdminApiKey }
dotnet run --project (Join-Path $root 'src\Server\RemoteConnect.Server\RemoteConnect.Server.csproj')
