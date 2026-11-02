# Holds one adhoc session open for N seconds (used by tools\test-web.ps1).
param(
    [string]$Server = '127.0.0.1',
    [int]$Port = 27312,
    [string]$Nick = 'Holder',
    [string]$Game = 'ULUS10084',
    [string]$Group = 'ABCD1234',
    [int]$Seconds = 25,
    [int]$MacSeed = 9,
    [switch]$NoGroup
)

$ErrorActionPreference = 'Stop'

function Packet-Login([string]$nick, [string]$game, [byte[]]$mac) {
    $b = New-Object byte[] 144
    $b[0] = 1
    [Array]::Copy($mac, 0, $b, 1, 6)
    $nb = [Text.Encoding]::UTF8.GetBytes($nick)
    [Array]::Copy($nb, 0, $b, 7, [Math]::Min($nb.Length, 128))
    $gb = [Text.Encoding]::UTF8.GetBytes($game)
    [Array]::Copy($gb, 0, $b, 135, [Math]::Min($gb.Length, 9))
    return $b
}

$mac = [byte[]](0x02, 0x00, 0x00, [byte]$MacSeed, 0x00, 0x01)
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($Server, $Port)
$client.NoDelay = $true
$stream = $client.GetStream()

$login = Packet-Login $Nick $Game $mac
$stream.Write($login, 0, $login.Length)
$stream.Flush()
Start-Sleep -Milliseconds 250

if (-not $NoGroup) {
    $c = New-Object byte[] 9
    $c[0] = 2
    $gb = [Text.Encoding]::UTF8.GetBytes($Group)
    [Array]::Copy($gb, 0, $c, 1, [Math]::Min($gb.Length, 8))
    $stream.Write($c, 0, $c.Length)
    $stream.Flush()
}

$deadline = (Get-Date).AddSeconds($Seconds)
$lastPing = Get-Date
while ((Get-Date) -lt $deadline) {
    $now = Get-Date
    if (($now - $lastPing).TotalSeconds -ge 2) {
        $ping = [byte[]](0)
        try { $stream.Write($ping, 0, 1); $stream.Flush() } catch { break }
        $lastPing = $now
    }
    if ($client.Available -gt 0) {
        $buf = New-Object byte[] 2048
        [void]$stream.Read($buf, 0, $buf.Length)
    } else {
        Start-Sleep -Milliseconds 200
    }
}
$client.Close()