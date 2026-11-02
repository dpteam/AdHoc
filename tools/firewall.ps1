# =========================================================================
#  DPT.PSP AdHoc Server - Windows Firewall setup
#
#  Adds inbound TCP allow rules for:
#    * adhoc protocol (default any source - players connect from the internet)
#    * public web board (LAN/private sources by default - NGinx Proxy Manager
#      runs locally or on another LAN host)
#    * admin web board (LAN/private only, always)
#
#  Rules are scoped to AdHocServer.exe and removed/recreated on every run.
#  Requires admin rights: the script relaunches itself with a UAC prompt.
#
#  usage (double click firewall.cmd, or):
#    powershell -ExecutionPolicy Bypass -File tools\firewall.ps1
#    ... -PublicAny      allow the public port from anywhere (direct expose)
#    ... -AdhocLanOnly   allow adhoc only from LAN (no internet players)
#    ... -Remove         delete the DPT.PSP rules
# =========================================================================
[CmdletBinding()]
param(
    [switch]$PublicAny,
    [switch]$AdhocLanOnly,
    [switch]$Remove,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\AdHocServer.exe'
$ini = Join-Path $root 'bin\config.ini'
$reportPath = Join-Path $env:TEMP 'dpt-firewall.log'
$report = New-Object 'System.Collections.Generic.List[string]'

function Add-Line([string]$text) {
    $report.Add($text)
    Write-Host $text
}

function Save-Report {
    try { Set-Content -Path $reportPath -Value $report -Encoding UTF8 } catch { }
}

# --------------------------------------------------------------- elevation

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($PublicAny) { $argList += '-PublicAny' }
    if ($AdhocLanOnly) { $argList += '-AdhocLanOnly' }
    if ($Remove) { $argList += '-Remove' }
    if ($NoPause) { $argList += '-NoPause' }

    Write-Host "Нужны права администратора - сейчас появится запрос UAC (нажмите «Да»)." -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    } catch {
        Write-Host "Повышение прав отменено: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Запустите этот файл от имени администратора вручную." -ForegroundColor Red
    }
    Start-Sleep -Seconds 1
    exit
}

# ------------------------------------------------------------------- config

function Read-IniValues([string]$path) {
    $map = @{}
    if (-not (Test-Path $path)) { return $map }
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$') {
            $map[$Matches[1].ToLowerInvariant()] = $Matches[2]
        }
    }
    return $map
}

$values = Read-IniValues $ini
$portAdhoc = 27312
$portPublic = 44501
$portAdmin = 44502
$portRelay = 27313
$relayEnabled = $true
if ($values.ContainsKey('adhocport') -and $values['adhocport']) { $portAdhoc = [int]$values['adhocport'] }
if ($values.ContainsKey('publicport') -and $values['publicport']) { $portPublic = [int]$values['publicport'] }
if ($values.ContainsKey('adminport') -and $values['adminport']) { $portAdmin = [int]$values['adminport'] }
if ($values.ContainsKey('relay.port') -and $values['relay.port']) { $portRelay = [int]$values['relay.port'] }
if ($values.ContainsKey('relay.enabled') -and $values['relay.enabled'] -match '^(false|0|no|off)$') { $relayEnabled = $false }

$bindPublic = if ($values.ContainsKey('publicbind')) { $values['publicbind'] } else { '0.0.0.0' }
$bindAdmin = if ($values.ContainsKey('adminbind')) { $values['adminbind'] } else { '0.0.0.0' }

# --------------------------------------------------------------- addresses

$lanRemote = @(
    'LocalSubnet',
    '127.0.0.1/32',
    '10.0.0.0/8',
    '172.16.0.0/12',
    '192.168.0.0/16',
    '169.254.0.0/16',
    '100.64.0.0/10'
)
$anyRemote = @('Any')

Add-Line ''
Add-Line '=== DPT.PSP AdHoc Server - firewall setup ==='
Add-Line ('config : ' + $(if (Test-Path $ini) { $ini } else { 'not found, using defaults' }))
Add-Line ('exe    : ' + $(if (Test-Path $exe) { $exe } else { 'NOT FOUND (rules will be port-only)' }))
Add-Line ('ports  : adhoc=' + $portAdhoc + ' public=' + $portPublic + ' admin=' + $portAdmin +
          $(if ($relayEnabled) { ' relay=' + $portRelay } else { ' relay=off' }))
Add-Line ('bind   : public=' + $bindPublic + ' admin=' + $bindAdmin)
Add-Line ''

$rules = @(
    @{
        Name   = 'DPT.PSP AdHoc'
        Port   = $portAdhoc
        Remote = $(if ($AdhocLanOnly) { $lanRemote } else { $anyRemote })
        Note   = 'SceNetAdhocctl control port (PPSSPP)'
    },
    @{
        Name   = 'DPT.PSP Public board'
        Port   = $portPublic
        Remote = $(if ($PublicAny) { $anyRemote } else { $lanRemote })
        Note   = 'public web board (read-only)'
    },
    @{
        Name   = 'DPT.PSP Admin board'
        Port   = $portAdmin
        Remote = $lanRemote
        Note   = 'admin web board (LAN only)'
    }
)

