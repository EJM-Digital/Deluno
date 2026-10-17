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
        Start-Sleep -Milliseconds 500 # reason: poll interval inside a loop bounded by its deadline
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
    Start-Sleep -Seconds 2 # reason: lets the page load before its sign-in keys are written; the page has no ready signal here
    $user = if ($Login.user) { ($Login.user | ConvertTo-Json -Compress -Depth 5) } else { '{}' }
    $js = 'localStorage.setItem("deluno-auth-token",' + ($Login.accessToken | ConvertTo-Json) + ');localStorage.setItem("deluno-auth-user",' + ($user | ConvertTo-Json) + ');' +
        $(if ($Login.expiresUtc) { 'localStorage.setItem("deluno-auth-expires",' + ($Login.expiresUtc | ConvertTo-Json) + ');' } else { '' }) + '1'
    Invoke-CdpEval -Page $Page -Script $js | Out-Null
    Send-Cdp -Page $Page -Method 'Page.navigate' -Params @{ url = "$base$Path" } | Out-Null
    Start-Sleep -Seconds 3 # reason: lets the page load and draw before it is read
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

# Presses the control whose text matches the pattern, the way a person does: it waits until the control is there, visible, enabled
# and not covered by something else, then clicks it with real mouse events. A disabled control (a drawer that is busy, say) ignores a
# click without a word, so the suite never clicks one: it waits up to -TimeoutSec and then throws a message that says which of
# "not on the page", "hidden", "disabled" or "covered" it was. With -TimeoutSec 0 it looks once and returns $false when the control
# is not ready (for a caller that is only probing); otherwise it returns $true.
function Click-PageText {
    param($Page, [Parameter(Mandatory)] [string] $Pattern, [string] $Within = '', [int] $TimeoutSec = 15, [switch] $RowOnly)
    $rowOnlyJs = if ($RowOnly) { 'true' } else { 'false' }
    $selector = if ($RowOnly) { '[role=row],tr' } else { 'button,a,[role=button],[role=row],tr,[role=tab]' }
    $scope = if ($Within) { 'document.querySelector(' + ($Within | ConvertTo-Json) + ')||document' } else { 'document' }
    $js = @"
(function(){
  var re = new RegExp($(($Pattern | ConvertTo-Json)), 'i');
  var scope = $scope;
  var els = Array.prototype.slice.call(scope.querySelectorAll('$selector'));
  var ready = null, readyLen = 1e9, blocked = null;
  els.forEach(function(e){
    var t = ($rowOnlyJs ? (e.innerText || '') : (e.getAttribute('aria-label') || e.innerText || '')).trim();
    if (!re.test(t)) return;
    var r = e.getBoundingClientRect();
    var cs = getComputedStyle(e);
    var why = null;
    if (r.width < 2 || r.height < 2 || cs.visibility === 'hidden' || cs.display === 'none') why = 'hidden';
    else if (e.disabled || e.getAttribute('aria-disabled') === 'true' || cs.pointerEvents === 'none' || e.closest('fieldset:disabled')) why = 'disabled';
    if (why) { if (!blocked || why === 'disabled') blocked = {why: why, text: t.slice(0, 60)}; return; }
    if (t.length < readyLen) { ready = e; readyLen = t.length; }
  });
  if (!ready) return blocked ? {state: blocked.why, text: blocked.text} : {state: 'none'};
  ready.scrollIntoView({block: 'center'});
  var r = ready.getBoundingClientRect();
  var x = r.left + r.width / 2, y = r.top + r.height / 2;
  var top = document.elementFromPoint(x, y);
  if (!top || !(ready === top || ready.contains(top) || top.contains(ready))) return {state: 'covered', text: (ready.innerText || '').trim().slice(0, 60)};
  return {state: 'ready', x: x, y: y};
})()
"@
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $last = $null
    while ($true) {
        $last = Invoke-CdpEval -Page $Page -Script $js
        if ($last -and $last.state -eq 'ready') { break }
        if ([DateTime]::UtcNow -ge $deadline) {
            if ($TimeoutSec -le 0) { return $false }
            $why = switch ($last.state) {
                'none' { 'no control with that text is on the page' }
                'hidden' { "the control '$($last.text)' is on the page but not visible" }
                'disabled' { "the control '$($last.text)' is disabled (the screen is still busy), so a click would do nothing" }
                'covered' { "the control '$($last.text)' is covered by something else" }
                default { "the control is not ready ($($last.state))" }
            }
            throw "Could not click /$Pattern/ within $TimeoutSec s: $why."
        }
        Start-Sleep -Milliseconds 250 # reason: poll interval inside a loop bounded by its deadline
    }
    foreach ($type in 'mouseMoved', 'mousePressed', 'mouseReleased') {
        $params = @{ type = $type; x = $last.x; y = $last.y; button = 'left'; clickCount = 1 }
        Send-Cdp -Page $Page -Method 'Input.dispatchMouseEvent' -Params $params | Out-Null
    }
    $true
}

# Types a value into the first element that matches the selector, the way a person typing would for a React-style form: the value
# is set through the element's own setter and the input and change events are sent.
function Set-PageInput {
    param($Page, [Parameter(Mandatory)] [string] $Selector, [Parameter(Mandatory)] [string] $Value)
    $js = "(function(){var e=document.querySelector(" + ($Selector | ConvertTo-Json) + ");if(!e)return false;e.focus();var d=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(e),'value');d.set.call(e," + ($Value | ConvertTo-Json) + ");e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));return true;})()"
    [bool](Invoke-CdpEval -Page $Page -Script $js)
}
