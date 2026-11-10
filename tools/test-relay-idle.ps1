# =========================================================================
#  Regression test for the relay idle sweep.
#
#  A PTP_LISTEN socket is silent by design (the client only waits for 10-byte
#  notices), so it must never be reaped for being idle - that killed live
#  matches (Killzone Liberation crashed after ~10 minutes). A session that
#  never carried any traffic must still be reaped.
#
#  Runs a throwaway server with relay.idleSeconds=5 on spare ports.
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\test-relay-idle.ps1
# =========================================================================
param(
    [int]$AdhocPort = 27512,
    [int]$RelayPort = 27513,
    [int]$PublicPort = 44701,
    [int]$AdminPort = 44702
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'AdHocServer.exe'
$script:failures = 0

function Assert([bool]$condition, [string]$name, [string]$detail) {
    if ($condition) { Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red; $script:failures++ }
}

if (-not (Test-Path $exe)) { Write-Host "missing $exe - run build.cmd" -ForegroundColor Red; exit 1 }

# the ports must be free, otherwise we would be talking to somebody else's server
foreach ($port in @($AdhocPort, $RelayPort, $PublicPort, $AdminPort)) {
    $busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    if ($busy) {
        Write-Host ("port " + $port + " is already in use (pid " + $busy[0].OwningProcess + "), pick other ports") -ForegroundColor Red
        exit 1
    }
}

$sandbox = Join-Path $env:TEMP ('adhoc-idle-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $sandbox | Out-Null
Copy-Item (Join-Path $bin '*') $sandbox -Recurse -Force
Remove-Item (Join-Path $sandbox 'config.ini') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $sandbox 'logs\*.log') -ErrorAction SilentlyContinue

$ini = @"
[server]
adhocPort=$AdhocPort
adhocPortFallbacks=
[web]
publicPort=$PublicPort
publicPortFallbacks=
adminPort=$AdminPort
adminPortFallbacks=
[relay]
enabled=true
port=$RelayPort
idleSeconds=5
lifetimeSeconds=600
[logging]
level=Debug
"@
Set-Content -Path (Join-Path $sandbox 'config.ini') -Value $ini -Encoding ASCII

function Connect-Relay {
    $c = New-Object System.Net.Sockets.TcpClient
    $c.Connect('127.0.0.1', $RelayPort)
    $c.NoDelay = $true
    $c.ReceiveTimeout = 4000
    return $c
}

function Build-Init([int]$type, [byte[]]$src, [int]$sport, [byte[]]$dst, [int]$dport) {
    $b = New-Object byte[] 24
    [Array]::Copy([BitConverter]::GetBytes([int]$type), 0, $b, 0, 4)
    [Array]::Copy($src, 0, $b, 4, 6)
    [Array]::Copy([BitConverter]::GetBytes([uint16]$sport), 0, $b, 12, 2)
    [Array]::Copy($dst, 0, $b, 14, 6)
    [Array]::Copy([BitConverter]::GetBytes([uint16]$dport), 0, $b, 22, 2)
    return $b
}

function Read-Exact($client, [int]$count) {
    $stream = $client.GetStream()
    $buf = New-Object byte[] $count
    $got = 0
    $deadline = (Get-Date).AddMilliseconds(5000)
    while ($got -lt $count) {
        if ((Get-Date) -gt $deadline) { throw "read timeout ($got/$count)" }
        try { $n = $stream.Read($buf, $got, $count - $got) }
        catch {
            if ($_.Exception.InnerException -is [System.Net.Sockets.SocketException]) { continue }
            throw
        }
        if ($n -le 0) { throw "connection closed ($got/$count)" }
        $got += $n
    }
    return ,$buf
}

function Is-Alive($client) {
    try {
        $s = $client.GetStream()
        return $s.CanWrite
    } catch { return $false }
}

Write-Host ""
Write-Host "DPT.PSP relay idle-sweep test (idleSeconds=5)" -ForegroundColor Cyan
Write-Host ""

$proc = Start-Process -FilePath (Join-Path $sandbox 'AdHocServer.exe') -ArgumentList @('--headless', '--level', 'Debug') -WorkingDirectory $sandbox -WindowStyle Hidden -PassThru

try {
    $ready = $false
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        try {
            $h = Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:" + $PublicPort + "/api/health") -TimeoutSec 2
            if ($h.ok) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready) {
        Write-Host ("server did not start (exited=" + $proc.HasExited + ")") -ForegroundColor Red
        $l = Get-ChildItem (Join-Path $sandbox 'logs') -Filter 'adhoc-*.log' -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime | Select-Object -Last 1
        if ($l) { Get-Content $l.FullName -Tail 25 }
        else { Write-Host "no log file in $sandbox\logs" -ForegroundColor Red }
        exit 1
    }

    # make sure the relay really listens before poking at it
    $relayUp = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $probeTcp = New-Object System.Net.Sockets.TcpClient
            $probeTcp.Connect('127.0.0.1', $RelayPort)
            $relayUp = $probeTcp.Connected
            $probeTcp.Close()
            if ($relayUp) { break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $relayUp) {
        Write-Host ("relay port " + $RelayPort + " is not listening") -ForegroundColor Red
        $l = Get-ChildItem (Join-Path $sandbox 'logs') -Filter 'adhoc-*.log' -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime | Select-Object -Last 1
        if ($l) { Get-Content $l.FullName -Tail 25 }
        exit 1
    }

    $macA = [byte[]](0x02, 0xAA, 0x00, 0x00, 0x11, 0x01)
    $macB = [byte[]](0x02, 0xBB, 0x00, 0x00, 0x22, 0x02)
    $zero = New-Object byte[] 6

    # a listener that stays silent, and a PDP session that never sends a frame
    $listen = Connect-Relay
    $listen.GetStream().Write((Build-Init 1 $macB 4010 $zero 0), 0, 24)
    $listen.GetStream().Flush()

    $orphan = Connect-Relay
    $orphan.GetStream().Write((Build-Init 0 $macA 7777 $zero 0), 0, 24)
    $orphan.GetStream().Flush()

    Start-Sleep -Milliseconds 700
    $before = (Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:" + $PublicPort + "/api/status") -TimeoutSec 5).relay.sessions
    Assert ($before -eq 2) 'two relay sessions open before the sweep' ("sessions=" + $before)

    Start-Sleep -Seconds 12   # idleSeconds is 5 and the sweep runs every 5 s

    $after = (Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:" + $PublicPort + "/api/status") -TimeoutSec 5).relay.sessions
    Assert ($after -eq 1) 'unused PDP session was reaped, listener kept' ("sessions=" + $after)
    Assert (Is-Alive $listen) 'silent PTP_LISTEN survives the idle sweep' ''

    # and it still works: a connect from the other player must produce a notice
    $connect = Connect-Relay
    $connect.GetStream().Write((Build-Init 2 $macA 4020 $macB 4010), 0, 24)
    $connect.GetStream().Flush()

    $notice = $null
    try { $notice = Read-Exact $listen 10 } catch { $notice = $null }
    Assert ($null -ne $notice) 'reaped-free listener still receives connect notices' ''
    if ($notice) {
        Assert (([BitConverter]::ToUInt16($notice, 8)) -eq 4020) 'notice carries the connector port' ("port=" + [BitConverter]::ToUInt16($notice, 8))
    }

    foreach ($c in @($listen, $orphan, $connect)) { try { $c.Close() } catch { } }
}
finally {
    try { if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } } catch { }
    Start-Sleep -Milliseconds 300
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "All relay idle checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
