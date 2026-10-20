<#
.SYNOPSIS
  Installs RemoteConnect for the current Windows user (no administrator rights needed) and starts it.

.DESCRIPTION
  - Copies the app to %LOCALAPPDATA%\Programs\RemoteConnect
  - Creates a Start-menu shortcut and an "Apps & features" uninstall entry
  - Starts RemoteConnect automatically at sign-in (visible in the tray; disable with -NoAutoStart,
    in RemoteConnect Settings, or in Task Manager -> Startup apps)
  - Optionally adds Windows Firewall rules for LAN connections (needs an elevated prompt)
  Unattended access is NOT enabled by installation; it must be turned on in the app with a password.

.PARAMETER Source          Folder containing RemoteConnect.Desktop.exe (default: this script's folder).
.PARAMETER NoAutoStart     Do not start RemoteConnect at sign-in.
.PARAMETER AddFirewallRules Allow inbound TCP 47070 / UDP 47071 on Private and Domain networks (requires admin).
.PARAMETER ServerUrl       Optional self-hosted server address to pre-configure (e.g. https://remote.example.com).
.PARAMETER NoLaunch        Do not start the app after installing.

.EXAMPLE  powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
.EXAMPLE  powershell -ExecutionPolicy Bypass -File .\install-windows.ps1 -ServerUrl https://remote.example.com -AddFirewallRules
#>
param(
    [string]$Source = $PSScriptRoot,
    [switch]$NoAutoStart,
    [switch]$AddFirewallRules,
    [string]$ServerUrl = '',
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

$exeName = 'RemoteConnect.Desktop.exe'
if (-not (Test-Path (Join-Path $Source $exeName))) {
    throw "$exeName not found in '$Source'. Run scripts\publish-windows.ps1 first or pass -Source."
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\RemoteConnect'
$exe = Join-Path $installDir $exeName
$dataDir = Join-Path $env:APPDATA 'RemoteConnect'

# Stop a running copy (only this user's processes) so files can be replaced.
Get-Process -Name 'RemoteConnect.Desktop' -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId } |
    ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (-not $_.HasExited) { $_.Kill() } }

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $installDir -Recurse -Force

# First-run settings: only written when the user has none yet (never overwrites existing configuration).
$settingsFile = Join-Path $dataDir 'settings.json'
if (-not (Test-Path $settingsFile)) {
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    $settings = [ordered]@{
        autoStart        = (-not $NoAutoStart.IsPresent)
        autoStartApplied = $false
    }
    if ($ServerUrl) { $settings.serverUrl = $ServerUrl }
    ($settings | ConvertTo-Json) | Out-File -Encoding utf8 $settingsFile
}

# Start at sign-in (per user). The app also manages this from its Settings page.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutoStart) {
    Remove-ItemProperty -Path $runKey -Name 'RemoteConnect' -ErrorAction SilentlyContinue
} else {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'RemoteConnect' -Value "`"$exe`" --autostart"
}

# Start-menu shortcut.
$shell = New-Object -ComObject WScript.Shell
$programs = [Environment]::GetFolderPath('Programs')
$lnk = $shell.CreateShortcut((Join-Path $programs 'RemoteConnect.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $installDir
$lnk.IconLocation = "$exe,0"
$lnk.Description = 'RemoteConnect remote desktop'
$lnk.Save()

# Apps & features entry.
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect'
New-Item -Path $uninstallKey -Force | Out-Null
$version = (Get-Item $exe).VersionInfo.ProductVersion
Set-ItemProperty $uninstallKey -Name DisplayName -Value 'RemoteConnect'
Set-ItemProperty $uninstallKey -Name DisplayVersion -Value $version
Set-ItemProperty $uninstallKey -Name Publisher -Value 'RemoteConnect'
Set-ItemProperty $uninstallKey -Name DisplayIcon -Value $exe
Set-ItemProperty $uninstallKey -Name InstallLocation -Value $installDir
Set-ItemProperty $uninstallKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDir\uninstall-windows.ps1`""
Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

if ($AddFirewallRules) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        Get-NetFirewallRule -DisplayName 'RemoteConnect*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName 'RemoteConnect (TCP-In)' -Direction Inbound -Program $exe -Protocol TCP -LocalPort 47070 -Profile Private, Domain -Action Allow | Out-Null
        New-NetFirewallRule -DisplayName 'RemoteConnect discovery (UDP-In)' -Direction Inbound -Program $exe -Protocol UDP -LocalPort 47071 -Profile Private, Domain -Action Allow | Out-Null
        Write-Host 'Firewall rules added for Private and Domain networks.'
    } else {
        Write-Warning 'Firewall rules need an elevated PowerShell. Re-run with "Run as administrator", or allow RemoteConnect when Windows asks.'
    }
}

Write-Host "RemoteConnect installed to $installDir"
if (-not $NoAutoStart) { Write-Host 'It will start automatically when you sign in and wait in the tray, ready for connections.' }
if (-not $NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $installDir }
