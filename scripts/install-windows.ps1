<#
.SYNOPSIS
  Installs RemoteConnect and starts it. Per user by default (no administrator rights needed), or machine-wide as a
  Windows service with -ServiceMode so the PC is reachable right after power-on, at the sign-in screen.

.DESCRIPTION
  Default (per user):
  - Copies the app to %LOCALAPPDATA%\Programs\RemoteConnect
  - Creates a Start-menu shortcut and an "Apps & features" uninstall entry
  - Starts RemoteConnect automatically at sign-in (visible in the tray; disable with -NoAutoStart,
    in RemoteConnect Settings, or in Task Manager -> Startup apps)
  - Optionally adds Windows Firewall rules for LAN connections (needs an elevated prompt)

  -ServiceMode (explicit opt-in, requires an elevated prompt):
  - Copies the app to %ProgramFiles%\RemoteConnect (admin-owned, users can only read)
  - Registers the "RemoteConnect Host Service" (LocalSystem, automatic start, restarts on failure). It runs the
    session agent in the console session, so the PC can be reached at the sign-in and lock screens.
  - Nobody can accept a request at the sign-in screen, so in service mode the unattended-access password is the
    only way in. It stays OFF until an administrator sets it:  RemoteConnect.HostService.exe set-password
  - Removes this user's earlier per-user install (it would compete for the LAN port).

  Unattended access is NOT enabled by installation in either mode.

.PARAMETER Source          Folder containing RemoteConnect.Desktop.exe (default: this script's folder).
.PARAMETER NoAutoStart     Do not start RemoteConnect at sign-in (per-user mode; the service always starts at boot).
.PARAMETER AddFirewallRules Allow inbound TCP 47070 / UDP 47071 on Private and Domain networks (requires admin).
.PARAMETER ServerUrl       Optional self-hosted server address to pre-configure (e.g. https://remote.example.com).
.PARAMETER NoLaunch        Do not start the app after installing (per-user mode).
.PARAMETER ServiceMode     Install machine-wide as a Windows service (requires admin). See above.

.EXAMPLE  powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
.EXAMPLE  powershell -ExecutionPolicy Bypass -File .\install-windows.ps1 -ServerUrl https://remote.example.com -AddFirewallRules
.EXAMPLE  (elevated) powershell -ExecutionPolicy Bypass -File .\install-windows.ps1 -ServiceMode -AddFirewallRules
#>
param(
    [string]$Source = $PSScriptRoot,
    [switch]$NoAutoStart,
    [switch]$AddFirewallRules,
    [string]$ServerUrl = '',
    [switch]$NoLaunch,
    [switch]$ServiceMode
)
$ErrorActionPreference = 'Stop'

$exeName = 'RemoteConnect.Desktop.exe'
if (-not (Test-Path (Join-Path $Source $exeName))) {
    throw "$exeName not found in '$Source'. Run scripts\publish-windows.ps1 first or pass -Source."
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Add-FirewallRules([string]$program) {
    if ($isAdmin) {
        Get-NetFirewallRule -DisplayName 'RemoteConnect*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName 'RemoteConnect (TCP-In)' -Direction Inbound -Program $program -Protocol TCP -LocalPort 47070 -Profile Private, Domain -Action Allow | Out-Null
        New-NetFirewallRule -DisplayName 'RemoteConnect discovery (UDP-In)' -Direction Inbound -Program $program -Protocol UDP -LocalPort 47071 -Profile Private, Domain -Action Allow | Out-Null
        Write-Host 'Firewall rules added for Private and Domain networks.'
    } else {
        Write-Warning 'Firewall rules need an elevated PowerShell. Re-run with "Run as administrator", or allow RemoteConnect when Windows asks.'
    }
}

function New-Shortcut([string]$folder, [string]$target, [string]$workingDir) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $folder 'RemoteConnect.lnk'))
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $workingDir
    $lnk.IconLocation = "$target,0"
    $lnk.Description = 'RemoteConnect remote desktop'
    $lnk.Save()
}

