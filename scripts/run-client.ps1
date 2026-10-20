<#
.SYNOPSIS  Runs the RemoteConnect desktop app from source.
.PARAMETER Profile
  Optional name for a separate data folder, so two instances can run on one PC for testing
  (e.g. -Profile second). Each profile gets its own device ID.
.EXAMPLE   .\scripts\run-client.ps1
.EXAMPLE   .\scripts\run-client.ps1 -Profile second
#>
param([string]$Profile = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Profile) {
    $env:REMOTECONNECT_HOME = Join-Path $env:APPDATA "RemoteConnect-$Profile"
    Write-Host "Using data folder $env:REMOTECONNECT_HOME"
}
dotnet run --project (Join-Path $root 'src\Desktop\RemoteConnect.Desktop\RemoteConnect.Desktop.csproj')
