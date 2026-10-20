<#
.SYNOPSIS  Publishes self-contained Windows x64 builds of the desktop app and the server into .\artifacts.
.EXAMPLE   .\scripts\publish-windows.ps1
#>
param(
    [string]$Runtime = 'win-x64',
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts' }

$desktopOut = Join-Path $Output "RemoteConnect-$Runtime"
$serverOut = Join-Path $Output "RemoteConnect.Server-$Runtime"
foreach ($dir in $desktopOut, $serverOut) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }

dotnet publish (Join-Path $root 'src\Desktop\RemoteConnect.Desktop\RemoteConnect.Desktop.csproj') -c Release -r $Runtime --self-contained true -p:PublishReadyToRun=true -o $desktopOut -nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
dotnet publish (Join-Path $root 'src\Server\RemoteConnect.Server\RemoteConnect.Server.csproj') -c Release -r $Runtime --self-contained true -o $serverOut -nologo
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }

Copy-Item (Join-Path $PSScriptRoot 'install-windows.ps1'), (Join-Path $PSScriptRoot 'uninstall-windows.ps1') $desktopOut
Copy-Item (Join-Path $PSScriptRoot 'install-server-windows.ps1'), (Join-Path $PSScriptRoot 'uninstall-server-windows.ps1') $serverOut

Write-Host ''
Write-Host "Desktop app : $desktopOut"
Write-Host "Server      : $serverOut"
Write-Host "Install the app on a PC with:  powershell -ExecutionPolicy Bypass -File `"$desktopOut\install-windows.ps1`""
