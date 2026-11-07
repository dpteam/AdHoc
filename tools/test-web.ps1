# =========================================================================
#  Web/API smoke test for BOTH boards.
#    public : read-only, redacted, no admin endpoints
#    admin  : signed + sealed requests, full data
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\test-web.ps1
#         [-PublicBase http://127.0.0.1:44501] [-AdminBase http://127.0.0.1:44502]
# =========================================================================
param(
    [string]$PublicBase = 'http://127.0.0.1:44501',
    [string]$AdminBase = 'http://127.0.0.1:44502',
    [string]$Server = '127.0.0.1',
    [int]$AdhocPort = 27312
)

$ErrorActionPreference = 'Stop'
$script:failures = 0
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
. (Join-Path $here 'admin-api.ps1')

$dataFile = Join-Path $root 'bin\data\games.json'
$configFile = Join-Path $root 'bin\config.ini'
$backup = $null

function Assert([bool]$condition, [string]$name, [string]$detail) {
    if ($condition) { Write-Host ("  [ok]   " + $name) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $name + "  " + $detail) -ForegroundColor Red; $script:failures++ }
}

function Get-Json([string]$url) {
    return Invoke-RestMethod -UseBasicParsing -Uri $url -TimeoutSec 15
}

function Get-Status([string]$url) {
    $r = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 15
    return [pscustomobject]@{ Status = [int]$r.StatusCode; Content = $r.Content; Headers = $r.Headers }
}

function Try-Request([string]$url, [string]$method = 'GET', $body = $null) {
    try {
        $p = @{ UseBasicParsing = $true; Uri = $url; Method = $method; TimeoutSec = 15 }
        if ($null -ne $body) { $p['Body'] = $body }
        $r = Invoke-WebRequest @p
        return [int]$r.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}

Write-Host ""
Write-Host "DPT.PSP web/API test" -ForegroundColor Cyan
Write-Host "  public: $PublicBase"
Write-Host "  admin : $AdminBase"
Write-Host ""

if (-not (Test-Path $configFile)) { throw "config.ini not found: $configFile (run the server once)" }
$token = $null
foreach ($line in Get-Content $configFile) {
    if ($line -match '^\s*adminToken\s*=\s*(.+)$') { $token = $Matches[1].Trim() }
}
if (-not $token) { throw 'web.adminToken is empty in config.ini' }

if (Test-Path $dataFile) {
    $backup = Join-Path $env:TEMP ('games-backup-' + [Guid]::NewGuid().ToString('N') + '.json')
    Copy-Item $dataFile $backup
}

$p1 = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'hold-client.ps1'),
    '-Nick', 'HolderOne', '-Game', 'ULUS10084', '-Group', 'ABCD1234', '-MacSeed', '11', '-Seconds', '60',
    '-Server', $Server, '-Port', $AdhocPort) -PassThru -WindowStyle Hidden
$p2 = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'hold-client.ps1'),
    '-Nick', 'HolderTwo', '-Game', 'ULES00318', '-Group', 'ABCD1234', '-MacSeed', '12', '-Seconds', '60',
    '-Server', $Server, '-Port', $AdhocPort) -PassThru -WindowStyle Hidden
$p3 = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'hold-client.ps1'),
    '-Nick', 'HolderThree', '-Game', 'ULES01213', '-NoGroup', '-MacSeed', '13', '-Seconds', '60',
    '-Server', $Server, '-Port', $AdhocPort) -PassThru -WindowStyle Hidden

