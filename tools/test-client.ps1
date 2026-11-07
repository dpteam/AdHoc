# =========================================================================
#  Protocol smoke test for DPT.Agent AdHoc Server.
#  Speaks the real SceNetAdhocctl wire format (no dependencies).
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\test-client.ps1
# =========================================================================
param(
    [string]$Server = '127.0.0.1',
    [int]$Port = 27312,
    [string]$Group = 'ABCD1234'
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

function New-LoginPacket([string]$nick, [string]$game, [byte[]]$mac) {
    $b = New-Object byte[] 144
    $b[0] = 1
    [Array]::Copy($mac, 0, $b, 1, 6)
    $nb = [Text.Encoding]::UTF8.GetBytes($nick)
    [Array]::Copy($nb, 0, $b, 7, [Math]::Min($nb.Length, 128))
    $gb = [Text.Encoding]::UTF8.GetBytes($game)
    [Array]::Copy($gb, 0, $b, 135, [Math]::Min($gb.Length, 9))
    return $b
}

function New-ConnectPacket([string]$group) {
    $b = New-Object byte[] 9
    $b[0] = 2
    $gb = [Text.Encoding]::UTF8.GetBytes($group)
    [Array]::Copy($gb, 0, $b, 1, [Math]::Min($gb.Length, 8))
    return $b
}

function New-ChatPacket([string]$text) {
    $b = New-Object byte[] 65
    $b[0] = 7
    $tb = [Text.Encoding]::UTF8.GetBytes($text)
    [Array]::Copy($tb, 0, $b, 1, [Math]::Min($tb.Length, 64))
    return $b
}

function Connect-Client([string]$nick, [string]$game, [byte[]]$mac) {
    $c = New-Object System.Net.Sockets.TcpClient
    $c.Connect($Server, $Port)
    $c.NoDelay = $true
    $s = $c.GetStream()
    $p = New-LoginPacket $nick $game $mac
    $s.Write($p, 0, $p.Length)
    $s.Flush()
    return $c
}

function Read-Packets($client, [int]$waitMs) {
    Start-Sleep -Milliseconds $waitMs
    $s = $client.GetStream()
    $all = New-Object 'System.Collections.Generic.List[byte]'
    while ($client.Available -gt 0) {
        $buf = New-Object byte[] 4096
        $n = $s.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        for ($i = 0; $i -lt $n; $i++) { $all.Add($buf[$i]) }
    }
    return ,$all.ToArray()
}

function Decode-Stream([byte[]]$data) {
    $events = New-Object System.Collections.Generic.List[string]
    $pos = 0
    while ($pos -lt $data.Length) {
        $op = $data[$pos]
        $len = 0
        switch ($op) {
            0 { $len = 1 }
            2 { $len = 139 }
            3 { $len = 5 }
            4 { $len = 15 }
            5 { $len = 1 }
            6 { $len = 7 }
            7 { $len = 193 }
            default { $events.Add("UNKNOWN op=$op"); return ,$events }
        }
        if ($pos + $len -gt $data.Length) { $events.Add("PARTIAL op=$op need=$len have=$($data.Length - $pos)"); break }

        switch ($op) {
            0 { $events.Add('PING') }
            2 {
                $nick = [Text.Encoding]::UTF8.GetString($data, $pos + 1, 128).TrimEnd([char]0)
                $mac = ($data[($pos + 129)..($pos + 134)] | ForEach-Object { $_.ToString('X2') }) -join ':'
                $ip = ($data[($pos + 135)..($pos + 138)] -join '.')
                $events.Add("CONNECT nick=$nick mac=$mac ip=$ip")
            }
            3 {
                $ip = ($data[($pos + 1)..($pos + 4)] -join '.')
                $events.Add("DISCONNECT ip=$ip")
            }
            4 {
                $g = [Text.Encoding]::UTF8.GetString($data, $pos + 1, 8).TrimEnd([char]0)
                $mac = ($data[($pos + 9)..($pos + 14)] | ForEach-Object { $_.ToString('X2') }) -join ':'
                $events.Add("SCAN group=$g host=$mac")
            }
            5 { $events.Add('SCAN_COMPLETE') }
            6 {
                $mac = ($data[($pos + 1)..($pos + 6)] | ForEach-Object { $_.ToString('X2') }) -join ':'
                $events.Add("CONNECT_BSSID mac=$mac")
            }
            7 {
                $msg = [Text.Encoding]::UTF8.GetString($data, $pos + 1, 64).TrimEnd([char]0)
                $nick = [Text.Encoding]::UTF8.GetString($data, $pos + 65, 128).TrimEnd([char]0)
                $events.Add("CHAT `"$msg`" from=$nick")
            }
        }
        $pos += $len
    }
    return ,$events
}

function Assert([bool]$condition, [string]$name, [string]$detail) {
    if ($condition) {
        Write-Host ("  [ok]   " + $name) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red
        $script:failures++
    }
}

Write-Host ""
Write-Host "DPT.Agent AdHoc protocol test -> $Server`:$Port" -ForegroundColor Cyan
Write-Host ""

$mac1 = [byte[]](0x02, 0x00, 0x00, 0x00, 0x00, 0x01)
$mac2 = [byte[]](0x02, 0x00, 0x00, 0x00, 0x00, 0x02)
$mac3 = [byte[]](0x02, 0x00, 0x00, 0x00, 0x00, 0x03)

$c1 = Connect-Client 'Alpha' 'ULUS10084' $mac1
$c2 = Connect-Client 'Bravo' 'ULES00318' $mac2   # crosslinks to ULUS10084 (Monster Hunter Freedom)
$c3 = Connect-Client 'Scanner' 'ULUS10084' $mac3

$quiet = Read-Packets $c1 300
Assert ($quiet.Length -eq 0) 'login sends no ACK (matches reference server)' "got $($quiet.Length) bytes"

$s1 = $c1.GetStream()
$p = New-ConnectPacket $Group
$s1.Write($p, 0, $p.Length); $s1.Flush()
$first = Read-Packets $c1 350
$ev1 = Decode-Stream $first
Assert ($ev1.Count -ge 1 -and $ev1[0] -like 'CONNECT_BSSID*') 'first join gets CONNECT_BSSID' ($ev1 -join '; ')

$s2 = $c2.GetStream()
$p = New-ConnectPacket $Group
$s2.Write($p, 0, $p.Length); $s2.Flush()
$second = Read-Packets $c2 400
$ev2 = Decode-Stream $second
Assert (($ev2 | Where-Object { $_ -like 'CONNECT nick=Alpha*' }).Count -eq 1) 'peer receives CONNECT of newcomer' ($ev2 -join '; ')
Assert (($ev2 | Where-Object { $_ -like 'CONNECT_BSSID mac=02:00:00:00:00:01' }).Count -eq 1) 'BSSID points to group founder' ($ev2 -join '; ')

$left = Read-Packets $c1 250
$evLeft = Decode-Stream $left
Assert (($evLeft | Where-Object { $_ -like 'CONNECT nick=Bravo*' }).Count -eq 1) 'founder receives CONNECT of newcomer' ($evLeft -join '; ')

# scanner is not in a group -> gets SCAN results + SCAN_COMPLETE
$s3 = $c3.GetStream()
$p = [byte[]](4)
$s3.Write($p, 0, $p.Length); $s3.Flush()
$scan = Read-Packets $c3 350
$evScan = Decode-Stream $scan
Assert (($evScan | Where-Object { $_ -like 'SCAN group=ABCD1234*' }).Count -ge 1) 'scan returns groups' ($evScan -join '; ')
Assert (($evScan | Where-Object { $_ -eq 'SCAN_COMPLETE' }).Count -eq 1) 'scan ends with SCAN_COMPLETE' ($evScan -join '; ')

$p = New-ChatPacket 'hello from test'
$s1.Write($p, 0, $p.Length); $s1.Flush()
$chat = Read-Packets $c2 350
$evChat = Decode-Stream $chat
Assert (($evChat | Where-Object { $_ -like 'CHAT*hello from test*' }).Count -eq 1) 'chat is forwarded inside the group' ($evChat -join '; ')

$p = [byte[]](3)
$s1.Write($p, 0, $p.Length); $s1.Flush()
$leave = Read-Packets $c2 350
$evLeave = Decode-Stream $leave
Assert (($evLeave | Where-Object { $_ -like 'DISCONNECT*' }).Count -ge 1) 'leaving the group notifies peers' ($evLeave -join '; ')

# ping must not produce anything
$p = [byte[]](0)
$s3.Write($p, 0, $p.Length); $s3.Flush()
$ping = Read-Packets $c3 250
Assert ($ping.Length -eq 0) 'ping is silent' "got $($ping.Length) bytes"

# empty group name: PPSSPP sends sceNetAdhocctlCreate(NULL) as 8 zero bytes
# (Phantasy Star Portable 2 does this) - the reference server accepts it as a group
$c4 = Connect-Client 'PspTwo' 'ULUS10529' ([byte[]](0x02, 0x00, 0x00, 0x00, 0x00, 0x04))
$s4 = $c4.GetStream()
$p = New-ConnectPacket ''
$s4.Write($p, 0, $p.Length); $s4.Flush()
$empty = Read-Packets $c4 400
$evEmpty = Decode-Stream $empty
Assert (($evEmpty | Where-Object { $_ -like 'CONNECT_BSSID*' }).Count -eq 1) 'empty group name is accepted (PSP2 case)' ($evEmpty -join '; ')

$c1.Close(); $c2.Close(); $c3.Close(); $c4.Close()
Start-Sleep -Milliseconds 300

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "All protocol checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
