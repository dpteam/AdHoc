# =========================================================================
#  Web API + board smoke test. Starts two adhoc holders, checks /api/*.
#  usage: powershell -ExecutionPolicy Bypass -File tools\test-web.ps1 -Base http://127.0.0.1:8080
# =========================================================================
param(
    [string]$Base = 'http://127.0.0.1:8080',
    [string]$Server = '127.0.0.1',
    [int]$Port = 27312
)

$ErrorActionPreference = 'Stop'
$script:failures = 0
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataFile = Join-Path (Split-Path -Parent $here) 'bin\data\games.json'
$backup = $null

function Assert([bool]$condition, [string]$name, [string]$detail) {
    if ($condition) { Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red; $script:failures++ }
}

function Get-Json([string]$path) {
    return Invoke-RestMethod -UseBasicParsing -Uri ($Base + $path) -TimeoutSec 10
}

function Post-Json([string]$path, $payload) {
    $json = $payload | ConvertTo-Json -Compress
    return Invoke-RestMethod -UseBasicParsing -Uri ($Base + $path) -Method POST -Body $json -ContentType 'application/json' -TimeoutSec 10
}

Write-Host ""
Write-Host "DPT.Agent web/API test -> $Base" -ForegroundColor Cyan
Write-Host ""

if (Test-Path $dataFile) {
    $backup = Join-Path $env:TEMP ('games-backup-' + [Guid]::NewGuid().ToString('N') + '.json')
    Copy-Item $dataFile $backup
}

$p1 = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'hold-client.ps1'),
    '-Nick', 'HolderOne', '-Game', 'ULUS10084', '-Group', 'ABCD1234', '-MacSeed', '11', '-Seconds', '30') -PassThru -WindowStyle Hidden
$p2 = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'hold-client.ps1'),
    '-Nick', 'HolderTwo', '-Game', 'ULES00318', '-Group', 'ABCD1234', '-MacSeed', '12', '-Seconds', '30') -PassThru -WindowStyle Hidden
$p3 = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'hold-client.ps1'),
    '-Nick', 'HolderThree', '-Game', 'ULES01213', '-NoGroup', '-MacSeed', '13', '-Seconds', '30') -PassThru -WindowStyle Hidden

