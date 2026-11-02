# =========================================================================
#  Helper for the admin API (signed + sealed requests).
#  Uses the server's own ApiCrypto implementation so the test cannot drift
#  from the protocol: Add-Type loads bin\AdHocServer.exe as an assembly.
#
#  . .\tools\admin-api.ps1
#  Connect-DptAdmin -Base http://127.0.0.1:44502 -Token <token from config.ini>
#  Invoke-DptAdmin -Method GET -Target '/api/status'
# =========================================================================

$script:DptRoot = Split-Path -Parent $PSScriptRoot
$script:DptKeys = @{}
$script:DptBase = ''
$script:DptToken = ''

function Connect-DptAdmin {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Token
    )

    $exe = Join-Path $script:DptRoot 'bin\AdHocServer.exe'
    if (-not ('Dpt.AdHoc.Core.ApiCrypto' -as [type])) {
        [void][System.Reflection.Assembly]::LoadFrom($exe)
    }

    $info = Invoke-RestMethod -UseBasicParsing -Uri ($Base + '/api/auth') -TimeoutSec 10
    if (-not $info.salt) { throw 'server did not return auth info' }

    $script:DptBase = $Base.TrimEnd('/')
    $script:DptToken = $Token
    $script:DptKeys[$script:DptBase] = [Dpt.AdHoc.Core.ApiCrypto]::DeriveKey($Token, $info.salt, [int]$info.iterations)
    return $info
}

function Get-DptKey {
    if (-not $script:DptKeys.ContainsKey($script:DptBase)) {
        throw 'call Connect-DptAdmin first'
    }
    return $script:DptKeys[$script:DptBase]
}

function New-DptTimestamp {
    $now = [DateTime]::UtcNow
    $epoch = New-Object DateTime(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    return [string][long][Math]::Floor(($now - $epoch).TotalSeconds)
}

<#
  Invoke-DptAdmin -Method GET|POST -Target '/api/status' [-Payload @{...}]
                  [-Encrypt] [-ReuseNonce <nonce>] [-Token <other>]
  Returns @{ Status; Headers; Json; Raw; Decrypted }
#>
function Invoke-DptAdmin {
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory = $true)][string]$Target,
        $Payload = $null,
        [switch]$Encrypt,
        [string]$ReuseNonce = '',
        [string]$Token = ''
    )

    if (-not $script:DptBase) { throw 'call Connect-DptAdmin first' }
    $base = $script:DptBase
    $key = Get-DptKey

    $wire = $null
    $headers = @{}
    if ($null -ne $Payload) {
        $json = $Payload | ConvertTo-Json -Compress
        if ($Encrypt) {
            $plain = [Text.Encoding]::UTF8.GetBytes($json)
            $wire = [Dpt.AdHoc.Core.ApiCrypto]::Seal($key, $plain)
            $headers['X-Auth-Enc'] = '1'
            $headers['Content-Type'] = 'text/plain; charset=utf-8'
        } else {
            $wire = $json
            $headers['Content-Type'] = 'application/json; charset=utf-8'
        }
    }

    $bodyBytes = if ($null -eq $wire) { [byte[]]@() } else { [Text.Encoding]::UTF8.GetBytes($wire) }
    if ($ReuseNonce) { $nonce = $ReuseNonce } else { $nonce = [Dpt.AdHoc.Core.ApiCrypto]::RandomHex(16) }
    $ts = New-DptTimestamp
    $sig = [Dpt.AdHoc.Core.ApiCrypto]::Signature($key, $Method.ToUpperInvariant(), $Target, $ts, $nonce, $bodyBytes)

    $headers['X-Auth-Ts'] = $ts
    $headers['X-Auth-Nonce'] = $nonce
    $headers['X-Auth-Sig'] = $sig
    if ($Token) { $headers['X-Admin-Token'] = $Token }

    $uri = $base + $Target
    $status = 0
    $content = ''
    $respHeaders = @{}
    try {
        $params = @{
            UseBasicParsing = $true
            Uri             = $uri
            Method          = $Method
            Headers         = $headers
            TimeoutSec      = 15
        }
        if ($null -ne $wire) { $params['Body'] = $wire }
        $r = Invoke-WebRequest @params
        $status = [int]$r.StatusCode
        $content = $r.Content
        foreach ($k in $r.Headers.Keys) { $respHeaders[$k] = $r.Headers[$k] }
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $status = [int]$resp.StatusCode
            $reader = New-Object IO.StreamReader($resp.GetResponseStream())
            $content = $reader.ReadToEnd()
            $reader.Close()
            foreach ($k in $resp.Headers.AllKeys) { $respHeaders[$k] = $resp.Headers[$k] }
        } else {
            throw
        }
    }

    $decrypted = $null
    $json = $null
    $text = $content
    if ($respHeaders.ContainsKey('X-Enc-Resp')) {
        $plainBytes = [Dpt.AdHoc.Core.ApiCrypto]::Open($key, $content.Trim())
        if ($plainBytes) { $text = [Text.Encoding]::UTF8.GetString($plainBytes) }
    }
    if ($text) { try { $json = $text | ConvertFrom-Json } catch { $json = $null } }

    return [pscustomobject]@{
        Status    = $status
        Headers   = $respHeaders
        Raw       = $content
        Text      = $text
        Json      = $json
        Encrypted = $respHeaders.ContainsKey('X-Enc-Resp')
    }
}