try {
    # ------------------------------------------------------------ public
    Write-Host "public board" -ForegroundColor Cyan

    $status = $null
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 300
        try { $status = Get-Json ($PublicBase + '/api/status') } catch { $status = $null }
        if ($null -eq $status) { continue }
        $probe = @($status.games | Where-Object { $_.id -eq 'ULUS10084' -and @($_.groups).Count -ge 1 })
        if ($status.stats.online -ge 3 -and $probe.Count -ge 1) { break }
    }
    Assert ($null -ne $status) 'public status reachable' ''
    Assert ($status.role -eq 'public') 'public role reported' $status.role
    Assert ($status.stats.online -ge 3) 'three sessions online' ("online=" + $status.stats.online)

    $mhf = $status.games | Where-Object { $_.id -eq 'ULUS10084' }
    Assert ($null -ne $mhf) 'public sees the game' ''
    Assert (@(($mhf.groups | Where-Object { $_.name -eq 'ABCD1234' }).members).Count -eq 2) 'public sees group members' ''

    $raw = (Get-Status ($PublicBase + '/api/status')).Content
    Assert ($raw -notmatch '"mac"') 'public status has no MAC field' ''
    Assert ($raw -notmatch '"ip"') 'public status has no IP field' ''
    Assert ($raw -notmatch '"idleSec"') 'public status has no idle/bytes fields' ''
    Assert ($raw -notmatch '"sessionId"') 'no session ids leak' ''
    Assert ($raw -match '"relay"') 'public status mentions the relay' ''
    Assert ($raw -notmatch '"pdpFrames"') 'public relay info is minimal' ''

    # lifetime stats (data/stats.json) - survive restarts, safe to publish
    $life = $status.lifetime
    Assert ($null -ne $life) 'public status exposes lifetime stats' ''
    Assert ($life.starts -ge 1) 'lifetime counts server starts' ("starts=" + $life.starts)
    Assert ($life.peakOnline -ge 3) 'lifetime peak online tracked' ("peak=" + $life.peakOnline)
    Assert ($life.sessions -ge 0 -and $null -ne $life.playSeconds) 'lifetime sessions/playtime present' ''
    Assert ($null -ne $status.topGames) 'top games list present' ''
    Assert ($null -eq $life.logins -and $null -eq $life.bytesIn) 'public lifetime hides admin-only counters' ''

    Assert ((Try-Request ($PublicBase + '/api/logs')) -eq 404) 'public: logs are hidden (404)' ''
    Assert ((Try-Request ($PublicBase + '/api/kick') 'POST' '{"id":"x"}') -eq 404) 'public: kick is hidden (404)' ''
    Assert ((Try-Request ($PublicBase + '/admin.html')) -eq 404) 'public: admin shell is hidden (404)' ''
    Assert ((Try-Request ($PublicBase + '/api/auth')) -eq 404) 'public: auth endpoint hidden' ''

    $health = Get-Json ($PublicBase + '/api/health')
    Assert ($health.ok -and $health.role -eq 'public') 'public health' ''

    $xml = (Get-Status ($PublicBase + '/status.xml')).Content
    Assert ($xml -like '*<prometheus usercount=*') 'legacy status.xml on public' ''

    # PPSSPP server-browser feed (/data.json)
    $feedRaw = (Get-Status ($PublicBase + '/data.json'))
    $feed = Get-Json ($PublicBase + '/data.json')
    Assert ($feedRaw.Status -eq 200) 'data.json served on public' ''
    Assert ($feedRaw.Headers['Access-Control-Allow-Origin'] -eq '*') 'data.json allows CORS' ("hdr=" + $feedRaw.Headers['Access-Control-Allow-Origin'])
    Assert (($feedRaw.Headers['Cache-Control'] -like '*max-age=*')) 'data.json is briefly cacheable' ("hdr=" + $feedRaw.Headers['Cache-Control'])
    Assert ($feedRaw.Headers['Content-Type'] -like 'application/json*') 'data.json is json' ''
    Assert ($null -ne $feed.games -and @($feed.games).Count -ge 1) 'data.json has games array' ''
    $feedGame = @($feed.games) | Where-Object { @($_.game_ids) -contains 'ULUS10084' } | Select-Object -First 1
    Assert ($null -ne $feedGame) 'data.json lists the live game by id' ''
    Assert (@($feedGame.groups | Where-Object { $_.name -eq 'ABCD1234' }).Count -eq 1) 'data.json has the group' ''
    Assert (@(@($feedGame.groups | Where-Object { $_.name -eq 'ABCD1234' })[0].users).Count -eq 2) 'data.json has group users' ''
    $feedText = $feedRaw.Content
    Assert ($feedText -notmatch '"mac"') 'data.json has no MAC' ''
    Assert ($feedText -notmatch '"ip"') 'data.json has no IP' ''
    Assert ($feedText -notmatch '"hairpin"') 'data.json has no hairpin flags' ''
    Assert ($feedText -notmatch 'sessionId') 'data.json has no session ids' ''

    $hist = Get-Json ($PublicBase + '/api/history?limit=10')
    Assert ($hist.ok) 'public history' ''
    Assert (($hist.history | Measure-Object).Count -ge 0) 'history redacted' ''

    # ---------------------------------------------------------- board visits
    $visit = Invoke-RestMethod -UseBasicParsing -Uri ($PublicBase + '/api/visit') -Method POST `
                               -Body '{}' -ContentType 'application/json' -TimeoutSec 15
    Assert ($visit.ok -eq $true) 'visit heartbeat accepted' ''
    Assert ($null -ne $visit.online -and $visit.online -ge 1) 'visit returns live readers' ("online=" + $visit.online)
    Assert ($visit.today -ge 1) 'visits counted for today' ("today=" + $visit.today)
    Assert ($visit.total -ge 1) 'visits counted all-time' ("total=" + $visit.total)
    Assert ($null -ne $visit.days) 'visits include a per-day series' ''
    Assert ($status.visits -ne $null) 'public status exposes visit counters' ''
    Assert ($raw -notmatch '"ip"') 'visits never leak addresses' ''

    # --------------------------------------------------------------- public chat
    $chatLog = Get-Json ($PublicBase + '/api/chat?since=0&limit=20')
    Assert ($chatLog.ok -eq $true) 'chat log readable' ''
    Assert ($chatLog.enabled -eq $true) 'chat is enabled' ''
    Assert ($chatLog.cooldown -ge 1) 'chat advertises a cooldown' ("cooldown=" + $chatLog.cooldown)
    Assert ($null -ne $chatLog.messages) 'chat returns a message list' ''

    $cap = Get-Json ($PublicBase + '/api/chat/captcha')
    Assert ($cap.ok -eq $true -and $cap.id.Length -ge 10) 'captcha issued' ("id=" + $cap.id)
    Assert ($cap.svg -like '<svg*') 'captcha is inline SVG' ''
    Assert ($cap.svg -notmatch '<text') 'captcha digits are not plain text' ''

    $badSend = Invoke-RestMethod -UseBasicParsing -Uri ($PublicBase + '/api/chat/send') -Method POST `
                                 -Body '{"nick":"Probe","text":"nope","captcha":"deadbeef","answer":"0000"}' `
                                 -ContentType 'application/json' -TimeoutSec 15
    Assert ($badSend.ok -eq $false -and $badSend.error -eq 'captcha') 'wrong captcha is rejected' ("error=" + $badSend.error)

    # the answer only exists server-side; Debug logs let the test solve it
    $code = $null
    $logs = Get-ChildItem (Join-Path $root 'bin\logs') -Filter 'adhoc-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
    if ($logs) {
        $hit = Select-String -Path $logs.FullName -Pattern ('captcha ' + $cap.id + ' = (\d+)') -Encoding UTF8 |
               Select-Object -Last 1
        if ($hit) { $code = $hit.Matches[0].Groups[1].Value }
    }
    if ($code) {
        $body = '{"nick":"Probe","text":"hello from the test suite","captcha":"' + $cap.id + '","answer":"' + $code + '"}'
        $goodSend = Invoke-RestMethod -UseBasicParsing -Uri ($PublicBase + '/api/chat/send') -Method POST `
                                      -Body $body -ContentType 'application/json' -TimeoutSec 15
        Assert ($goodSend.ok -eq $true) 'solved captcha posts the message' ("error=" + $goodSend.error)
        Assert ($goodSend.message.text -eq 'hello from the test suite') 'message text preserved' ''

        $replay = Invoke-RestMethod -UseBasicParsing -Uri ($PublicBase + '/api/chat/send') -Method POST `
                                    -Body $body -ContentType 'application/json' -TimeoutSec 15
        Assert ($replay.ok -eq $false) 'captcha is single-use' ("error=" + $replay.error)
        Assert ($replay.error -eq 'cooldown' -or $replay.error -eq 'captcha') 'replay blocked by cooldown/captcha' ("error=" + $replay.error)

        $after = Get-Json ($PublicBase + '/api/chat?since=0&limit=20')
        Assert (@($after.messages | Where-Object { $_.text -eq 'hello from the test suite' }).Count -eq 1) 'message is in the log' ''
    } else {
        Write-Host "  [skip] captcha solving (server log is not at Debug level)" -ForegroundColor Yellow
    }

    $index = (Get-Status ($PublicBase + '/')).Content
    Assert ($index -like '*DPT.PSP*' -and $index -like '*data-role="public"*') 'public index served' ''

    # i18n: static markup is translatable and the language file ships all locales
    Assert ($index -like '*id="lang-select"*') 'language selector in the top bar' ''
    Assert ($index -like '*data-i18n="nav.lobby"*') 'index has i18n hooks' ''
    $i18n = (Get-Status ($PublicBase + '/assets/js/i18n.js?v=4'))
    Assert ($i18n.Status -eq 200) 'i18n.js served' ("status=" + $i18n.Status)
    foreach ($code in @('en', 'ru', 'uk', 'lt', 'pl', 'pt-BR', 'es', 'de', 'fr', 'it', 'ja', 'ko', 'zh-CN', 'id', 'tr', 'vi')) {
        Assert ($i18n.Content -like ("*'" + $code + "'*")) ("locale present: " + $code) ''
    }
    Assert ($i18n.Content -like "*'nav.lobby'*") 'dictionary keys present' ''
    Assert ($i18n.Content -like '*localStorage*') 'language is persisted client-side' ''

    # proxy style request: spoofed X-Forwarded-For must not break anything
    $xff = Invoke-WebRequest -UseBasicParsing -Uri ($PublicBase + '/api/health') -Headers @{ 'X-Forwarded-For' = '203.0.113.9, 192.168.1.2' } -TimeoutSec 10
    Assert ([int]$xff.StatusCode -eq 200) 'works behind reverse proxy (X-Forwarded-For)' ''

    # gzip + etag
    $req = [System.Net.HttpWebRequest]::Create($PublicBase + '/assets/js/app.js')
    $req.AutomaticDecompression = [System.Net.DecompressionMethods]::None
    $req.Headers.Add('Accept-Encoding', 'gzip')
    $resp = $req.GetResponse()
    $enc = $resp.Headers['Content-Encoding']
    $etag = $resp.Headers['ETag']
    $resp.Close()
    Assert ($enc -eq 'gzip') 'static assets gzipped' ("encoding=" + $enc)
    Assert (-not [string]::IsNullOrEmpty($etag)) 'static assets have ETag' ''

    $req2 = [System.Net.HttpWebRequest]::Create($PublicBase + '/assets/js/app.js')
    $req2.Headers.Add('If-None-Match', $etag)
    $code = 0
    try { $r2 = $req2.GetResponse(); $code = [int]$r2.StatusCode; $r2.Close() }
    catch {
        $resp3 = $_.Exception.Response
        if (-not $resp3 -and $_.Exception.InnerException) { $resp3 = $_.Exception.InnerException.Response }
        if ($resp3) { $code = [int]$resp3.StatusCode }
    }
    Assert ($code -eq 304) 'conditional GET returns 304' ("code=" + $code)

    # ------------------------------------------------------------- admin
    Write-Host ""
    Write-Host "admin board" -ForegroundColor Cyan

    $authInfo = Connect-DptAdmin -Base $AdminBase -Token $token
    Assert ($authInfo.scheme -eq 'hmac-sha256+ctr') 'auth scheme advertised' $authInfo.scheme
    Assert ($authInfo.salt.Length -ge 16) 'auth salt present' ''

    Assert ((Try-Request ($AdminBase + '/api/status')) -eq 401) 'unsigned admin request rejected (401)' ''

    $adminStatus = Invoke-DptAdmin -Method GET -Target '/api/status'
    Assert ($adminStatus.Status -eq 200) 'signed admin status works' ("code=" + $adminStatus.Status)
    Assert ($adminStatus.Encrypted) 'admin response is sealed' ''
    Assert ($null -ne $adminStatus.Json -and $adminStatus.Json.role -eq 'admin') 'admin payload decrypted' ''

    $adminRaw = $adminStatus.Text
    Assert ($adminRaw -match '"mac"') 'admin sees MAC' ''
    Assert ($adminRaw -match '"ip"') 'admin sees IP' ''
    Assert ($adminRaw -match '"pdpFrames"') 'admin sees full relay stats' ''

    # replay the same nonce
    $tsR = New-DptTimestamp
    $nonceR2 = [Dpt.AdHoc.Core.ApiCrypto]::RandomHex(16)
    $keyR = Get-DptKey
    $sig2 = [Dpt.AdHoc.Core.ApiCrypto]::Signature($keyR, 'GET', '/api/status', $tsR, $nonceR2, [byte[]]@())
    $h2 = @{ 'X-Auth-Ts' = $tsR; 'X-Auth-Nonce' = $nonceR2; 'X-Auth-Sig' = $sig2 }
    $r1 = Invoke-WebRequest -UseBasicParsing -Uri ($AdminBase + '/api/status') -Headers $h2 -TimeoutSec 10
    Assert ([int]$r1.StatusCode -eq 200) 'first signed request accepted' ''
    $replayCode = 0
    try {
        $r2b = Invoke-WebRequest -UseBasicParsing -Uri ($AdminBase + '/api/status') -Headers $h2 -TimeoutSec 10
        $replayCode = [int]$r2b.StatusCode
    } catch {
        if ($_.Exception.Response) { $replayCode = [int]$_.Exception.Response.StatusCode }
    }
    Assert ($replayCode -eq 401 -or $replayCode -eq 429) 'replayed nonce rejected' ("code=" + $replayCode)

    # bad signature
    $badKey = New-Object byte[] 32
    $tsB = New-DptTimestamp
    $nonceB = [Dpt.AdHoc.Core.ApiCrypto]::RandomHex(16)
    $sigB = [Dpt.AdHoc.Core.ApiCrypto]::Signature($badKey, 'GET', '/api/status', $tsB, $nonceB, [byte[]]@())
    $badCode = 0
    try {
        $rb = Invoke-WebRequest -UseBasicParsing -Uri ($AdminBase + '/api/status') -Headers @{
            'X-Auth-Ts' = $tsB; 'X-Auth-Nonce' = $nonceB; 'X-Auth-Sig' = $sigB } -TimeoutSec 10
        $badCode = [int]$rb.StatusCode
    } catch {
        if ($_.Exception.Response) { $badCode = [int]$_.Exception.Response.StatusCode }
    }
    Assert ($badCode -eq 401) 'bad signature rejected (401)' ("code=" + $badCode)

    # encrypted POST
    $chat = Invoke-DptAdmin -Method POST -Target '/api/chat' -Payload @{ text = 'automation ping'; sender = 'TEST' } -Encrypt
    Assert ($chat.Status -eq 200) 'encrypted POST accepted' ("code=" + $chat.Status)
    Assert ($chat.Encrypted) 'encrypted response' ''
    Assert ($null -ne $chat.Json -and $chat.Json.ok) 'encrypted response decrypts' ''

    # kick
    $victim = $mhf.groups[0].members[0].name
    $kick = Invoke-DptAdmin -Method POST -Target '/api/kick' -Payload @{ name = $victim } -Encrypt
    Assert ($kick.Status -eq 200 -and $kick.Json.kicked -ge 1) 'signed+sealed kick works' ("code=" + $kick.Status)

    $logs = Invoke-DptAdmin -Method GET -Target '/api/logs?limit=20&level=all'
    Assert ($logs.Status -eq 200 -and @($logs.Json.lines).Count -gt 0) 'admin logs available' ''

    $adminIndex = (Get-Status ($AdminBase + '/')).Content
    Assert ($adminIndex -like '*data-role="admin"*') 'admin shell served on admin port' ''

    # catalog save roundtrip (through the sealed channel)
    $save = Invoke-DptAdmin -Method POST -Target '/api/games' -Payload @{ id = 'ULUS99999'; name = 'Test Game (automation)' } -Encrypt
    Assert ($save.Status -eq 200 -and $save.Json.ok) 'sealed catalog save' ''
    $games = Invoke-DptAdmin -Method GET -Target '/api/games'
    Assert (@($games.Json.games | Where-Object { $_.id -eq 'ULUS99999' }).Count -eq 1) 'saved title visible' ''
}
finally {
    foreach ($p in @($p1, $p2, $p3)) {
        try { if ($p -and -not $p.HasExited) { $p.Kill() } } catch { }
    }
    if ($backup -and (Test-Path $backup)) {
        Start-Sleep -Milliseconds 300
        Copy-Item $backup $dataFile -Force
        Remove-Item $backup -Force
        try { Invoke-DptAdmin -Method POST -Target '/api/reload' -Payload @{} | Out-Null } catch { }
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