try {
    Start-Sleep -Milliseconds 400

    # wait until both holders logged in and joined the group (max ~7s)
    $status = $null
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 300
        try { $status = Get-Json '/api/status' } catch { $status = $null }
        if ($null -eq $status) { continue }
        $probe = @($status.games | Where-Object { $_.id -eq 'ULUS10084' -and @($_.groups).Count -ge 1 })
        if ($status.stats.online -ge 3 -and $probe.Count -ge 1) { break }
    }
    Assert ($null -ne $status) 'status reachable' ''
    Assert ([bool]$status.ok) 'status.ok' ''
    Assert ($status.stats.online -ge 3) 'three sessions online' ("online=" + $status.stats.online)

    $mhf = $status.games | Where-Object { $_.id -eq 'ULUS10084' }
    Assert ($null -ne $mhf) 'game ULUS10084 present' ''
    Assert ($mhf.players -ge 2) 'two players share the crosslinked game' ("players=" + $mhf.players)
    Assert ($mhf.name -like '*Monster Hunter Freedom*') 'game name resolved from db' $mhf.name
    Assert (@($mhf.groups | Where-Object { $_.name -eq 'ABCD1234' }).Count -eq 1) 'group ABCD1234 visible' ''
    Assert (@(($mhf.groups | Where-Object { $_.name -eq 'ABCD1234' }).members | Where-Object { $_.isHost }).Count -eq 1) 'exactly one group host' ''
    Assert (($mhf.groups | Where-Object { $_.name -eq 'ABCD1234' }).members.Count -eq 2) 'both players in group' ''

    $mhfu = $status.games | Where-Object { $_.id -eq 'ULUS10391' }
    Assert ($null -ne $mhfu) 'crosslink ULES01213 -> ULUS10391' ''
    Assert ($mhfu.name -like '*Unite*') 'crosslinked title resolved' $mhfu.name

    $health = Get-Json '/api/health'
    Assert ([bool]$health.ok) 'health endpoint' ''

    $games = Get-Json '/api/games'
    Assert ($games.count -ge 150) 'games catalog loaded' ('count=' + $games.count)
    Assert (@($games.games | Where-Object { $_.id -eq 'ULUS10084' }).Count -eq 1) 'catalog contains ULUS10084' ''

    # add a title for an unknown id, then confirm it round-trips
    $save = Post-Json '/api/games' @{ id = 'ULUS99999'; name = 'Test Game (automation)' }
    Assert ([bool]$save.ok) 'POST /api/games saves' ''
    $games2 = Get-Json '/api/games'
    Assert (@($games2.games | Where-Object { $_.id -eq 'ULUS99999' -and $_.name -eq 'Test Game (automation)' }).Count -eq 1) 'new title visible in catalog' ''

    # broadcast chat (no players in that group? holders are in ABCD1234, so >= 1 target)
    $chat = Post-Json '/api/chat' @{ text = 'automation ping'; sender = 'TEST' }
    Assert ([bool]$chat.ok) 'POST /api/chat accepted' ''
    Assert ($chat.sent -ge 1) 'chat delivered to holders' ('sent=' + $chat.sent)

    # kick one player by session id
    $victim = $mhf.groups[0].members[0]
    $kick = Post-Json '/api/kick' @{ id = $victim.id }
    Assert ([bool]$kick.ok) 'POST /api/kick works' (ConvertTo-Json $kick -Compress)
    Start-Sleep -Milliseconds 900
    $status2 = Get-Json '/api/status'
    Assert ($status2.stats.online -lt $status.stats.online) 'kicked session disappeared' ("before=" + $status.stats.online + " after=" + $status2.stats.online)

    $xml = (Invoke-WebRequest -UseBasicParsing -Uri ($Base + '/status.xml') -TimeoutSec 10).Content
    Assert ($xml -like '*<prometheus usercount=*') 'legacy status.xml' ''

    $logs = Get-Json '/api/logs?limit=20&level=all'
    Assert ($logs.lines.Count -gt 0) 'logs endpoint returns lines' ''
    Assert (@($logs.lines | Where-Object { $_.cat -eq 'PROTO' }).Count -ge 1) 'protocol log entries present' ''

    $hist = Get-Json '/api/history?limit=10'
    Assert ([bool]$hist.ok) 'history endpoint' ''

    # static assets: gzip + etag
    $req = [System.Net.HttpWebRequest]::Create($Base + '/assets/js/app.js')
    $req.AutomaticDecompression = [System.Net.DecompressionMethods]::None
    $req.Headers.Add('Accept-Encoding', 'gzip')
    $resp = $req.GetResponse()
    $enc = $resp.Headers['Content-Encoding']
    $etag = $resp.Headers['ETag']
    $resp.Close()
    Assert ($enc -eq 'gzip') 'static assets are gzipped' ("encoding=" + $enc)
    Assert (-not [string]::IsNullOrEmpty($etag)) 'static assets have an ETag' ''

    $req2 = [System.Net.HttpWebRequest]::Create($Base + '/assets/js/app.js')
    $req2.Headers.Add('If-None-Match', $etag)
    try {
        $r2 = $req2.GetResponse()
        $code = [int]$r2.StatusCode
        $r2.Close()
    } catch {
        $resp3 = $_.Exception.Response; if (-not $resp3 -and $_.Exception.InnerException) { $resp3 = $_.Exception.InnerException.Response }; if ($resp3) { $code = [int]$resp3.StatusCode }
    }
    Assert ($code -eq 304) 'conditional GET returns 304' ("code=" + $code)

    $index = Invoke-WebRequest -UseBasicParsing -Uri ($Base + '/') -TimeoutSec 10
    Assert ($index.Content -like '*DPT.Agent*') 'index.html served' ''
}
finally {
    foreach ($p in @($p1, $p2, $p3)) {
        try { if ($p -and -not $p.HasExited) { $p.Kill() } } catch { }
    }
    if ($backup -and (Test-Path $backup)) {
        Start-Sleep -Milliseconds 300
        Copy-Item $backup $dataFile -Force
        Remove-Item $backup -Force
        try { Post-Json '/api/reload' @{} | Out-Null } catch { }
    }
}

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "All web checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
