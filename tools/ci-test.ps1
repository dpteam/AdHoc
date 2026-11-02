# =========================================================================
#  One-shot CI run: build is already done, this starts the server, runs the
#  protocol / relay / web suites and stops the server again.
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\ci-test.ps1
#         [-SkipBuild] [-Level Debug]
#
#  Exit code 0 = every suite passed.
# =========================================================================
param(
    [switch]$SkipBuild,
    [string]$Level = 'Debug',
    [int]$StartupTimeoutSec = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\AdHocServer.exe'
$bin = Join-Path $root 'bin'

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "=== $text" -ForegroundColor Cyan
}

if (-not $SkipBuild) {
    Write-Step 'build'
    & cmd.exe /c build.cmd
    if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 1 }
}

if (-not (Test-Path $exe)) { Write-Host "missing $exe" -ForegroundColor Red; exit 1 }

Write-Step 'starting server'
$outLog = Join-Path $env:TEMP 'adhoc-ci-out.log'
$errLog = Join-Path $env:TEMP 'adhoc-ci-err.log'
$proc = Start-Process -FilePath $exe -ArgumentList @('--headless', '--level', $Level) -WorkingDirectory $bin `
                      -WindowStyle Hidden -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog

$ready = $false
$deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) { break }
    try {
        $health = Invoke-RestMethod -UseBasicParsing -Uri 'http://127.0.0.1:44501/api/health' -TimeoutSec 3
        if ($health.ok) { $ready = $true; break }
    } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ready) {
    Write-Host 'server did not become ready' -ForegroundColor Red
    if (Test-Path $outLog) { Get-Content $outLog -Tail 30 }
    if (Test-Path $errLog) { Get-Content $errLog -Tail 30 }
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
    exit 1
}
Write-Host ("server up: pid " + $proc.Id)

$suites = @(
    @{ name = 'protocol'; script = 'tools\test-client.ps1' },
    @{ name = 'relay';    script = 'tools\test-relay.ps1'  },
    @{ name = 'web';      script = 'tools\test-web.ps1'    }
)

$failed = @()
try {
    foreach ($suite in $suites) {
        Write-Step ("suite: " + $suite.name)
        $scriptPath = Join-Path $root $suite.script
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath
        if ($LASTEXITCODE -ne 0) { $failed += $suite.name }
    }
}
finally {
    Write-Step 'stopping server'
    try { if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } } catch { }
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host ("failed suites: " + ($failed -join ', ')) -ForegroundColor Red
    $log = Get-ChildItem (Join-Path $bin 'logs') -Filter 'adhoc-*.log' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host ("--- tail of " + $log.Name + " ---")
        Get-Content $log.FullName -Tail 40
    }
    exit 1
}

Write-Host ""
Write-Host 'All suites passed.' -ForegroundColor Green
exit 0