<#
.SYNOPSIS
  Quick post-build check: starts the server on a free local port with a temporary data folder, verifies the
  health and info endpoints, checks both executables report a version, then stops everything.
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tfm = 'net8.0'

$serverDll = Join-Path $root "src\Server\RemoteConnect.Server\bin\$Configuration\$tfm\RemoteConnect.Server.dll"
$desktopDll = Join-Path $root "src\Desktop\RemoteConnect.Desktop\bin\$Configuration\$tfm\RemoteConnect.Desktop.dll"
foreach ($f in $serverDll, $desktopDll) { if (-not (Test-Path $f)) { throw "Missing $f - run scripts\build.ps1 -Configuration $Configuration first." } }

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$data = Join-Path ([IO.Path]::GetTempPath()) ("rc-smoke-" + [Guid]::NewGuid().ToString('N'))

$results = @()
$proc = Start-Process dotnet -ArgumentList "`"$serverDll`" --Kestrel:Endpoints:Http:Url=http://127.0.0.1:$port --RemoteConnect:DataDirectory=`"$data`" --RemoteConnect:RegistrationKey=smoke-test-key" -PassThru -WindowStyle Hidden
try {
    $ok = $false
    for ($i = 0; $i -lt 40 -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 250
        try { $ok = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 "http://127.0.0.1:$port/health/live").StatusCode -eq 200 } catch { }
    }
    $results += [pscustomobject]@{ Check = 'server /health/live'; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }) }

    $ready = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "http://127.0.0.1:$port/health/ready").StatusCode
    $results += [pscustomobject]@{ Check = 'server /health/ready'; Result = $(if ($ready -eq 200) { 'PASS' } else { "FAIL ($ready)" }) }

    $info = Invoke-RestMethod -TimeoutSec 5 "http://127.0.0.1:$port/api/v1/info"
    $results += [pscustomobject]@{ Check = "server /api/v1/info (v$($info.version), key required=$($info.registrationKeyRequired))"; Result = $(if ($info.registrationKeyRequired) { 'PASS' } else { 'FAIL' }) }

    $admin = try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "http://127.0.0.1:$port/api/v1/admin/devices").StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
    $results += [pscustomobject]@{ Check = 'admin API disabled without key'; Result = $(if ($admin -eq 404) { 'PASS' } else { "FAIL ($admin)" }) }
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}

$v = & dotnet $serverDll --version
$results += [pscustomobject]@{ Check = "server --version ($v)"; Result = $(if ($v -match 'RemoteConnect Server') { 'PASS' } else { 'FAIL' }) }
$v = & dotnet $desktopDll --version
$results += [pscustomobject]@{ Check = "desktop --version ($v)"; Result = $(if ($v -match 'RemoteConnect') { 'PASS' } else { 'FAIL' }) }

$results | Format-Table -AutoSize
if ($results.Result -match 'FAIL') { exit 1 }
