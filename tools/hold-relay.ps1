# =========================================================================
#  Registers a PDP relay session for one MAC and keeps it open.
#  Handy to simulate "this player uses the relay" while another does not.
#
#  usage: powershell -File tools\hold-relay.ps1 -Mac 02:00:00:31:00:01 -Port 5555 -Seconds 30
# =========================================================================
param(
    [string]$Server = '127.0.0.1',
    [int]$RelayPort = 27313,
    [string]$Mac = '02:00:00:31:00:01',
    [int]$Port = 5555,
    [int]$Seconds = 30
)

$ErrorActionPreference = 'Stop'

function Parse-Mac([string]$text) {
    $parts = $text.Split(':')
    if ($parts.Count -ne 6) { throw "bad MAC: $text" }
    $bytes = New-Object byte[] 6
    for ($i = 0; $i -lt 6; $i++) { $bytes[$i] = [Convert]::ToByte($parts[$i], 16) }
    return ,$bytes
}

$macBytes = Parse-Mac $Mac
$init = New-Object byte[] 24
[Array]::Copy([BitConverter]::GetBytes([int]0), 0, $init, 0, 4)
[Array]::Copy($macBytes, 0, $init, 4, 6)
[Array]::Copy([BitConverter]::GetBytes([uint16]$Port), 0, $init, 12, 2)

$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($Server, $RelayPort)
$client.NoDelay = $true
$stream = $client.GetStream()
$stream.Write($init, 0, $init.Length)
$stream.Flush()

Write-Host ("relay PDP session for " + $Mac + ":" + $Port + " held for " + $Seconds + "s")

$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    if ($client.Available -gt 0) {
        $buf = New-Object byte[] 2048
        [void]$stream.Read($buf, 0, $buf.Length)
    } else {
        Start-Sleep -Milliseconds 250
    }
}
$client.Close()