if ($relayEnabled) {
    $rules += @{
        Name   = 'DPT.PSP Relay'
        Port   = $portRelay
        Remote = $(if ($AdhocLanOnly) { $lanRemote } else { $anyRemote })
        Note   = 'PPSSPP packet relay (aemu_postoffice, TCP)'
    }
}

$haveCmdlets = $null -ne (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)

function Remove-Rule([string]$name) {
    if ($haveCmdlets) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    } else {
        netsh advfirewall firewall delete rule name="$name" | Out-Null
    }
}

function Add-Rule($rule) {
    $name = $rule.Name
    $port = $rule.Port
    $remote = $rule.Remote
    $note = $rule.Note

    Remove-Rule $name

    if ($Remove) { return }

    if ($haveCmdlets) {
        $params = @{
            DisplayName  = $name
            Description  = 'DPT.PSP AdHoc Server - ' + $note
            Direction    = 'Inbound'
            Action       = 'Allow'
            Protocol     = 'TCP'
            LocalPort    = $port
            RemoteAddress = $remote
            Profile      = 'Any'
            Enabled      = 'True'
        }
        if (Test-Path $exe) { $params['Program'] = $exe }
        New-NetFirewallRule @params | Out-Null
    } else {
        $cmd = 'netsh advfirewall firewall add rule name="' + $name + '" dir=in action=allow protocol=TCP localport=' + $port + ' profile=any enable=yes'
        if (Test-Path $exe) { $cmd += ' program="' + $exe + '"' }
        if ($remote -notcontains 'Any') { $cmd += ' remoteip=' + ($remote -join ',') }
        Invoke-Expression $cmd | Out-Null
    }
}

# ------------------------------------------------------------------- apply

if ($Remove) {
    Add-Line 'Removing DPT.PSP firewall rules...' 
} else {
    Add-Line 'Adding inbound TCP rules...'
}

foreach ($rule in $rules) {
    try {
        Add-Rule $rule
        if ($Remove) {
            Add-Line ('  removed : ' + $rule.Name)
        } else {
            $scope = if ($rule.Remote -contains 'Any') { 'any source' } else { 'LAN/private only' }
            Add-Line ('  ok      : ' + $rule.Name.PadRight(22) + ' tcp/' + $rule.Port + '  (' + $scope + ')')
        }
    } catch {
        Add-Line ('  FAILED  : ' + $rule.Name + ' -> ' + $_.Exception.Message)
    }
}

# ------------------------------------------------------------- diagnostics

Add-Line ''
Add-Line '--- listening sockets ---'
try {
    $netstat = netstat -ano | Select-String 'LISTENING'
    foreach ($port in @($portAdhoc, $portPublic, $portAdmin, $portRelay)) {
        $hit = $netstat | Where-Object { $_.Line -match (':' + $port + '\s') } | Select-Object -First 1
        if ($hit) {
            $addr = ($hit.Line -split '\s+')[2]
            Add-Line ('  ' + $port + ' -> ' + $addr)
        } else {
            Add-Line ('  ' + $port + ' -> not listening (server stopped?)')
        }
    }
} catch {
    Add-Line ('  netstat failed: ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- local IPv4 addresses (используйте их в NGinx Proxy Manager) ---'
try {
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
    foreach ($ip in $ips) {
        Add-Line ('  ' + $ip.IPAddress + '/' + $ip.PrefixLength + '   [' + $ip.InterfaceAlias + ']')
    }
} catch {
    Add-Line ('  ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- local health check (обходит фаервол, проверяет сам сервер) ---'
foreach ($port in @($portPublic, $portAdmin)) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:' + $port + '/api/health') -TimeoutSec 5
        Add-Line ('  ' + $port + ' -> HTTP ' + [int]$r.StatusCode + ' ' + $r.Content)
    } catch {
        Add-Line ('  ' + $port + ' -> FAILED: ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line '--- rules ---'
if ($haveCmdlets) {
    try {
        Get-NetFirewallRule -DisplayName 'DPT.PSP*' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $pf = $_ | Get-NetFirewallPortFilter
                $af = $_ | Get-NetFirewallAddressFilter
                Add-Line ('  ' + $_.DisplayName + ' | ' + $_.Enabled + ' | ' + $_.Direction + '/' + $_.Action +
                          ' | tcp/' + $pf.LocalPort + ' | remote: ' + ($af.RemoteAddress -join ','))
            }
    } catch {
        Add-Line ('  ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line 'Подсказки:'
Add-Line '  * NGinx Proxy Manager в Docker НЕ видит 127.0.0.1 хоста - укажите LAN-IP сверху.'
Add-Line ('  * Схема в NPM: http + <LAN-IP> + порт ' + $portPublic + ', без пути (или с /adhoc).')
Add-Line '  * Публичная доска рассчитана на LAN-источник (NPM рядом). Прямой проброс 44501'
Add-Line '    наружу потребует запуска с -PublicAny.'
Add-Line ('  * Админка (порт ' + $portAdmin + ') остаётся только в приватных сетях.')
Add-Line ''
Add-Line ('Отчёт: ' + $reportPath)

Save-Report

if (-not $NoPause) {
    Write-Host ''
    Read-Host 'Готово. Enter - закрыть окно' | Out-Null
}