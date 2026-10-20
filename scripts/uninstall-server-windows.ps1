<#
.SYNOPSIS  Removes the RemoteConnect server Windows service. Run elevated.
.PARAMETER RemoveData  Also delete the device registry and audit logs in %ProgramData%\RemoteConnect Server.
#>
param([string]$ServiceName = 'RemoteConnectServer', [switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated PowerShell.' }

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
}
Get-NetFirewallRule -DisplayName 'RemoteConnect Server*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'RemoteConnect Server') -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'RemoteConnect Server') -ErrorAction SilentlyContinue }
Write-Host 'RemoteConnect server removed.'
