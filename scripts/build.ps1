<#
.SYNOPSIS  Restores and builds the whole solution.
.EXAMPLE   .\scripts\build.ps1 -Configuration Release
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet --version | Out-Null
dotnet build (Join-Path $root 'RemoteConnect.sln') -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
