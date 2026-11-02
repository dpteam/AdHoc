# =========================================================================
#  Relay (aemu_postoffice) smoke test for DPT.PSP.
#  Speaks the same TCP protocol as PPSSPP: INIT + PDP frames + PTP handshake.
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\test-relay.ps1
#         [-Server 127.0.0.1] [-RelayPort 27313]
# =========================================================================
param(
    [string]$Server = '127.0.0.1',
    [int]$RelayPort = 27313
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

function Assert([bool]$condition, [string]$name, [string]$detail) {
    if ($condition) { Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red; $script:failures++ }
}

function Connect-Relay {
    $c = New-Object System.Net.Sockets.TcpClient
    $c.Connect($Server, $RelayPort)
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
        try {
            $n = $stream.Read($buf, $got, $count - $got)
        } catch {
            if ($_.Exception.InnerException -is [System.Net.Sockets.SocketException]) { continue }
            throw
        }
        if ($n -le 0) { throw "connection closed ($got/$count)" }
        $got += $n
    }
    return ,$buf
}

function MacText([byte[]]$b) {
    return (($b[0..5] | ForEach-Object { $_.ToString('X2') }) -join ':')
}

Write-Host ""
Write-Host "DPT.PSP relay test -> $Server`:$RelayPort" -ForegroundColor Cyan
Write-Host ""

$macA = [byte[]](0x02, 0xAA, 0x00, 0x00, 0x00, 0x01)
$macB = [byte[]](0x02, 0xBB, 0x00, 0x00, 0x00, 0x02)
$zero = New-Object byte[] 6

# ---------------------------------------------------------------- PDP
$pdpA = Connect-Relay
$pdpB = Connect-Relay
$pdpA.GetStream().Write((Build-Init 0 $macA 3011 $zero 0), 0, 24)
$pdpB.GetStream().Write((Build-Init 0 $macB 3012 $zero 0), 0, 24)
Start-Sleep -Milliseconds 500

$payload = [Text.Encoding]::UTF8.GetBytes('hello relay')
$frame = New-Object byte[] (14 + $payload.Length)
[Array]::Copy($macB, 0, $frame, 0, 6)
[Array]::Copy([BitConverter]::GetBytes([uint16]3012), 0, $frame, 8, 2)
[Array]::Copy([BitConverter]::GetBytes([uint32]$payload.Length), 0, $frame, 10, 4)
[Array]::Copy($payload, 0, $frame, 14, $payload.Length)
$pdpA.GetStream().Write($frame, 0, $frame.Length)

$header = Read-Exact $pdpB 14
$size = [BitConverter]::ToUInt32($header, 10)
$body = if ($size -gt 0) { Read-Exact $pdpB ([int]$size) } else { @() }
$srcMac = MacText $header
$srcPort = [BitConverter]::ToUInt16($header, 8)

Assert ($srcMac -eq '02:AA:00:00:00:01') 'PDP rewrites source MAC to sender' $srcMac
Assert ($srcPort -eq 3011) 'PDP rewrites source port to sender' ("port=" + $srcPort)
Assert ($size -eq $payload.Length) 'PDP payload size preserved' ("size=" + $size)
Assert ([Text.Encoding]::UTF8.GetString($body) -eq 'hello relay') 'PDP payload delivered' ''

# unknown destination must be silently dropped, not crash the session
$ghost = New-Object byte[] (14 + 3)
[Array]::Copy([byte[]](0x02, 0xEE, 0x00, 0x00, 0x00, 0x09), 0, $ghost, 0, 6)
[Array]::Copy([BitConverter]::GetBytes([uint16]9999), 0, $ghost, 8, 2)
[Array]::Copy([BitConverter]::GetBytes([uint32]3), 0, $ghost, 10, 4)
[Array]::Copy([Text.Encoding]::UTF8.GetBytes('xyz'), 0, $ghost, 14, 3)
$pdpA.GetStream().Write($ghost, 0, $ghost.Length)
Start-Sleep -Milliseconds 150

$stillAlive = $pdpA.GetStream().CanWrite
Assert ($stillAlive) 'PDP session survives a frame for an unknown peer' ''

# ---------------------------------------------------------------- PTP
$listen = Connect-Relay
$listen.GetStream().Write((Build-Init 1 $macB 4010 $zero 0), 0, 24)
Start-Sleep -Milliseconds 400

$connect = Connect-Relay
$connect.GetStream().Write((Build-Init 2 $macA 4011 $macB 4010), 0, 24)

$notice = Read-Exact $listen 10
Assert ((MacText $notice) -eq '02:AA:00:00:00:01') 'listener gets connect request (MAC)' (MacText $notice)
Assert (([BitConverter]::ToUInt16($notice, 8)) -eq 4011) 'listener gets connect request (port)' ''