function Set-UninstallEntry([string]$key, [string]$exe, [string]$dir) {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty $key -Name DisplayName -Value 'RemoteConnect'
    Set-ItemProperty $key -Name DisplayVersion -Value (Get-Item $exe).VersionInfo.ProductVersion
    Set-ItemProperty $key -Name Publisher -Value 'RemoteConnect'
    Set-ItemProperty $key -Name DisplayIcon -Value $exe
    Set-ItemProperty $key -Name InstallLocation -Value $dir
    Set-ItemProperty $key -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\uninstall-windows.ps1`""
    Set-ItemProperty $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $key -Name NoRepair -Value 1 -Type DWord
}

if ($ServiceMode) {
    if (-not $isAdmin) {
        throw 'Service mode installs a Windows service that starts at power-on. Run this script from an elevated PowerShell ("Run as administrator").'
    }

    $serviceExeName = 'RemoteConnect.HostService.exe'
    if (-not (Test-Path (Join-Path $Source $serviceExeName))) {
        throw "$serviceExeName not found in '$Source'. Run scripts\publish-windows.ps1 first (it publishes the service next to the app)."
    }

    $serviceName = 'RemoteConnectHost'
    $installDir = Join-Path $env:ProgramFiles 'RemoteConnect'
    $exe = Join-Path $installDir $exeName
    $serviceExe = Join-Path $installDir $serviceExeName

    # 1. Stop a previous version: the service (which ends its agent) and RemoteConnect windows started from this folder.
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Get-CimInstance Win32_Process -Filter "Name = '$exeName'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    # 2. This user's earlier per-user install would compete with the service for the LAN port: remove it.
    $userDir = Join-Path $env:LOCALAPPDATA 'Programs\RemoteConnect'
    if (Test-Path (Join-Path $userDir $exeName)) {
        Get-Process -Name 'RemoteConnect.Desktop' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($userDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $run = (Get-ItemProperty -Path $runKey -Name 'RemoteConnect' -ErrorAction SilentlyContinue).RemoteConnect
        if ($run -and $run.Contains($userDir)) { Remove-ItemProperty -Path $runKey -Name 'RemoteConnect' }
        Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect' -Recurse -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'RemoteConnect.lnk') -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $userDir
        Write-Host "Removed the earlier per-user install from $userDir (its settings in %APPDATA%\RemoteConnect are kept)."
    }

    # 3. Program files. The service runs the agent from here as LocalSystem, so the folder must be owned by
    #    Administrators with only the inherited Program Files permissions; the service checks this before every start.
    New-Item -ItemType Directory -Force $installDir | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $installDir -Recurse -Force
    & icacls.exe $installDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set the owner of $installDir (icacls exit code $LASTEXITCODE)." }
    & icacls.exe $installDir /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not reset the permissions of $installDir (icacls exit code $LASTEXITCODE)." }

    # 4. The service: LocalSystem, automatic start, restart after a crash. The quoted path avoids the unquoted-path issue.
    $binPath = "`"$serviceExe`""
    if ($existing) {
        Set-Service -Name $serviceName -StartupType Automatic # same fixed path; files were replaced above
    } else {
        New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName 'RemoteConnect Host Service' `
            -Description 'Makes this computer reachable with RemoteConnect from power-on, including the sign-in screen. Only the unattended-access password set by an administrator grants access.' `
            -StartupType Automatic | Out-Null
    }
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    New-Item -Path 'HKLM:\SOFTWARE\RemoteConnect\Service' -Force | Out-Null

    if ($ServerUrl) {
        & $serviceExe set-server $ServerUrl
        if ($LASTEXITCODE -ne 0) { throw "Could not set the server address (exit code $LASTEXITCODE)." }
    }

    # 5. Start menu for all users, machine-wide uninstall entry, optional firewall rules.
    New-Shortcut ([Environment]::GetFolderPath('CommonPrograms')) $exe $installDir
    Set-UninstallEntry 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect' $exe $installDir
    if ($AddFirewallRules) { Add-FirewallRules $exe }

    # 6. Start and report.
    Start-Service -Name $serviceName
    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Seconds 1
        $state = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\RemoteConnect\Service' -Name State -ErrorAction SilentlyContinue).State
    } while ($state -notin @('Ready', 'Degraded', 'Blocked') -and (Get-Date) -lt $deadline)

    Write-Host ''
    & $serviceExe status
    Write-Host ''
    Write-Host "RemoteConnect is installed in service mode in $installDir."
    Write-Host 'The service starts at power-on and keeps this PC reachable at the sign-in and lock screens.'
    if ($state -eq 'Blocked') {
        Write-Warning 'The service refused to start its agent. See Event Viewer -> Windows Logs -> Application (source RemoteConnect.HostService).'
    }
    Write-Host ''
    Write-Host 'Unattended access is OFF. In service mode it is the only way in, because nobody can accept a request'
    Write-Host 'at the sign-in screen. To turn it on, run this in an elevated prompt and choose a strong password:'
    Write-Host "    & `"$serviceExe`" set-password"
    Write-Host 'To turn it off again:  ... disable-unattended   (more: ... help)'
    Write-Host ''
    Write-Host 'Open RemoteConnect from the Start menu to see this PC''s ID or to connect to other computers.'
    return
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\RemoteConnect'
$exe = Join-Path $installDir $exeName
$dataDir = Join-Path $env:APPDATA 'RemoteConnect'

if (Get-Service -Name 'RemoteConnectHost' -ErrorAction SilentlyContinue) {
    Write-Warning 'RemoteConnect is installed in service mode on this PC. The service handles incoming connections; this per-user copy will only make outgoing ones.'
}

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

# Start-menu shortcut and Apps & features entry.
New-Shortcut ([Environment]::GetFolderPath('Programs')) $exe $installDir
Set-UninstallEntry 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect' $exe $installDir

if ($AddFirewallRules) { Add-FirewallRules $exe }

Write-Host "RemoteConnect installed to $installDir"
if (-not $NoAutoStart) { Write-Host 'It will start automatically when you sign in and wait in the tray, ready for connections.' }
if (-not $NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $installDir }
