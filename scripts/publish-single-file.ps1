<#
.SYNOPSIS
  Builds RemoteConnect as single files: RemoteConnect.exe (the app, one file, no install needed)
  and RemoteConnect.Server.exe (+ appsettings.json) into .\artifacts\single-file.
.DESCRIPTION
  RemoteConnect.exe registers itself to start at sign-in from wherever it is when first run, so put it in a
  permanent folder (e.g. C:\RemoteConnect) before starting it - not in Downloads.
#>
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\single-file'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$common = @('-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-nologo')

dotnet publish (Join-Path $root 'src\Desktop\RemoteConnect.Desktop\RemoteConnect.Desktop.csproj') @common -o (Join-Path $out 'desktop')
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
dotnet publish (Join-Path $root 'src\Server\RemoteConnect.Server\RemoteConnect.Server.csproj') @common -o (Join-Path $out 'Server')
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }

Move-Item (Join-Path $out 'desktop\RemoteConnect.Desktop.exe') (Join-Path $out 'RemoteConnect.exe')
Remove-Item -Recurse -Force (Join-Path $out 'desktop')
Remove-Item (Join-Path $out 'Server\appsettings.Development.json') -ErrorAction SilentlyContinue

Write-Host "App:    $(Join-Path $out 'RemoteConnect.exe')"
Write-Host "Server: $(Join-Path $out 'Server\RemoteConnect.Server.exe')"
