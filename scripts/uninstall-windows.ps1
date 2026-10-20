<#
.SYNOPSIS  Removes RemoteConnect for the current user (app files, auto-start, shortcuts, uninstall entry).
.PARAMETER RemoveUserData  Also delete settings, device identity and logs (%APPDATA%\RemoteConnect).
                           The device then gets a new ID if RemoteConnect is installed again.
#>
param([switch]$RemoveUserData)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\RemoteConnect'
$exe = Join-Path $installDir 'RemoteConnect.Desktop.exe'

Get-Process -Name 'RemoteConnect.Desktop' -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId } |
    ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RemoteConnect' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect' -Recurse -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'RemoteConnect.lnk') -ErrorAction SilentlyContinue

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Get-NetFirewallRule -DisplayName 'RemoteConnect*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if (Test-Path $installDir) {
    # This script may live in the install folder; delete everything after it exits.
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$installDir`"" -WindowStyle Hidden
}

if ($RemoveUserData) {
    Remove-Item -Recurse -Force (Join-Path $env:APPDATA 'RemoteConnect') -ErrorAction SilentlyContinue
    Write-Host 'User data removed.'
}

Write-Host 'RemoteConnect has been uninstalled. It will no longer start at sign-in or accept connections.'