$accept = Connect-Relay
$accept.GetStream().Write((Build-Init 3 $macB 4010 $macA 4011), 0, 24)

$ackConnect = Read-Exact $connect 10
$ackAccept = Read-Exact $accept 10
Assert ((MacText $ackConnect) -eq '02:BB:00:00:00:02') 'connector gets acceptor MAC' (MacText $ackConnect)
Assert (([BitConverter]::ToUInt16($ackConnect, 8)) -eq 4010) 'connector gets acceptor port' ''
Assert ((MacText $ackAccept) -eq '02:AA:00:00:00:01') 'acceptor gets connector MAC' (MacText $ackAccept)
Assert (([BitConverter]::ToUInt16($ackAccept, 8)) -eq 4011) 'acceptor gets connector port' ''

function PtpFrame([string]$text) {
    $data = [Text.Encoding]::UTF8.GetBytes($text)
    $f = New-Object byte[] (4 + $data.Length)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$data.Length), 0, $f, 0, 4)
    [Array]::Copy($data, 0, $f, 4, $data.Length)
    return ,$f
}

$f1 = PtpFrame 'ping-through-relay'
$accept.GetStream().Write($f1, 0, $f1.Length)
$h1 = Read-Exact $connect 4
$n1 = [BitConverter]::ToUInt32($h1, 0)
$d1 = Read-Exact $connect ([int]$n1)
Assert ([Text.Encoding]::UTF8.GetString($d1) -eq 'ping-through-relay') 'PTP accept -> connect data' ''

$f2 = PtpFrame 'pong-through-relay'
$connect.GetStream().Write($f2, 0, $f2.Length)
$h2 = Read-Exact $accept 4
$n2 = [BitConverter]::ToUInt32($h2, 0)
$d2 = Read-Exact $accept ([int]$n2)
Assert ([Text.Encoding]::UTF8.GetString($d2) -eq 'pong-through-relay') 'PTP connect -> accept data' ''

# closing one side must close the other
$accept.Close()
Start-Sleep -Milliseconds 400
$closed = $false
try {
    $connect.GetStream().ReadTimeout = 800
    $probe = New-Object byte[] 1
    $r = $connect.GetStream().Read($probe, 0, 1)
    $closed = ($r -eq 0)
} catch {
    $closed = $true
}
Assert ($closed) 'closing the acceptor closes the connector' ''

# ------------------------------------------------- port offset mismatch
# Desktop PPSSPP uses Port offset 0, Android uses 10000. The relay must still route.
$pdpC = Connect-Relay
$pdpD = Connect-Relay
$pdpC.GetStream().Write((Build-Init 0 $macA 5011 $zero 0), 0, 24)
$pdpD.GetStream().Write((Build-Init 0 $macB 15012 $zero 0), 0, 24)   # 5012 + 10000
Start-Sleep -Milliseconds 500

$payload2 = [Text.Encoding]::UTF8.GetBytes('offset test')
$frame2 = New-Object byte[] (14 + $payload2.Length)
[Array]::Copy($macB, 0, $frame2, 0, 6)
[Array]::Copy([BitConverter]::GetBytes([uint16]5012), 0, $frame2, 8, 2)
[Array]::Copy([BitConverter]::GetBytes([uint32]$payload2.Length), 0, $frame2, 10, 4)
[Array]::Copy($payload2, 0, $frame2, 14, $payload2.Length)
$pdpC.GetStream().Write($frame2, 0, $frame2.Length)

$h3 = Read-Exact $pdpD 14
$n3 = [BitConverter]::ToUInt32($h3, 10)
$d3 = Read-Exact $pdpD ([int]$n3)
$srcPort3 = [BitConverter]::ToUInt16($h3, 8)
Assert ([Text.Encoding]::UTF8.GetString($d3) -eq 'offset test') 'PDP routed across port offsets' ''
Assert ($srcPort3 -eq 15011) 'PDP source port translated into receiver space' ("port=" + $srcPort3)

$listenD = Connect-Relay
$listenD.GetStream().Write((Build-Init 1 $macB 16010 $zero 0), 0, 24)
Start-Sleep -Milliseconds 400

$connectC = Connect-Relay
$connectC.GetStream().Write((Build-Init 2 $macA 6011 $macB 6010), 0, 24)

$notice2 = Read-Exact $listenD 10
Assert (([BitConverter]::ToUInt16($notice2, 8)) -eq 16011) 'PTP notice translated to listener offset space' ''

$acceptD = Connect-Relay
$acceptD.GetStream().Write((Build-Init 3 $macB 16010 $macA 16011), 0, 24)

