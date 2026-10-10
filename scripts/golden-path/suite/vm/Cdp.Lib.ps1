<#
.SYNOPSIS
    Drives Edge in the VM's signed-in desktop session over its DevTools port, so the suite can read what a person sees on
    Deluno's screens and press the buttons they press (golden path scenario suite).

.DESCRIPTION
    Dot-source this file. Start-Edge opens Edge with a DevTools port on 127.0.0.1; New-CdpPage opens a tab; Invoke-CdpEval runs
    a script in it; Get-PageText reads the visible text; Click-PageText presses the control whose text matches, with real mouse
    events. Windows PowerShell 5.1 (System.Net.WebSockets.ClientWebSocket).
#>

$script:Cdp = @{ Port = 9222; Id = 0 }

function Start-Edge {
    param([int] $Port = 9222, [string] $Profile = 'C:\golden\suite\edge-profile')
    $script:Cdp.Port = $Port
    try { Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/version" -TimeoutSec 2 | Out-Null; return } catch { }
    $exe = @('C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe', 'C:\Program Files\Microsoft\Edge\Application\msedge.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) { throw 'Edge is not installed in the VM.' }
    Start-Process -FilePath $exe -ArgumentList "--remote-debugging-port=$Port", "--user-data-dir=$Profile", '--no-first-run', '--no-default-browser-check', '--window-size=1366,900', 'about:blank' | Out-Null
    $ready = $false
    for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 500
        try { Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/version" -TimeoutSec 2 | Out-Null; $ready = $true } catch { }
    }
    if (-not $ready) { throw 'Edge did not open its DevTools port.' }
}

function Stop-Edge { Get-Process -Name msedge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }

function Receive-CdpMessage {
    param($Ws, [int] $TimeoutMs = 20000)
    $cts = New-Object System.Threading.CancellationTokenSource($TimeoutMs)
    $buffer = New-Object byte[] 65536
    $ms = New-Object System.IO.MemoryStream
    try {
        do {
            $seg = New-Object System.ArraySegment[byte] -ArgumentList @(, $buffer)
            $res = $Ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
            if ($res.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
            $ms.Write($buffer, 0, $res.Count)
        } while (-not $res.EndOfMessage)
    } catch { return $null } finally { $cts.Dispose() }
    [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
}

# Opens a new tab at $Url and returns the page handle.
function New-CdpPage {
    param([string] $Url = 'about:blank')
    $port = $script:Cdp.Port
    $target = $null
    try { $target = Invoke-RestMethod -Uri "http://127.0.0.1:$port/json/new?$([uri]::EscapeDataString($Url))" -Method Put -TimeoutSec 10 }
    catch { $target = Invoke-RestMethod -Uri "http://127.0.0.1:$port/json/new?$([uri]::EscapeDataString($Url))" -TimeoutSec 10 }
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    [void]$ws.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $page = [pscustomobject]@{ Ws = $ws; TargetId = $target.id }
    Send-Cdp -Page $page -Method 'Page.enable' | Out-Null
    Send-Cdp -Page $page -Method 'Runtime.enable' | Out-Null
    $page
}

function Close-CdpPage {
    param($Page)
    try { [void]$Page.Ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } catch { }
    try { Invoke-RestMethod -Uri "http://127.0.0.1:$($script:Cdp.Port)/json/close/$($Page.TargetId)" -TimeoutSec 5 | Out-Null } catch { }
}

function Send-Cdp {
    param($Page, [string] $Method, $Params = @{}, [int] $TimeoutMs = 20000)
    $script:Cdp.Id++
    $id = $script:Cdp.Id
    $json = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object System.ArraySegment[byte] -ArgumentList @(, $bytes)
    [void]$Page.Ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $msg = Receive-CdpMessage -Ws $Page.Ws -TimeoutMs $TimeoutMs
        if ($null -eq $msg) { break }
        if ($msg.PSObject.Properties.Name -contains 'id' -and $msg.id -eq $id) {
            if ($msg.PSObject.Properties.Name -contains 'error') { throw "DevTools $Method failed: $($msg.error.message)" }
            return $msg.result
        }
    }
    throw "DevTools $Method did not answer."
}

# Runs JavaScript in the page and returns its value (promises are awaited).
function Invoke-CdpEval {
    param($Page, [string] $Script, [int] $TimeoutMs = 20000)
    $r = Send-Cdp -Page $Page -Method 'Runtime.evaluate' -Params @{ expression = $Script; returnByValue = $true; awaitPromise = $true } -TimeoutMs $TimeoutMs
    if ($r.PSObject.Properties.Name -contains 'exceptionDetails') { throw "Page script failed: $($r.exceptionDetails.text) $($r.exceptionDetails.exception.description)" }
    $r.result.value
}

# Signs the page in as the suite's login by writing the keys the app keeps its sign-in in, then opens $Path.
function Open-DelunoPage {
    param($Page, [Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] $Login)
    $base = $script:S.DelunoUrl
    Send-Cdp -Page $Page -Method 'Page.navigate' -Params @{ url = "$base/" } | Out-Null
    Start-Sleep -Seconds 2
    $user = if ($Login.user) { ($Login.user | ConvertTo-Json -Compress -Depth 5) } else { '{}' }
    $js = 'localStorage.setItem("deluno-auth-token",' + ($Login.accessToken | ConvertTo-Json) + ');localStorage.setItem("deluno-auth-user",' + ($user | ConvertTo-Json) + ');' +
        $(if ($Login.expiresUtc) { 'localStorage.setItem("deluno-auth-expires",' + ($Login.expiresUtc | ConvertTo-Json) + ');' } else { '' }) + '1'
    Invoke-CdpEval -Page $Page -Script $js | Out-Null
    Send-Cdp -Page $Page -Method 'Page.navigate' -Params @{ url = "$base$Path" } | Out-Null
    Start-Sleep -Seconds 3
}

function Get-PageText {
    param($Page)
    [string](Invoke-CdpEval -Page $Page -Script 'document.body ? document.body.innerText : ""')
}

# The visible text inside the open drawer or dialog, or the whole page when there is none.
function Get-DrawerText {
    param($Page)
    [string](Invoke-CdpEval -Page $Page -Script '(function(){var d=document.querySelector("[role=dialog],[data-state=open][role=dialog],aside[aria-label]");return (d?d:document.body).innerText;})()')
}

# Finds the smallest visible clickable element (button, link, row) whose text matches the pattern and presses it with a real
# mouse click. Returns $true when one was found.
function Click-PageText {
    param($Page, [Parameter(Mandatory)] [string] $Pattern, [string] $Within = '')
    $js = @"
(function(){
  var re = new RegExp($(($Pattern | ConvertTo-Json)), 'i');
  var scope = $(if ($Within) { 'document.querySelector(' + ($Within | ConvertTo-Json) + ')||document' } else { 'document' });
  var els = Array.prototype.slice.call(scope.querySelectorAll('button,a,[role=button],[role=row],tr,[role=tab]'));
  var best = null, bestLen = 1e9;
  els.forEach(function(e){
    var t = (e.getAttribute('aria-label') || e.innerText || '').trim();
    var r = e.getBoundingClientRect();
    if (!re.test(t) || r.width < 2 || r.height < 2 || e.disabled) return;
    if (t.length < bestLen) { best = e; bestLen = t.length; }
  });
  if (!best) return null;
  best.scrollIntoView({block:'center'});
  var r = best.getBoundingClientRect();
  return {x: r.left + r.width/2, y: r.top + r.height/2};
})()
"@
    $pt = Invoke-CdpEval -Page $Page -Script $js
    if (-not $pt) { return $false }
    foreach ($type in 'mouseMoved', 'mousePressed', 'mouseReleased') {
        $params = @{ type = $type; x = $pt.x; y = $pt.y; button = 'left'; clickCount = 1 }
        Send-Cdp -Page $Page -Method 'Input.dispatchMouseEvent' -Params $params | Out-Null
    }
    $true
}
