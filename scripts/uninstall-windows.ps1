<#
.SYNOPSIS  Removes RemoteConnect: the per-user install (app files, auto-start, shortcuts, uninstall entry) and, when
           run elevated, the service-mode install (Windows service, %ProgramFiles%\RemoteConnect, machine-wide entries).
.PARAMETER RemoveUserData  Also delete settings, device identity and logs (%APPDATA%\RemoteConnect, and in service
                           mode the service's data in the LocalSystem profile). The device then gets a new ID if
                           RemoteConnect is installed again.
#>
param([switch]$RemoveUserData)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\RemoteConnect'
$exe = Join-Path $installDir 'RemoteConnect.Desktop.exe'
$serviceName = 'RemoteConnectHost'
$machineDir = Join-Path $env:ProgramFiles 'RemoteConnect'
$machineData = Join-Path $env:SystemRoot 'System32\config\systemprofile\AppData\Local\RemoteConnect'
$foldersToDelete = @()

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# Service mode (machine-wide): needs an elevated prompt.
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -or (Test-Path (Join-Path $machineDir 'RemoteConnect.HostService.exe'))) {
    if (-not $isAdmin) {
        throw 'RemoteConnect is installed in service mode. Run this script from an elevated PowerShell ("Run as administrator") to remove the service.'
    }

    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force   # the service ends its session agent (sessions are closed, input released)
            (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not delete the service (sc.exe exit code $LASTEXITCODE)." }
    }

    # RemoteConnect windows started from the machine-wide folder, in any session.
    Get-CimInstance Win32_Process -Filter "Name = 'RemoteConnect.Desktop.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($machineDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    Remove-Item -Path 'HKLM:\SOFTWARE\RemoteConnect' -Recurse -ErrorAction SilentlyContinue
    Remove-Item -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect' -Recurse -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'RemoteConnect.lnk') -ErrorAction SilentlyContinue
    if (Test-Path $machineDir) { $foldersToDelete += $machineDir }

    if ($RemoveUserData) {
        Remove-Item -Recurse -Force $machineData -ErrorAction SilentlyContinue
        Write-Host 'Service data (machine device identity, settings, logs) removed.'
    } elseif (Test-Path $machineData) {
        Write-Host "The service's device identity and settings are kept in $machineData (a reinstall keeps the same ID). Use -RemoveUserData to delete them."
    }

    Write-Host 'The RemoteConnect service has been removed. This PC is no longer reachable at the sign-in screen.'
}

# Per-user install.
Get-Process -Name 'RemoteConnect.Desktop' -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId } |
    ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RemoteConnect' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect' -Recurse -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'RemoteConnect.lnk') -ErrorAction SilentlyContinue

if ($isAdmin) {
    Get-NetFirewallRule -DisplayName 'RemoteConnect*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if (Test-Path $installDir) { $foldersToDelete += $installDir }
foreach ($folder in $foldersToDelete) {
    # This script may live in the folder being removed; delete it after the script exits.
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$folder`"" -WindowStyle Hidden
}

if ($RemoveUserData) {
    Remove-Item -Recurse -Force (Join-Path $env:APPDATA 'RemoteConnect') -ErrorAction SilentlyContinue
    Write-Host 'User data removed.'
}

Write-Host 'RemoteConnect has been uninstalled. It will no longer start at sign-in or accept connections.'