$ackC = Read-Exact $connectC 10
$ackD = Read-Exact $acceptD 10
Assert (([BitConverter]::ToUInt16($ackC, 8)) -eq 6010) 'connector got listener port in its own space' ''
Assert (([BitConverter]::ToUInt16($ackD, 8)) -eq 16011) 'listener got connector port in its own space' ''

$o1 = PtpFrame 'offset-ping'
$acceptD.GetStream().Write($o1, 0, $o1.Length)
$oh = Read-Exact $connectC 4
$od = Read-Exact $connectC ([int][BitConverter]::ToUInt32($oh, 0))
Assert ([Text.Encoding]::UTF8.GetString($od) -eq 'offset-ping') 'PTP data across port offsets' ''

foreach ($cc in @($pdpC, $pdpD, $listenD, $connectC, $acceptD)) { try { $cc.Close() } catch { } }

# --------------------------------------- replay of a real hairpin session
# Straight from a live log: desktop client on 'Port offset 0' opened PDP port 1,
# the other client on 'Port offset 10000' opened 10001 (same game port 1).
# Before the fix every frame was counted as dropped and never delivered.
$pdpR1 = Connect-Relay
$pdpR2 = Connect-Relay
$pdpR1.GetStream().Write((Build-Init 0 $macA 1 $zero 0), 0, 24)
$pdpR2.GetStream().Write((Build-Init 0 $macB 10001 $zero 0), 0, 24)
Start-Sleep -Milliseconds 500

$payA = [Text.Encoding]::UTF8.GetBytes('worms broadcast')
$frA = New-Object byte[] (14 + $payA.Length)
[Array]::Copy($macB, 0, $frA, 0, 6)
[Array]::Copy([BitConverter]::GetBytes([uint16]1), 0, $frA, 8, 2)
[Array]::Copy([BitConverter]::GetBytes([uint32]$payA.Length), 0, $frA, 10, 4)
[Array]::Copy($payA, 0, $frA, 14, $payA.Length)
$pdpR1.GetStream().Write($frA, 0, $frA.Length)

$hR1 = Read-Exact $pdpR2 14
$nR1 = [BitConverter]::ToUInt32($hR1, 10)
$dR1 = Read-Exact $pdpR2 ([int]$nR1)
Assert ([Text.Encoding]::UTF8.GetString($dR1) -eq 'worms broadcast') 'live log replay: offset 0 -> offset 10000 delivered' ''
Assert (([BitConverter]::ToUInt16($hR1, 8)) -eq 10001) 'live log replay: sender port seen in receiver space' ("port=" + [BitConverter]::ToUInt16($hR1, 8))

$payB = [Text.Encoding]::UTF8.GetBytes('worms reply')
$frB = New-Object byte[] (14 + $payB.Length)
[Array]::Copy($macA, 0, $frB, 0, 6)
[Array]::Copy([BitConverter]::GetBytes([uint16]10001), 0, $frB, 8, 2)
[Array]::Copy([BitConverter]::GetBytes([uint32]$payB.Length), 0, $frB, 10, 4)
[Array]::Copy($payB, 0, $frB, 14, $payB.Length)
$pdpR2.GetStream().Write($frB, 0, $frB.Length)

$hR2 = Read-Exact $pdpR1 14
$nR2 = [BitConverter]::ToUInt32($hR2, 10)
$dR2 = Read-Exact $pdpR1 ([int]$nR2)
Assert ([Text.Encoding]::UTF8.GetString($dR2) -eq 'worms reply') 'live log replay: offset 10000 -> offset 0 delivered' ''
Assert (([BitConverter]::ToUInt16($hR2, 8)) -eq 1) 'live log replay: reply port translated back to 1' ("port=" + [BitConverter]::ToUInt16($hR2, 8))

foreach ($cc in @($pdpR1, $pdpR2)) { try { $cc.Close() } catch { } }

# ---------------------------------------------------------------- bad init
$bad = Connect-Relay
$bad.GetStream().Write((Build-Init 99 $zero 1 $zero 1), 0, 24)
Start-Sleep -Milliseconds 400
$badClosed = $false
try {
    $bad.GetStream().ReadTimeout = 800
    $probe2 = New-Object byte[] 1
    $r2 = $bad.GetStream().Read($probe2, 0, 1)
    $badClosed = ($r2 -eq 0)
} catch {
    $badClosed = $true
}
Assert ($badClosed) 'bad init type is dropped' ''

foreach ($c in @($pdpA, $pdpB, $listen, $connect, $bad)) { try { $c.Close() } catch { } }

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "All relay checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}