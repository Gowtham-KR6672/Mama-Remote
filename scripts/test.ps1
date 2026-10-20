<#
.SYNOPSIS  Runs unit and integration tests (no real mouse/keyboard input is ever injected by tests).
.EXAMPLE   .\scripts\test.ps1
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet test (Join-Path $root 'RemoteConnect.sln') -c $Configuration -nologo --logger "console;verbosity=minimal"
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
