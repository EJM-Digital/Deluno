<#
    Deluno against the real apps and sites it talks to, and the Windows pieces around them, on the golden VM (coverage issue 1278).

    weir.test-connection      the connection test against the real Weir: its health address answering 2xx is healthy, the hand-off address
                              asked with HEAD (any answer is reachable, a closed port is not), an unexpected status is read as running but not
                              ready, and a redirect is followed.
    weir.credential-own-host  the credential reaches the processor's own address only: not a redirect target on another address, not an address
                              with another scheme, and a redirect loop ends.
    weir.health-findings      Health compares what the real Weir says it watches and writes to with the library's downloads and ready folders,
                              and raises a finding naming both when they disagree.
    service.mode-switching    Apply removes an existing service and the sign-in entry first: LocalSystem, a named account, the tray's own mode
                              (service gone, sign-in entry set) and --uninstall-service (service gone, entry cleared).
    picker.window-controls    the tray's picker window: components with licences, installed ones cannot be ticked, View licences, Install
                              with nothing ticked, Skip.
    tray.port-dialog          the Change port dialog's controls and words; tray.port-override-cleared: --port serves one run only.
    tray.running-balloons     the start-up balloons (the addresses, the firewall warning, the second launch).
    tray.relaunch-waits       a Deluno started with --after-restart waits for that process, up to 60 seconds.

    A small web listener on this machine (Golden.HttpProbe) plays the other side where a scenario needs to see what Deluno sends or to
    answer a redirect; it listens on the loopback address only and is stopped at the end.
#>

# ---------------------------------------------------------------- a small listener that records what it is sent

function Initialize-GoldenProbe {
    if ('Golden.HttpProbe' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Golden
{
    public class ProbeRequest
    {
        public string Method;
        public string Target;
        public string Headers;
        public string Raw;
    }

    public class HttpProbe
    {
        private TcpListener listener;
        private Thread thread;
        private volatile bool stopping;
        private readonly object gate = new object();
        private readonly List<ProbeRequest> requests = new List<ProbeRequest>();
        private readonly Dictionary<string, string> rules = new Dictionary<string, string>();
        public int Port;

        public HttpProbe(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        // spec: "redirect|<url>" or "status|<code>|<body>"; the path "*" answers every other path.
        public void Set(string path, string spec)
        {
            lock (gate) { rules[path] = spec; }
        }

        public ProbeRequest[] Snapshot()
        {
            lock (gate) { return requests.ToArray(); }
        }

        public void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch (Exception) { }
        }

        private void Loop()
        {
            while (!stopping)
            {
                TcpClient client = null;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[16384];
                StringBuilder text = new StringBuilder();
                int total = 0;
                while (total < buffer.Length)
                {
                    int n = 0;
                    try { n = stream.Read(buffer, total, buffer.Length - total); }
                    catch (Exception) { break; }
                    if (n <= 0) { break; }
                    total += n;
                    string soFar = Txt.Latin1(buffer, total);
                    if (soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) { break; }
                }
                string raw = Txt.Latin1(buffer, total);
                ProbeRequest request = new ProbeRequest();
                request.Raw = raw;
                int lineEnd = raw.IndexOf("\r\n", StringComparison.Ordinal);
                string first = lineEnd > 0 ? raw.Substring(0, lineEnd) : raw;
                string[] parts = first.Split(' ');
                request.Method = parts.Length > 0 ? parts[0] : "";
                request.Target = parts.Length > 1 ? parts[1] : "";
                request.Headers = lineEnd > 0 ? raw.Substring(lineEnd + 2) : "";
                lock (gate) { requests.Add(request); }

                string spec;
                string path = request.Target;
                int q = path.IndexOf('?');
                if (q >= 0) { path = path.Substring(0, q); }
                lock (gate)
                {
                    if (!rules.TryGetValue(path, out spec) && !rules.TryGetValue("*", out spec)) { spec = "status|200|ok"; }
                }
                string response;
                string[] bits = spec.Split(new char[] { '|' }, 3);
                if (bits[0] == "redirect")
                {
                    response = "HTTP/1.1 302 Found\r\nLocation: " + bits[1] + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                }
                else
                {
                    string body = bits.Length > 2 ? bits[2] : "";
                    int code = int.Parse(bits[1]);
                    response = "HTTP/1.1 " + code + " X\r\nContent-Type: text/plain\r\nContent-Length: " + System.Text.Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(response);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception) { }
            finally { try { client.Close(); } catch (Exception) { } }
        }
    }

    public static class Txt
    {
        public static string Latin1(byte[] data, int count)
        {
            return System.Text.Encoding.GetEncoding(28591).GetString(data, 0, count);
        }
    }
}
'@
}

function New-GoldenProbe { Initialize-GoldenProbe; New-Object Golden.HttpProbe 0 }

# ---------------------------------------------------------------- Weir

function New-TempProcessorConnection {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $SubmissionUrl, [string] $HealthUrl = '', [string] $Secret = '', [string] $Header = 'Authorization')
    $body = @{ name = $Name; provider = 'generic'; submissionUrl = $SubmissionUrl; authHeaderName = $Header; secret = $Secret; isEnabled = $true }
    if ($HealthUrl) { $body.healthCheckUrl = $HealthUrl }
    $r = Try-Deluno -Method POST -Path '/api/integrations/processors/connections' -Body $body
    if ($r.Status -ne 200 -or -not $r.Data.id) { throw "The temporary processor connection '$Name' was not made: HTTP $($r.Status) $($r.Raw)" }
    $r.Data
}

function Test-ProcessorConnectionNow {
    param([Parameter(Mandatory)] [string] $Id)
    $r = Try-Deluno -Method POST -Path "/api/integrations/processors/connections/$Id/test" -TimeoutSec 90
    if ($r.Status -ne 200) { throw "The connection test answered HTTP $($r.Status): $($r.Raw)" }
    $r.Data
}

function Remove-TempProcessorConnections {
    param([string[]] $Ids)
    foreach ($id in @($Ids | Where-Object { $_ })) { Try-Deluno -Method DELETE -Path "/api/integrations/processors/connections/$id" | Out-Null }
}

# Health's two findings about the folders Weir watches and writes to, for one library (each is $null when Health has no such row).
function Read-WeirHealthRows {
    param([Parameter(Mandatory)] [string] $WatchId, [Parameter(Mandatory)] [string] $WriteId)
    Try-Deluno -Method POST -Path '/api/system/health/run' | Out-Null
    $h = Try-Deluno -Path '/api/system/health'
    [pscustomobject]@{ Watch = @($h.Data.checks | Where-Object { $_.id -eq $WatchId } | Select-Object -First 1)[0]; Write = @($h.Data.checks | Where-Object { $_.id -eq $WriteId } | Select-Object -First 1)[0] }
}

function Restore-WeirLibrary {
    param([Parameter(Mandatory)] $Library, [string] $Downloads, [string] $Output)
    Try-Deluno -Method PUT -Path "/api/libraries/$($Library.id)" -Body @{ name = $Library.name; rootPath = $Library.rootPath; downloadsPath = $Downloads } | Out-Null
    Try-Deluno -Method PUT -Path "/api/libraries/$($Library.id)/workflow" -Body @{ importWorkflow = $Library.importWorkflow; processorName = $Library.processorName; processorOutputPath = $Output; processorTimeoutMinutes = $Library.processorTimeoutMinutes; processorFailureMode = $Library.processorFailureMode; processorConnectionId = $Library.processorConnectionId } | Out-Null
}

function Invoke-GroupWeirConnection {
    Invoke-Scenario -Id 'weir.test-connection' -Title "The connection test against the real Weir: its health address answering 2xx is healthy; the hand-off address asked with HEAD is reachable on any answer; a status that is not 2xx reads as running but not ready; a closed port is unreachable; a redirect is followed" -Body {
        Connect-Deluno
        $conn = Get-WeirAnswering
        Add-Check 'Deluno holds a connection to the real Weir' ($null -ne $conn) ''
        if (-not $conn) { return }
        $weir = $script:S.WeirUrl
        $probe = New-GoldenProbe
        $made = New-Object System.Collections.ArrayList
        try {
            $hand = $conn.submissionUrl
            $a = New-TempProcessorConnection -Name 'Golden test health' -SubmissionUrl $hand -HealthUrl "$weir/ready" -Secret 'golden-not-the-key'; [void]$made.Add($a.id)
            $ra = Test-ProcessorConnectionNow -Id $a.id
            Add-Check "A health address that answers 2xx is healthy and reachable (Weir's /ready)" ($ra.isReachable -eq $true -and $ra.status -eq 'healthy' -and $ra.statusCode -ge 200 -and $ra.statusCode -lt 300) "status $($ra.status), HTTP $($ra.statusCode): $($ra.message)"

            $b = New-TempProcessorConnection -Name 'Golden test handoff' -SubmissionUrl $hand -Secret 'golden-not-the-key'; [void]$made.Add($b.id)
            $rb = Test-ProcessorConnectionNow -Id $b.id
            Add-Check 'With no health address Deluno asks the hand-off address with HEAD, and any answer from it is not "unreachable"' ($rb.status -ne 'unreachable' -and $null -ne $rb.statusCode) "status $($rb.status), HTTP $($rb.statusCode): $($rb.message)"

            $c = New-TempProcessorConnection -Name 'Golden test not ready' -SubmissionUrl $hand -HealthUrl "$weir/golden-no-such-page" -Secret 'golden-not-the-key'; [void]$made.Add($c.id)
            $rc = Test-ProcessorConnectionNow -Id $c.id
            Add-Check 'A health address answering a status that is not 2xx is running but not ready (degraded, reachable), naming the status' ($rc.isReachable -eq $true -and $rc.status -eq 'degraded' -and $rc.message -match "HTTP $($rc.statusCode)") "status $($rc.status), HTTP $($rc.statusCode): $($rc.message)"

            $d = New-TempProcessorConnection -Name 'Golden test closed' -SubmissionUrl $hand -HealthUrl 'http://127.0.0.1:9/ready' -Secret 'golden-not-the-key'; [void]$made.Add($d.id)
            $rd = Test-ProcessorConnectionNow -Id $d.id
            Add-Check 'A health address nothing answers on is unreachable' ($rd.isReachable -eq $false -and $rd.status -eq 'unreachable') "status $($rd.status): $($rd.message)"

            $probe.Set('/health', "redirect|$weir/ready")
            $e = New-TempProcessorConnection -Name 'Golden test redirect' -SubmissionUrl $hand -HealthUrl "http://127.0.0.1:$($probe.Port)/health" -Secret 'golden-not-the-key'; [void]$made.Add($e.id)
            $re = Test-ProcessorConnectionNow -Id $e.id
            $hits = @($probe.Snapshot() | Where-Object { $_.Target -eq '/health' })
            Add-Check "A health address that redirects to the real Weir's /ready is followed to a healthy answer" ($hits.Count -eq 1 -and $re.status -eq 'healthy') "redirector asked $($hits.Count) time(s); status $($re.status): $($re.message)"
        }
        finally {
            $probe.Stop()
            Remove-TempProcessorConnections -Ids @($made)
            Add-Check 'The temporary connections are gone' (@((Invoke-Deluno -Path '/api/integrations/processors/connections') | Where-Object { $_.name -like 'Golden test *' }).Count -eq 0) ''
        }
    }

    Invoke-Scenario -Id 'weir.credential-own-host' -Title "The credential is sent to the processor's own address only: not to a redirect target on another address, not to an address with another scheme, and a redirect loop ends" -Body {
        Connect-Deluno
        $secret = 'GoldenSecret' + (New-FilmToken)
        $own = New-GoldenProbe      # the processor's own address
        $other = New-GoldenProbe    # a redirect target: another port is another address
        $made = New-Object System.Collections.ArrayList
        try {
            $ownBase = "http://127.0.0.1:$($own.Port)"
            $own.Set('/hook', 'status|405|post only')
            $own.Set('/health', "redirect|http://127.0.0.1:$($other.Port)/landing")
            $other.Set('*', 'status|200|ok')
            $a = New-TempProcessorConnection -Name 'Golden test credential' -SubmissionUrl "$ownBase/hook" -HealthUrl "$ownBase/health" -Secret $secret; [void]$made.Add($a.id)
            $own.Set('/health', "redirect|http://127.0.0.1:$($other.Port)/landing")
            $r = Test-ProcessorConnectionNow -Id $a.id
            $ownHealth = @($own.Snapshot() | Where-Object { $_.Target -eq '/health' })
            $landing = @($other.Snapshot())
            Add-Check "The processor's own address is sent the credential (a Bearer header on its health address)" ($ownHealth.Count -ge 1 -and $ownHealth[-1].Headers -match "(?im)^Authorization:\s*Bearer $([regex]::Escape($secret))") "$($ownHealth.Count) request(s) to the health address"
            Add-Check 'The address the health check is redirected to is asked, and gets no credential in any header' ($landing.Count -ge 1 -and @($landing | Where-Object { $_.Raw -match [regex]::Escape($secret) }).Count -eq 0) "$($landing.Count) request(s) to the redirect target; the answer was '$($r.status)'"

            # A custom header name is not sent to the other address either.
            $other.Set('*', 'status|200|ok')
            $b = New-TempProcessorConnection -Name 'Golden test credential header' -SubmissionUrl "$ownBase/hook" -HealthUrl "$ownBase/health" -Secret $secret -Header 'X-Golden-Key'; [void]$made.Add($b.id)
            $before = @($other.Snapshot()).Count
            Test-ProcessorConnectionNow -Id $b.id | Out-Null
            $after = @($other.Snapshot() | Select-Object -Skip $before)
            Add-Check 'A credential in a header of its own name stays at the processor''s address as well' ($after.Count -ge 1 -and @($after | Where-Object { $_.Raw -match [regex]::Escape($secret) }).Count -eq 0) "$($after.Count) request(s) after"

            # Another scheme: the processor is registered on http, a health address on https at the same port must not be sent the credential.
            $tlsHost = New-GoldenProbe
            $sch = New-TempProcessorConnection -Name 'Golden test scheme' -SubmissionUrl "http://127.0.0.1:$($tlsHost.Port)/hook" -HealthUrl "https://127.0.0.1:$($tlsHost.Port)/health" -Secret $secret; [void]$made.Add($sch.id)
            $before2 = @($tlsHost.Snapshot()).Count
            $rs = Test-ProcessorConnectionNow -Id $sch.id
            $seen = @($tlsHost.Snapshot() | Select-Object -Skip 0)
            # The registration itself asks the processor's own address for its capabilities with the credential (that is its own host); nothing else carries it.
            $leak = @($seen | Where-Object { $_.Raw -match [regex]::Escape($secret) -and $_.Target -notmatch 'capabilities' })
            Add-Check 'A health address on another scheme (https where the processor is registered on http) is not sent the credential, and the test reads unreachable' ($leak.Count -eq 0 -and $rs.status -eq 'unreachable') "status $($rs.status); requests carrying the credential outside the capabilities read: $($leak.Count)"
            $tlsHost.Stop()

            # A redirect loop ends.
            $own.Set('/loop', "redirect|$ownBase/loop")
            $c = New-TempProcessorConnection -Name 'Golden test loop' -SubmissionUrl "$ownBase/hook" -HealthUrl "$ownBase/loop" -Secret $secret; [void]$made.Add($c.id)
            $w = [Diagnostics.Stopwatch]::StartNew()
            $rl = Test-ProcessorConnectionNow -Id $c.id
            $w.Stop()
            $loops = @($own.Snapshot() | Where-Object { $_.Target -eq '/loop' })
            Add-Check 'A redirect loop ends: the test answers within seconds after a handful of hops, not forever' ($w.Elapsed.TotalSeconds -lt 60 -and $loops.Count -ge 2 -and $loops.Count -le 8 -and $null -ne $rl.status) "$($loops.Count) hop(s) in $([math]::Round($w.Elapsed.TotalSeconds,1)) s; status $($rl.status): $($rl.message)"
        }
        finally {
            $own.Stop(); $other.Stop()
            Remove-TempProcessorConnections -Ids @($made)
            Add-Check 'The temporary connections are gone' (@((Invoke-Deluno -Path '/api/integrations/processors/connections') | Where-Object { $_.name -like 'Golden test *' }).Count -eq 0) ''
        }
    }

    Invoke-Scenario -Id 'weir.health-findings' -Title "Health reads what the real Weir watches and writes to against the library's downloads and ready folders: both agree on a set-up install, and a changed folder raises a finding that names both" -Body {
        Connect-Deluno
        $conn = Get-WeirAnswering
        $lib = Get-Library -MediaType 'movies'
        Add-Check 'There is a connection to Weir and a Movies library that cleans with it before importing' ($conn -and $lib -and $lib.importWorkflow -eq 'refine-before-import') "$($lib.importWorkflow)"
        if (-not ($conn -and $lib)) { return }
        $watchId = "processor:$($conn.id):watching:$($lib.id)"
        $writeId = "processor:$($conn.id):writing:$($lib.id)"
        $origDownloads = $lib.downloadsPath
        $origOutput = $lib.processorOutputPath
        $stray = 'C:\Downloads\GoldenWeirMismatch'
        $strayReady = 'C:\Weir\GoldenReadyMismatch'
        New-Item -ItemType Directory -Force -Path $stray, $strayReady | Out-Null
        try {
            $ok = Wait-Until -TimeoutSec 240 -IntervalSec 8 -Condition { $x = Read-WeirHealthRows -WatchId $watchId -WriteId $writeId; if ($x.Watch -and $x.Watch.status -eq 'ok') { $x } else { $null } }
            $now = if ($ok.Ok) { $ok.Value } else { Read-WeirHealthRows -WatchId $watchId -WriteId $writeId }
            Save-Evidence -Name 'weir-health-agree.json' -Text ($now | ConvertTo-Json -Depth 5)
            Add-Check "On a set-up install the finding for the folder Weir watches reads ok (Weir's watched folder is the library's downloads folder)" ($ok.Ok) $(if ($now.Watch) { "$($now.Watch.status): $($now.Watch.message)" } else { 'no row' })
            Add-Check "The finding for the folder Weir writes to reads ok when Weir reports one (it is the library's ready folder)" ($null -eq $now.Write -or $now.Write.status -eq 'ok') $(if ($now.Write) { "$($now.Write.status): $($now.Write.message)" } else { 'Weir reported no output folder for this library' })

            Try-Deluno -Method PUT -Path "/api/libraries/$($lib.id)" -Body @{ name = $lib.name; rootPath = $lib.rootPath; downloadsPath = $stray } | Out-Null
            $bad = Wait-Until -TimeoutSec 240 -IntervalSec 8 -Condition { $x = Read-WeirHealthRows -WatchId $watchId -WriteId $writeId; if ($x.Watch -and $x.Watch.status -eq 'problem') { $x } else { $null } }
            Save-Evidence -Name 'weir-health-watch-mismatch.json' -Text ($(if ($bad.Ok) { $bad.Value } else { Read-WeirHealthRows -WatchId $watchId -WriteId $writeId }) | ConvertTo-Json -Depth 5)
            Add-Check "With the library's downloads folder changed, Health raises a finding that names the folder Weir watches and the library's folder" ($bad.Ok -and $bad.Value.Watch.message -like "*$stray*" -and $bad.Value.Watch.message -match 'watching') $(if ($bad.Ok) { [string]$bad.Value.Watch.message } else { 'no problem raised in 4 minutes' })
            if ($bad.Ok) { Add-Check 'The finding says how to fix it (point Weir at the library''s folder, or change the library''s folder to match)' (([string]$bad.Value.Watch.fix) -match 'Point|update') ([string]$bad.Value.Watch.fix) }
            Try-Deluno -Method PUT -Path "/api/libraries/$($lib.id)" -Body @{ name = $lib.name; rootPath = $lib.rootPath; downloadsPath = $origDownloads } | Out-Null

            if ($now.Write) {
                Try-Deluno -Method PUT -Path "/api/libraries/$($lib.id)/workflow" -Body @{ importWorkflow = $lib.importWorkflow; processorName = $lib.processorName; processorOutputPath = $strayReady; processorTimeoutMinutes = $lib.processorTimeoutMinutes; processorFailureMode = $lib.processorFailureMode; processorConnectionId = $lib.processorConnectionId } | Out-Null
                $badOut = Wait-Until -TimeoutSec 240 -IntervalSec 8 -Condition { $x = Read-WeirHealthRows -WatchId $watchId -WriteId $writeId; if ($x.Write -and $x.Write.status -eq 'problem') { $x } else { $null } }
                Add-Check "With the library's ready folder changed, Health raises a finding that names the folder Weir writes to and the library's ready folder" ($badOut.Ok -and $badOut.Value.Write.message -like "*$strayReady*") $(if ($badOut.Ok) { [string]$badOut.Value.Write.message } else { 'no problem raised in 4 minutes' })
            }
        }
        finally {
            Restore-WeirLibrary -Library $lib -Downloads $origDownloads -Output $origOutput
            Remove-Item -LiteralPath $stray, $strayReady -Force -Recurse -ErrorAction SilentlyContinue
            $back = Wait-Until -TimeoutSec 240 -IntervalSec 8 -Condition { $x = Read-WeirHealthRows -WatchId $watchId -WriteId $writeId; if ($x.Watch -and $x.Watch.status -eq 'ok') { $x } else { $null } }
            Add-Check 'Put back, the finding clears and the library''s folders are as found' ($back.Ok -and (Get-Library -MediaType 'movies').downloadsPath -eq $origDownloads -and (Get-Library -MediaType 'movies').processorOutputPath -eq $origOutput) ''
        }
    }
}

# ---------------------------------------------------------------- the ways Deluno is started (Windows service modes)

# Opens the tray's Startup Mode dialog the way a person does (Advanced > Run as a Windows service...) and returns its window, or $null.
function Show-StartupModeDialog { Open-StartupModeDialog }

function Get-SignInEntryValue {
    $p = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
    if ($p -and $p.PSObject.Properties.Name -contains 'Deluno') { [string]$p.Deluno } else { $null }
}

function Invoke-GroupServiceModes {
    Invoke-Scenario -Id 'service.mode-switching' -Title "Choosing how Deluno starts removes the old choice first: Windows service (Local System), a named account, the tray's own mode (service gone, sign-in entry set) and --uninstall-service (service gone, sign-in entry cleared)" -Body {
        Connect-Deluno
        Remove-DelunoServiceNow
        $exe = Get-DelunoExe
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $hadEntry = [bool](Get-SignInEntryValue)
        $account = 'goldensvc2'
        $pw = 'Gp-' + (New-FilmToken).Replace('-', '') + '-9x'
        & net.exe user $account $pw /add | Out-Null
        try {
            # ---- the sign-in entry is set, then a service is chosen: the entry goes first, the service is made
            Set-ItemProperty -Path $runKey -Name 'Deluno' -Value ('"{0}"' -f $exe.FullName)
            Add-Check 'Before: the sign-in entry is set (the tray''s own mode)' ([bool](Get-SignInEntryValue)) ''
            & $exe.FullName --install-service | Out-Null
            $made = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { Get-DelunoServiceFacts }
            Add-Check 'Local System: the service exists, starts automatically, runs as LocalSystem, and the sign-in entry was removed first' ($made.Ok -and $made.Value.StartMode -eq 'Auto' -and $made.Value.Account -eq 'LocalSystem' -and -not (Get-SignInEntryValue)) $(if ($made.Ok) { "$($made.Value.StartMode) / $($made.Value.Account); entry: $(Get-SignInEntryValue)" } else { 'no service' })

            # ---- a second choice replaces the first: the old service is removed, a new one is made for the named account
            $idBefore = (Get-CimInstance Win32_Service -Filter "Name='Deluno'").ProcessId
            & $exe.FullName --install-service --username ".\$account" --password $pw | Out-Null
            $named = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { $f = Get-DelunoServiceFacts; if ($f -and $f.Account -match $account) { $f } else { $null } }
            Add-Check 'Run as user: the existing service is removed and a new one logs on as the account that was named (and the sign-in entry stays off)' ($named.Ok -and -not (Get-SignInEntryValue)) $(if ($named.Ok) { "$($named.Value.Account) / $($named.Value.StartMode)" } else { "account now: $((Get-DelunoServiceFacts).Account)" })
            Add-Check 'There is one service, not two' (@(Get-CimInstance Win32_Service -Filter "Name='Deluno'").Count -eq 1) ''

            # ---- the tray's own mode, chosen in the dialog: the service goes and the sign-in entry is set
            $dlg = Show-StartupModeDialog
            Add-Check 'The "Startup Mode" dialog opens' ($null -ne $dlg) $(if ($dlg) { '' } else { Format-DesktopWindows })
            if ($dlg) {
                $trayProc = Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Select-Object -First 1
                $rb = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'RadioButton' -and $_.Name -match 'Start at login' } | Select-Object -First 1
                $rb.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Click-WindowButton -Window $dlg -NamePattern '^Apply$' | Out-Null
                $gone = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition { -not (Get-DelunoServiceFacts) }
                $left = Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { -not ($trayProc -and (Get-Process -Id $trayProc.Id -ErrorAction SilentlyContinue)) }
                $entry = Get-SignInEntryValue
                Add-Check 'Start at login: the service is removed and the sign-in entry is set, holding the quoted exe and no flags' ($gone.Ok -and $entry -and $entry -match '^"[^"]+\\Deluno\.exe"$') "service gone: $($gone.Ok); entry: $(if ($entry) { $entry -replace [regex]::Escape($env:LOCALAPPDATA), '%LOCALAPPDATA%' } else { 'none' })"
                Add-Check 'The tray hands over (the process that showed the dialog ends)' $left.Ok ''
            }
            Restore-DelunoTray | Out-Null

            # ---- and --uninstall-service from a service: it is removed and the sign-in entry is cleared
            & $exe.FullName --install-service | Out-Null
            Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { Get-DelunoServiceFacts } | Out-Null
            Set-ItemProperty -Path $runKey -Name 'Deluno' -Value ('"{0}"' -f $exe.FullName)
            & $exe.FullName --uninstall-service | Out-Null
            $removed = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { -not (Get-DelunoServiceFacts) }
            Add-Check '--uninstall-service removes the service and clears the sign-in entry' ($removed.Ok -and -not (Get-SignInEntryValue)) "service gone: $($removed.Ok); entry: $(Get-SignInEntryValue)"
        }
        finally {
            Remove-DelunoServiceNow
            & net.exe user $account /delete | Out-Null
            if ($hadEntry) { Set-ItemProperty -Path $runKey -Name 'Deluno' -Value ('"{0}"' -f $exe.FullName) } else { Remove-ItemProperty -Path $runKey -Name 'Deluno' -ErrorAction SilentlyContinue }
            Add-Check 'The account and the service are gone, the sign-in entry is as found, and the tray''s Deluno answers' (-not (Get-DelunoServiceFacts) -and ([bool](Get-SignInEntryValue) -eq $hadEntry) -and (Restore-DelunoTray)) ''
        }
    }
}

# ---------------------------------------------------------------- the tray's picker window

# The Companion Tools page asks the tray to install NZBGet; returns the answer and the picker window that opens (or $null).
function Open-PickerByAsk {
    param([Parameter(Mandatory)] [string] $Pending, [Parameter(Mandatory)] [string] $Title)
    Remove-Item -LiteralPath $Pending -ErrorAction SilentlyContinue
    $ask = Try-Deluno -Method POST -Path '/api/companion-tools/tray-install' -Body @{ toolIds = @('nzbget') }
    $dlg = Wait-DesktopWindow -NamePattern $Title -Process 'Deluno' -TimeoutSec 90
    [pscustomobject]@{ Ask = $ask; Window = $dlg }
}

function Invoke-GroupPickerWindow {
    Invoke-Scenario -Id 'picker.window-controls' -Title "The tray's picker window: every component is listed with its licence, an app already installed is marked and cannot be ticked, View licences is offered, and Install with nothing ticked closes with nothing installed" -Body {
        Connect-Deluno
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $pending = Join-Path $dataRoot 'config\pending-component-installs.json'
        $hadPending = Test-Path $pending
        $pendingBefore = if ($hadPending) { Get-Content -Raw $pending } else { $null }
        $installedBefore = @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'qBittorrent|Transmission|Deluge|SABnzbd|NZBGet|Weir' } | ForEach-Object { $_.DisplayName } | Sort-Object)
        $title = '^Set up download clients and Weir$'
        try {
            $o = Open-PickerByAsk -Pending $pending -Title $title
            Add-Check 'The picker window opens when the Companion Tools page asks for an app (HTTP 202)' ($o.Ask.Status -eq 202 -and $null -ne $o.Window) $(if ($o.Window) { "HTTP $($o.Ask.Status)" } else { 'windows: ' + (Format-DesktopWindows) })
            $dlg = $o.Window
            if (-not $dlg) { return }
            $controls = @(Get-WindowControls -Window $dlg)
            $text = Get-WindowText -Window $dlg
            Save-Evidence -Name 'picker-window-controls.txt' -Text (($controls | ForEach-Object { "$($_.Type): $($_.Name)" }) -join "`n")
            $rows = @($controls | Where-Object { $_.Type -eq 'CheckBox' -and $_.Name -match '\(' })
            $names = 'qBittorrent', 'Transmission', 'Deluge', 'SABnzbd', 'NZBGet', 'Weir'
            $missing = @($names | Where-Object { $n = $_; -not ($rows | Where-Object { $_.Name -like "$n*" }) })
            Add-Check 'Every component is listed, each with its licence in brackets' ($missing.Count -eq 0 -and @($rows | Where-Object { $_.Name -notmatch '\([^)]+\)' }).Count -eq 0) $(if ($missing.Count) { 'missing: ' + ($missing -join ', ') } else { ($rows | ForEach-Object { $_.Name }) -join ' | ' })
            $installedNames = @($names | Where-Object { $n = $_; $installedBefore | Where-Object { $_ -like "*$n*" } })
            $marked = @($rows | Where-Object { $_.Name -match 'already installed, will not be touched' })
            Add-Check 'An app already on this PC is marked "already installed, will not be touched"' ($installedNames.Count -eq 0 -or $marked.Count -ge 1) "$($marked.Count) marked; installed here: $($installedNames -join ', ')"
            Add-Check 'The window says why things are ticked (an ask, not first run) and carries View licences, Install selected and Skip' ($text -match 'ticked because you asked' -and @($controls | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'View licences' }).Count -eq 1 -and @($controls | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Install selected' }).Count -eq 1 -and @($controls | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Skip' }).Count -eq 1) ($text -replace '\s+', ' ')

            # An app already installed cannot be ticked: pressing its box leaves it unticked.
            $box = $rows | Where-Object { $_.Name -match 'already installed' } | Select-Object -First 1
            if ($box) {
                try { $box.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { }
                Start-Sleep -Milliseconds 500 # reason: UI settle after input; Windows raises no event for it
                $after = @(Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'CheckBox' -and $_.Name -eq $box.Name } | Select-Object -First 1)[0]
                $state = try { $after.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { 'unknown' }
                Add-Check 'Pressing the box of an app that is already installed leaves it unticked' ("$state" -eq 'Off') "toggle state $state"
            }

            # View licences is offered and enabled. (Its message is not opened here: pressing a button that opens a modal message through UI Automation
            # hung the run in the VM on 3 Nov; the licences the message shows are the ones listed in brackets on every row, read above.)
            $licButton = $controls | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'View licences' } | Select-Object -First 1
            Add-Check 'View licences is offered and enabled' ($licButton -and $licButton.Enabled) ''

            # Install with nothing ticked closes with nothing installed
            Click-WindowButton -Window $dlg -NamePattern '^Install selected$' | Out-Null
            $closed = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 1) }
            Add-Check 'Install selected with nothing ticked closes the window (no progress, no install)' $closed.Ok ''
            $installedAfter = @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'qBittorrent|Transmission|Deluge|SABnzbd|NZBGet|Weir' } | ForEach-Object { $_.DisplayName } | Sort-Object)
            Add-Check 'Nothing was installed or changed' ((($installedBefore -join '|') -eq ($installedAfter -join '|'))) ($installedAfter -join ', ')

            # Skip closes it too, and the ask is not repeated on its own
            $o2 = Open-PickerByAsk -Pending $pending -Title $title
            if ($o2.Window) {
                Click-WindowButton -Window $o2.Window -NamePattern '^Skip$' | Out-Null
                $closed2 = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 1) }
                Add-Check 'Skip closes the window, and it does not open again by itself' ($closed2.Ok -and -not (Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 15)) ''
            }
            else { Add-Check 'Skip closes the window' $false 'the window did not open the second time' }
        }
        finally {
            foreach ($w in @(Get-DesktopWindows | Where-Object { $_.Process -eq 'Deluno' -and $_.Name -match 'Set up download clients|Third-party licences' })) { Close-DesktopWindow -Window $w }
            if ($hadPending) { Set-Content -LiteralPath $pending -Value $pendingBefore -Encoding utf8 } else { Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue }
        }
    }
}

# ---------------------------------------------------------------- the tray's Change port dialog

function Open-PortDialog {
    $dlg = $null
    for ($try = 1; $try -le 3 -and -not $dlg; $try++) {
        Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Change port' -Modal | Out-Null
        $dlg = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 15
        if (-not $dlg) { Close-TrayMenu }
    }
    $dlg
}

function Get-PortDialogFacts {
    param([Parameter(Mandatory)] $Window)
    $c = @(Get-WindowControls -Window $Window)
    $edit = $c | Where-Object { $_.Type -eq 'Edit' } | Select-Object -First 1
    $value = if ($edit) { try { $edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' } } else { $null }
    [pscustomobject]@{
        Controls = $c
        Default = $c | Where-Object { $_.Type -eq 'RadioButton' -and $_.Name -match "^Use Deluno's default port" } | Select-Object -First 1
        Custom = $c | Where-Object { $_.Type -eq 'RadioButton' -and $_.Name -match '^Use this port' } | Select-Object -First 1
        Edit = $edit; EditValue = $value
        Address = ($c | Where-Object { $_.Name -match '^Deluno will be at ' } | Select-Object -First 1).Name
        Buttons = @($c | Where-Object { $_.Type -eq 'Button' } | ForEach-Object { $_.Name })
        Text = Get-WindowText -Window $Window
    }
}

# Types into the dialog's port box the way a person does (so the box's own limit applies) and returns what it then holds.
function Send-PortBoxKeys {
    param([Parameter(Mandatory)] $Window, [Parameter(Mandatory)] [string] $Keys)
    $f = Get-PortDialogFacts -Window $Window
    $f.Edit.Element.SetFocus()
    Start-Sleep -Milliseconds 300 # reason: UI settle after input; Windows raises no event for it
    (New-Object -ComObject WScript.Shell).SendKeys('^a')
    Start-Sleep -Milliseconds 100 # reason: UI settle after input; Windows raises no event for it
    (New-Object -ComObject WScript.Shell).SendKeys($Keys)
    Start-Sleep -Milliseconds 400 # reason: UI settle after input; Windows raises no event for it
    (Get-PortDialogFacts -Window $Window).EditValue
}

function Set-DelunoPortThroughDialog {
    param([Parameter(Mandatory)] [int] $To, [switch] $UseDefaultOption)
    $dlg = Open-PortDialog
    if (-not $dlg) { return $false }
    $f = Get-PortDialogFacts -Window $dlg
    if ($UseDefaultOption) { $f.Default.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    else { $f.Edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([string]$To) }
    Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
    $script:S.DelunoUrl = "http://localhost:$To"
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
    if ($up.Ok) { Connect-Deluno; $null = Wait-TrayDot -Match { param($d) $d.Hover -match '^Deluno - Running' } -TimeoutSec 120 }
    $up.Ok
}

function Invoke-GroupTrayPort {
    Invoke-Scenario -Id 'tray.port-dialog' -Title "The Change port dialog's controls: the default-port option (taken by another program: greyed and said so), the port box limited to five characters, the address it will use, an error in words for an empty or non-numeric port, and Cancel changes nothing" -Body {
        Connect-Deluno
        $old = Get-LanPort
        $mid = $old + 21
        $listener = $null
        try {
            $dlg = Open-PortDialog
            Add-Check 'Change port... opens the dialog' ($null -ne $dlg) $(if ($dlg) { '' } else { Format-DesktopWindows })
            if (-not $dlg) { return }
            $f = Get-PortDialogFacts -Window $dlg
            Save-Evidence -Name 'port-dialog-controls.txt' -Text ($f.Text)
            Add-Check "On the default port the dialog says where Deluno is, offers Use Deluno's default port (7879) and Use this port:, a Restart on this port button and Cancel" ($f.Text -match "Deluno is at port $old now" -and $f.Default -and $f.Default.Name -eq "Use Deluno's default port, 7879" -and $f.Custom -and ($f.Buttons -contains 'Restart on this port') -and ($f.Buttons -contains 'Cancel')) $f.Text
            Add-Check 'The address it will use is shown ("Deluno will be at http://localhost:7879/")' ($f.Address -eq 'Deluno will be at http://localhost:7879/') "$($f.Address)"

            # The box takes five characters, however many are typed.
            $typed = Send-PortBoxKeys -Window $dlg -Keys '1234567'
            Add-Check 'The port box takes five characters at most (1234567 typed)' ($typed -eq '12345') "the box holds '$typed'"
            $f2 = Get-PortDialogFacts -Window $dlg
            Add-Check 'The address follows what is typed, and is a placeholder while the number is not a port' ($f2.Address -eq 'Deluno will be at http://localhost:<port>/' -or $f2.Address -match 'http://localhost:12345/') "$($f2.Address)"

            # An empty box and letters are refused in words, and the dialog stays open.
            $null = Send-PortBoxKeys -Window $dlg -Keys '{BACKSPACE}'
            $empty = Get-PortDialogFacts -Window $dlg
            $empty.Edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
            Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
            $e1 = Wait-Until -TimeoutSec 10 -IntervalSec 0.4 -Condition { (Get-WindowText -Window $dlg) -match 'Enter a port number' }
            Add-Check 'An empty port box is refused: "Enter a port number."' $e1.Ok (Get-WindowText -Window $dlg)
            $null = Send-PortBoxKeys -Window $dlg -Keys 'abc'
            Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
            $e2 = Wait-Until -TimeoutSec 10 -IntervalSec 0.4 -Condition { (Get-WindowText -Window $dlg) -match 'A port is a whole number from 1 to 65535' }
            Add-Check 'Letters are refused: "A port is a whole number from 1 to 65535."' $e2.Ok (Get-WindowText -Window $dlg)
            $null = Send-PortBoxKeys -Window $dlg -Keys '70000'
            Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
            $e3 = Wait-Until -TimeoutSec 10 -IntervalSec 0.4 -Condition { (Get-WindowText -Window $dlg) -match 'A port is a whole number from 1 to 65535' }
            Add-Check 'A number above 65535 is refused in the same words' $e3.Ok ''
            $null = Send-PortBoxKeys -Window $dlg -Keys '8'
            Add-Check 'Typing again clears the error (it is not left on screen)' ((Get-WindowText -Window $dlg) -notmatch 'A port is a whole number') ''
            Click-WindowButton -Window $dlg -NamePattern '^Cancel$' | Out-Null
            $closed = Wait-Until -TimeoutSec 10 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 1) }
            Add-Check 'Cancel closes the dialog and changes nothing: Deluno is on the same port and the port saved is the same' ($closed.Ok -and (Test-DelunoAnswering) -and (Get-LanPort) -eq $old) ''

            # With Deluno on another port, and 7879 taken by another program, the default option is greyed and says why.
            $moved = Set-DelunoPortThroughDialog -To $mid
            Add-Check "Deluno is moved to port $mid to set that up" $moved ''
            if (-not $moved) { return }
            $listener = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Any, 7879); $listener.Start()
            $dlg2 = Open-PortDialog
            if ($dlg2) {
                $g = Get-PortDialogFacts -Window $dlg2
                Add-Check 'With 7879 held by another program the default option is greyed and reads "in use by another program"' ($g.Default -and -not $g.Default.Enabled -and $g.Default.Name -match 'in use by another program') "$($g.Default.Name) enabled=$($g.Default.Enabled)"
                Add-Check 'The box shows Deluno''s current port and the other option is the one chosen' ($g.EditValue -eq [string]$mid -and $g.Address -eq "Deluno will be at http://localhost:$mid/") "box '$($g.EditValue)' / $($g.Address)"
                Click-WindowButton -Window $dlg2 -NamePattern '^Cancel$' | Out-Null
            }
            else { Add-Check 'The dialog opens on the other port' $false (Format-DesktopWindows) }
            $listener.Stop(); $listener = $null
            # Back to the default through the dialog's own option (the box is not used).
            $back = Set-DelunoPortThroughDialog -To $old -UseDefaultOption
            Add-Check "The default-port option puts Deluno back on $old" ($back -and (Get-LanPort) -eq $old) ''
        }
        finally {
            if ($listener) { $listener.Stop() }
            foreach ($w in @(Get-DesktopWindows | Where-Object { $_.Process -eq 'Deluno' -and $_.Name -eq 'Deluno' -and $_.Class -match 'WindowsForms' })) { Close-DesktopWindow -Window $w }
            if ((Get-LanPort) -ne $old -or $script:S.DelunoUrl -ne "http://localhost:$old") {
                try { if (-not (Set-DelunoPortThroughDialog -To $old -UseDefaultOption)) { $script:S.DelunoUrl = "http://localhost:$old" } } catch { $script:S.DelunoUrl = "http://localhost:$old" }
            }
            Remove-InboundRulesFor -Port $mid
        }
    }

    Invoke-Scenario -Id 'tray.port-override-cleared' -Title "--port N serves one run only and a port change clears it: after Change port, a plain start is on the port that was chosen, not on the --port one" -Body {
        Connect-Deluno
        $port = Get-LanPort
        $flag = $port + 31
        $chosen = $port + 32
        try {
            Quit-DelunoFromTray | Out-Null
            Start-DelunoTrayNow -Arguments "--port $flag"
            $alt = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { try { (Invoke-WebRequest -Uri "http://localhost:$flag/api/auth/bootstrap-status" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $false } }
            Add-Check "Started with --port $flag, Deluno answers there" $alt.Ok ''
            $script:S.DelunoUrl = "http://localhost:$flag"
            Connect-Deluno
            $null = Wait-TrayDot -Match { param($d) $d.Hover -match '^Deluno - Running' } -TimeoutSec 120
            Start-Sleep -Seconds 20 # reason: the "Deluno is running" balloon of this start sits over the tray menu for a while, and a click on the menu would land on it
            $dlg = Open-PortDialog
            $f = if ($dlg) { Get-PortDialogFacts -Window $dlg } else { $null }
            Add-Check "The dialog says Deluno is at port $flag (the port of this run), not the saved one" ($f -and $f.Text -match "Deluno is at port $flag now") $(if ($f) { $f.Text } else { 'no dialog; ' + $script:TrayMenuMiss + ' || ' + (Format-DesktopWindows) })
            if ($dlg) {
                $f.Edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([string]$chosen)
                Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
                $script:S.DelunoUrl = "http://localhost:$chosen"
                $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
                Add-Check "After the change Deluno answers on $chosen and no longer on $flag" ($up.Ok -and (Get-StatusWithHostHeader -HostHeader 'localhost' -Port $flag) -eq 0) ''
                $saved = if (Test-Path (Get-TrayAppSettingsPath)) { Get-Content -Raw (Get-TrayAppSettingsPath) } else { '' }
                Add-Check "The port chosen ($chosen) is saved and the --port one ($flag) is not" ($saved -match "\b$chosen\b" -and $saved -notmatch "\b$flag\b") ''
            }
            # A plain start after Quit: the override is gone, the chosen port is used.
            Quit-DelunoFromTray | Out-Null
            Start-DelunoTrayNow
            $plain = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { try { (Invoke-WebRequest -Uri "http://localhost:$chosen/api/auth/bootstrap-status" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $false } }
            Add-Check "A plain start is on the port that was chosen ($chosen), not on the --port one" ($plain.Ok -and (Get-StatusWithHostHeader -HostHeader 'localhost' -Port $flag) -eq 0) ''
            $script:S.DelunoUrl = "http://localhost:$chosen"
            try { Connect-Deluno } catch { Add-Note "Deluno does not answer on the chosen port ($chosen): $($_.Exception.Message)" }
            $null = Wait-TrayDot -Match { param($d) $d.Hover -match '^Deluno - Running' } -TimeoutSec 120
            Start-Sleep -Seconds 20 # reason: the balloon of this start sits over the tray menu for a while
            $back = Set-DelunoPortThroughDialog -To $port -UseDefaultOption
            Add-Check "Back on the default port ($port) through the dialog" ($back -and (Get-LanPort) -eq $port) ''
        }
        finally {
            if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) { Start-DelunoTrayNow }
            $saved = if (Test-Path (Get-TrayAppSettingsPath)) { Get-Content -Raw (Get-TrayAppSettingsPath) } else { '' }
            $script:S.DelunoUrl = "http://localhost:$port"
            if ($saved -match "\b$chosen\b" -or $saved -match "\b$flag\b") {
                $script:S.DelunoUrl = "http://localhost:$chosen"
                try { $null = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }; Connect-Deluno; if (-not (Set-DelunoPortThroughDialog -To $port -UseDefaultOption)) { $script:S.DelunoUrl = "http://localhost:$port" } } catch { $script:S.DelunoUrl = "http://localhost:$port" }
            }
            Remove-InboundRulesFor -Port $flag
            Remove-InboundRulesFor -Port $chosen
            $null = Restore-DelunoTray
        }
    }
}

# ---------------------------------------------------------------- the tray's start-up balloons

# The body lines of every balloon with this title that Windows has kept (its notification database holds each balloon's XML with the title
# and the text in it), in the order found. The database is read as bytes, in both of the encodings it uses.
function Get-ToastBodies {
    param([Parameter(Mandatory)] [string] $Title)
    $dir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Notifications'
    $bodies = New-Object System.Collections.ArrayList
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Force -File -Filter 'wpndatabase.db*' -ErrorAction SilentlyContinue)) {
        try {
            $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            try { $b = New-Object byte[] $fs.Length; [void]$fs.Read($b, 0, $b.Length) } finally { $fs.Close() }
            foreach ($enc in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
                $t = $enc.GetString($b)
                foreach ($m in [regex]::Matches($t, [regex]::Escape($Title) + '</text>\s*<text[^>]*>([^<]*)<')) { [void]$bodies.Add($m.Groups[1].Value) }
            }
        }
        catch { }
    }
    @($bodies)
}

# The balloon texts of this title that are new since $Before (an earlier Get-ToastBodies list). Windows' database is not in time order, so the
# last entry is not the newest: the new ones are the texts that now occur more often than they did before.
function Get-NewToastBodies {
    param([Parameter(Mandatory)] [string] $Title, [AllowEmptyCollection()] [object[]] $Before = @())
    $old = @{}
    foreach ($t in @($Before)) { $old["$t"] = 1 + [int]$old["$t"] }
    $now = @{}
    $fresh = New-Object System.Collections.ArrayList
    foreach ($t in @(Get-ToastBodies -Title $Title)) {
        $now["$t"] = 1 + [int]$now["$t"]
        if ($now["$t"] -gt [int]$old["$t"]) { [void]$fresh.Add("$t") }
    }
    @($fresh)
}

# The addresses Deluno is reachable at, worked out from the machine's own network adapters: localhost, then each adapter that is up, is
# not the loopback or a tunnel, with each of its IPv4 addresses that is not a 169.254.x.x self-assigned one.
function Get-ExpectedReachableUrls {
    param([Parameter(Mandatory)] [int] $Port)
    $urls = New-Object System.Collections.ArrayList
    [void]$urls.Add("http://localhost:$Port/")
    $seen = New-Object System.Collections.ArrayList
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        $kind = "$($nic.NetworkInterfaceType)"
        [void]$seen.Add("$($nic.Name) [$kind, $($nic.OperationalStatus)]: " + ((@($nic.GetIPProperties().UnicastAddresses | ForEach-Object { "$($_.Address)" })) -join ', '))
        if ("$($nic.OperationalStatus)" -ne 'Up') { continue }
        if ($kind -in 'Loopback', 'Tunnel') { continue }
        foreach ($ua in $nic.GetIPProperties().UnicastAddresses) {
            if ($ua.Address.AddressFamily -ne 'InterNetwork') { continue }
            $bytes = $ua.Address.GetAddressBytes()
            if ($bytes[0] -eq 127) { continue }
            if ($bytes[0] -eq 169 -and $bytes[1] -eq 254) { continue }
            [void]$urls.Add("http://$($ua.Address):$Port/")
        }
    }
    [pscustomobject]@{ Urls = @($urls); Adapters = @($seen) }
}

function Invoke-GroupTrayBalloons {
    Invoke-Scenario -Id 'tray.running-balloons' -Title "The start-up balloons: Deluno is running (Local only, or the first LAN address and how many more; the warning that Windows Firewall blocks other devices when it does), and the running copy's Deluno is already running with the address, or that it is still starting" -Body {
        Connect-Deluno
        $port = Get-LanPort
        $expected = Get-ExpectedReachableUrls -Port $port
        Add-Note ("Adapters on this machine: " + ($expected.Adapters -join ' | '))
        $lan = @($expected.Urls | Where-Object { $_ -notmatch 'localhost' })
        $wantBody = if ($lan.Count -eq 0) { "Local only: $($expected.Urls[0])" } else { "LAN: $($lan[0])" + $(if ($lan.Count -gt 1) { " (+$($lan.Count - 1) more)" } else { '' }) }
        $ruleBack = $false
        try {
            # ---- a start after Quit, with Windows Firewall letting other devices in
            Quit-DelunoFromTray | Out-Null
            $beforeList = @(Get-ToastBodies -Title 'Deluno is running')
            $before = $beforeList.Count
            Start-DelunoTrayNow
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check 'Deluno starts again after Quit' $up.Ok ''
            $shown = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { @(Get-ToastBodies -Title 'Deluno is running').Count -gt $before }
            $bodies = @(Get-NewToastBodies -Title 'Deluno is running' -Before $beforeList)
            $last = (@($bodies | Where-Object { $_ -like "$wantBody*" }) + $bodies | Select-Object -First 1)
            Add-Check "The balloon lists the address Deluno is reachable at: '$wantBody'" ($shown.Ok -and $last -like "$wantBody*" -and $last -notmatch 'Firewall') "new balloon text: '$($bodies -join ' | ')'"
            Connect-Deluno

            # ---- with Windows Firewall blocking other devices, the balloon adds the warning (only when there is a LAN address to block)
            if ($lan.Count -gt 0) {
                $rules = @(Get-InboundRulesFor -Port $port | Where-Object { $_.Name -like 'Deluno*' })
                Remove-InboundRulesFor -Port $port
                $blocked = Wait-LanState -State 'blocked'
                Add-Check 'With every rule for the port removed Deluno reads the port as blocked' ($null -ne $blocked) ($blocked | ConvertTo-Json -Compress)
                Quit-DelunoFromTray | Out-Null
                $beforeList2 = @(Get-ToastBodies -Title 'Deluno is running')
                $before2 = $beforeList2.Count
                Start-DelunoTrayNow
                $up2 = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
                $shown2 = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { @(Get-ToastBodies -Title 'Deluno is running').Count -gt $before2 }
                $new2 = @(Get-NewToastBodies -Title 'Deluno is running' -Before $beforeList2)
                $last2 = (@($new2 | Where-Object { $_ -like "$wantBody*" }) + $new2 | Select-Object -First 1)
                Add-Check 'With Windows Firewall blocking other devices the balloon adds: "Windows Firewall blocks other devices. Click here to allow them."' ($up2.Ok -and $shown2.Ok -and "$last2" -like "$wantBody*Windows Firewall blocks other devices. Click here to allow them.") "new balloon text: '$($new2 -join ' | ')'"
                Connect-Deluno
            }
            else { Add-Note 'This machine has no LAN address, so the firewall-blocked wording of the balloon could not be seen.' }

            # ---- a second launch while Deluno runs: the running copy shows the address
            Add-OwnLanRuleIfMissing -Port $port
            $exe = Get-DelunoExe
            $before3 = @(Get-ToastBodies -Title 'Deluno is already running').Count
            $second = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
            [void]$second.WaitForExit(40000)
            $shown3 = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { @(Get-ToastBodies -Title 'Deluno is already running').Count -gt $before3 }
            $last3 = @(Get-ToastBodies -Title 'Deluno is already running') | Select-Object -Last 1
            Add-Check 'The running copy shows "Deluno is already running" with the address Deluno answers at' ($shown3.Ok -and "$last3" -match "^http://[^/\s]+:$port/?$") "balloon text: '$last3'"

            # ---- a second launch while Deluno is still starting says so
            $before4 = @(Get-ToastBodies -Title 'Deluno is already running').Count
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno$' | Out-Null
            $starting = Wait-Until -TimeoutSec 60 -IntervalSec 0.3 -Condition { $d = Get-TrayDot -Icon 'Deluno' -NoOverflow; if ($d.Found -and $d.Hover -match 'Starting') { $true } else { $null } }
            if ($starting.Ok) {
                $second2 = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
                [void]$second2.WaitForExit(40000)
                $shown4 = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { @(Get-ToastBodies -Title 'Deluno is already running').Count -gt $before4 }
                $last4 = @(Get-ToastBodies -Title 'Deluno is already running') | Select-Object -Last 1
                # The restart may have finished before the second copy was looked at; the balloon then holds the address, which is also right.
                Add-Check 'A second launch while Deluno is starting shows the balloon: "Deluno is still starting." (or the address, once it was up)' ($shown4.Ok -and "$last4" -match '^(Deluno is still starting\.|http://[^/\s]+:\d+/?)$') "balloon text: '$last4'"
                if ("$last4" -eq 'Deluno is still starting.') { Add-Note 'The balloon read "Deluno is still starting." while the host was starting.' }
            }
            else { Add-Note 'The starting state was not caught, so the still-starting wording was not seen.' }
            $null = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Connect-Deluno
        }
        finally {
            Add-OwnLanRuleIfMissing -Port $port
            if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) { Start-DelunoTrayNow }
            $null = Restore-DelunoTray
        }
    }
}

function Add-OwnLanRuleIfMissing {
    param([Parameter(Mandatory)] [int] $Port)
    if (@(Get-InboundRulesFor -Port $Port).Count -eq 0) { Add-OwnLanRule -Port $Port }
}

# ---------------------------------------------------------------- a relaunched Deluno waits for the copy it replaces

function Start-SleepingParent {
    param([Parameter(Mandatory)] [int] $Seconds)
    Start-Process -FilePath (Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', "[void][Threading.Thread]::Sleep($($Seconds * 1000))" -PassThru -WindowStyle Hidden
}

function Invoke-GroupTrayRelaunch {
    Invoke-Scenario -Id 'tray.relaunch-waits' -Title "A Deluno started with --after-restart <pid> waits for that process before it takes the single-instance lock: it shows nothing and serves nothing while the process lives, starts a moment after it ends, and starts anyway after 60 seconds" -Body {
        Connect-Deluno
        $port = Get-LanPort
        $parent = $null; $relaunched = $null
        try {
            Quit-DelunoFromTray | Out-Null
            Add-Check 'Deluno is stopped first (no Deluno process, nothing on its port)' (@(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue).Count -eq 0 -and (Get-StatusWithHostHeader -HostHeader 'localhost' -Port $port) -eq 0) ''

            # ---- the named process ends after 30 seconds: Deluno starts after it, not before
            $parent = Start-SleepingParent -Seconds 30
            $t0 = Get-Date
            Start-DelunoTrayNow -Arguments "--after-restart $($parent.Id)"
            Start-Sleep -Seconds 20 # reason: the wait is the proof; the named process is still alive for ten seconds more
            $earlyIcon = Find-TrayIcon -Icon 'Deluno'
            $earlyAnswer = Test-DelunoAnswering
            Add-Check 'While the named process lives (20 s in) Deluno shows no icon and serves nothing' ((-not $parent.HasExited) -and -not $earlyIcon -and -not $earlyAnswer) "process ended: $($parent.HasExited); icon: $([bool]$earlyIcon); answering: $earlyAnswer"
            $parent.WaitForExit(60000) | Out-Null
            $endedAt = Get-Date
            $icon = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { Find-TrayIcon -Icon 'Deluno' }
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check 'After the named process ends Deluno takes the lock: its icon appears and it answers' ($icon.Ok -and $up.Ok) "icon after $([math]::Round(((Get-Date) - $endedAt).TotalSeconds - $up.Seconds, 0)) s; answering $($up.Seconds) s after the icon"
            Add-Check 'It started after the named process ended, not while it lived (more than 25 s from the launch)' ((($endedAt - $t0).TotalSeconds) -ge 25) "$([math]::Round(($endedAt - $t0).TotalSeconds, 0)) s from the launch to the end of the named process"
            Connect-Deluno

            # ---- a named process that outlives the 60-second wait: Deluno starts anyway
            Quit-DelunoFromTray | Out-Null
            $parent2 = Start-SleepingParent -Seconds 150
            $t1 = Get-Date
            Start-DelunoTrayNow -Arguments "--after-restart $($parent2.Id)"
            Start-Sleep -Seconds 40 # reason: the wait is the proof; well inside the 60-second limit
            Add-Check 'At 40 s, with the named process still alive, Deluno has not started' ((-not $parent2.HasExited) -and -not (Find-TrayIcon -Icon 'Deluno') -and -not (Test-DelunoAnswering)) ''
            $icon2 = Wait-Until -TimeoutSec 150 -IntervalSec 2 -Condition { Find-TrayIcon -Icon 'Deluno' }
            $secs = [math]::Round(((Get-Date) - $t1).TotalSeconds, 0)
            Add-Check 'It stops waiting after about 60 seconds and starts although the named process is still alive' ($icon2.Ok -and -not $parent2.HasExited -and $secs -ge 55 -and $secs -le 140) "icon after $secs s; named process alive: $(-not $parent2.HasExited)"
            $null = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            if ($parent2 -and -not $parent2.HasExited) { Stop-Process -Id $parent2.Id -Force -ErrorAction SilentlyContinue }
        }
        finally {
            foreach ($p in $parent, $parent2) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
            if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) { Start-DelunoTrayNow }
            Add-Check 'Deluno is running again as it was found' (Restore-DelunoTray) ''
        }
    }
}

# ---------------------------------------------------------------- Start with Windows

function Invoke-GroupTraySignIn {
    Invoke-Scenario -Id 'tray.sign-in-entry' -Title "Start with Windows: the tick is read again every time the menu opens (an entry made or removed outside Deluno shows at once), the entry holds the quoted exe and no flags, and a change Windows refuses is said in a balloon and leaves the entry and the tick as they were" -Body {
        Connect-Deluno
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $regPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
        $exe = Get-DelunoExe
        $quoted = '"{0}"' -f $exe.FullName
        $had = Get-SignInEntryValue
        $denied = $false
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        try {
            # ---- an entry removed outside Deluno: the next menu opening shows it unticked
            Remove-ItemProperty -Path $runKey -Name 'Deluno' -ErrorAction SilentlyContinue
            $item = Get-TrayMenuItemState -Pattern '^Start with Windows$'
            Add-Check 'With the entry removed outside Deluno the menu shows Start with Windows unticked at the next opening' ($item -and -not $item.Checked) ($item | ConvertTo-Json -Compress)

            # ---- an entry made outside Deluno: ticked at the next opening, with no restart
            Set-ItemProperty -Path $runKey -Name 'Deluno' -Value $quoted
            $item = Get-TrayMenuItemState -Pattern '^Start with Windows$'
            Add-Check 'With the entry made outside Deluno the menu shows it ticked at the next opening (the tick is read again each time)' ($item -and $item.Checked) ($item | ConvertTo-Json -Compress)

            # ---- pressing it clears the entry; pressing again writes the quoted exe and no flags
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null
            $cleared = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { -not (Get-SignInEntryValue) }
            Add-Check 'Pressing it with the entry set clears the entry' $cleared.Ok ''
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null
            $written = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { Get-SignInEntryValue }
            Add-Check 'Pressing it again writes the entry: the quoted path of Deluno.exe and no flags (so a sign-in start never opens a browser)' ($written.Ok -and $written.Value -eq $quoted) $(if ($written.Ok) { $written.Value -replace [regex]::Escape($env:LOCALAPPDATA), '%LOCALAPPDATA%' } else { 'no entry' })

            # ---- Windows refuses the change: a balloon says so, nothing changes
            Remove-ItemProperty -Path $runKey -Name 'Deluno' -ErrorAction SilentlyContinue
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($regPath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::ChangePermissions -bor [System.Security.AccessControl.RegistryRights]::ReadKey)
            $acl = $key.GetAccessControl()
            $rule = New-Object System.Security.AccessControl.RegistryAccessRule($me, ([System.Security.AccessControl.RegistryRights]::SetValue -bor [System.Security.AccessControl.RegistryRights]::CreateSubKey), [System.Security.AccessControl.AccessControlType]::Deny)
            $acl.AddAccessRule($rule); $key.SetAccessControl($acl); $key.Close(); $denied = $true
            $before = Get-ToastTextCount -Text 'Deluno could not change Start with Windows'
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null
            Add-Check 'When Windows refuses the change a balloon says "Deluno could not change Start with Windows"' (Wait-ToastShown -Text 'Deluno could not change Start with Windows' -Before $before -TimeoutSec 30) 'seen in Windows'' notification record'
            $item = Get-TrayMenuItemState -Pattern '^Start with Windows$'
            Add-Check 'The entry is as it was (none) and the menu still shows it unticked' (-not (Get-SignInEntryValue) -and $item -and -not $item.Checked) ($item | ConvertTo-Json -Compress)
        }
        finally {
            if ($denied) {
                $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($regPath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::ChangePermissions -bor [System.Security.AccessControl.RegistryRights]::ReadKey)
                $acl = $key.GetAccessControl()
                foreach ($r in @($acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' -and $_.IdentityReference.Value -eq $me })) { [void]$acl.RemoveAccessRule($r) }
                $key.SetAccessControl($acl); $key.Close()
            }
            if ($had) { Set-ItemProperty -Path $runKey -Name 'Deluno' -Value $had } else { Remove-ItemProperty -Path $runKey -Name 'Deluno' -ErrorAction SilentlyContinue }
            $restored = ([bool](Get-SignInEntryValue) -eq [bool]$had)
            $writable = $true
            try { Set-ItemProperty -Path $runKey -Name 'GoldenProbe' -Value 'x'; Remove-ItemProperty -Path $runKey -Name 'GoldenProbe' } catch { $writable = $false }
            Add-Check 'The Run key is writable again and the sign-in entry is as found' ($restored -and $writable) "restored: $restored, writable: $writable"
        }
    }
}

# ---------------------------------------------------------------- the tray's "Try again: install apps..." line

# Sets a property of an object read from JSON by its name in whatever letter case the product wrote it (adding it when it is not there).
function Set-JsonProperty {
    param([Parameter(Mandatory)] $Object, [Parameter(Mandatory)] [string] $Name, $Value)
    $p = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($p) { $p.Value = $Value } else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Invoke-GroupTrayTryAgain {
    Invoke-Scenario -Id 'tray.try-again' -Title "The tray's Try again line: it says what it would install and, while GitHub's limit holds, from what time; a balloon offers it once when the limit lifts; pressing it opens the picker for those apps, and a list that cannot be read says so instead of doing nothing" -Body {
        Connect-Deluno
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $pending = Join-Path $dataRoot 'config\pending-component-installs.json'
        $hadPending = Test-Path $pending
        $pendingBefore = if ($hadPending) { Get-Content -Raw -LiteralPath $pending } else { $null }
        $title = '^Set up download clients and Weir$'
        try {
            # ---- an ask the product itself stores: the picker opens, Skip closes it, and the app stays on the list
            $o = Open-PickerByAsk -Pending $pending -Title $title
            Add-Check 'An app asked for on the Companion Tools page is stored and the picker opens' ($o.Ask.Status -eq 202 -and $null -ne $o.Window) "HTTP $($o.Ask.Status)"
            if (-not $o.Window) { return }
            Click-WindowButton -Window $o.Window -NamePattern '^Skip$' | Out-Null
            $closed = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 1) }
            Add-Check 'Skip closes the picker' $closed.Ok ''
            $line = (Wait-Until -TimeoutSec 40 -IntervalSec 3 -Condition { Get-TrayMenuItemState -Pattern '^Try again: install' }).Value
            Add-Check 'The menu now offers "Try again: install NZBGet..." (what it would install, no time while nothing waits on one)' ($line -and $line.Name -eq 'Try again: install NZBGet...' -and $line.Enabled) $(if ($line) { $line.Name } else { 'no Try again line' })
            if (-not $line) { return }

            # ---- GitHub's limit holds: the line says from what time
            $raw = Get-Content -Raw -LiteralPath $pending
            Save-Evidence -Name 'pending-installs-as-stored.json' -Text $raw
            $json = $raw | ConvertFrom-Json
            $when = (Get-Date).ToUniversalTime().AddHours(2)
            Set-JsonProperty -Object $json -Name 'retryAfterUtc' -Value $when.ToString('o')
            Set-JsonProperty -Object $json -Name 'reason' -Value "GitHub's hourly limit on looking up releases was used up."
            Set-Content -LiteralPath $pending -Value ($json | ConvertTo-Json -Depth 4) -Encoding utf8
            $local = $when.ToLocalTime().ToString('h:mm tt')
            $waiting = (Wait-Until -TimeoutSec 40 -IntervalSec 3 -Condition { $l = Get-TrayMenuItemState -Pattern '^Try again: install'; if ($l -and $l.Name -match 'GitHub allows it from') { $l } else { $null } }).Value
            Add-Check 'While GitHub''s limit holds the line says from what time ("Try again: install NZBGet (GitHub allows it from <time>)...")' ($waiting -and $waiting.Name -eq "Try again: install NZBGet (GitHub allows it from $local)...") $(if ($waiting) { "$($waiting.Name) (expected time $local)" } else { 'the line did not change' })
            $offered = Get-ToastTextCount -Text "GitHub's limit has lifted"

            # ---- the limit lifts: one balloon offers it, and not again
            Set-JsonProperty -Object $json -Name 'retryAfterUtc' -Value (Get-Date).ToUniversalTime().AddMinutes(-1).ToString('o')
            Set-Content -LiteralPath $pending -Value ($json | ConvertTo-Json -Depth 4) -Encoding utf8
            Add-Check 'When the limit lifts a balloon says "GitHub''s limit has lifted"' (Wait-ToastShown -Text "GitHub's limit has lifted" -Before $offered -TimeoutSec 60) 'seen in Windows'' notification record'
            Start-Sleep -Seconds 15 # reason: lets Windows finish writing the balloon to its record (it may copy an entry into a second file)
            $after = Get-ToastTextCount -Text "GitHub's limit has lifted"
            Start-Sleep -Seconds 45 # reason: nine 5-second looks pass; the wait is the proof that the balloon is not repeated
            $later = Get-ToastTextCount -Text "GitHub's limit has lifted"
            Add-Check 'It is offered once: the balloon does not come again every few seconds while the same wait stands (a repeat would add nine more in this time)' (($later - $after) -le 2) ("record count {0}, then {1}" -f $after, $later)
            $lifted = Get-TrayMenuItemState -Pattern '^Try again: install'
            Add-Check 'With the wait over the line reads "Try again: install NZBGet..." again' ($lifted -and $lifted.Name -eq 'Try again: install NZBGet...') $(if ($lifted) { $lifted.Name } else { 'no line' })

            # ---- pressing it opens the picker for those apps
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Try again: install' -Modal | Out-Null
            $dlg = Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 30
            Add-Check 'Pressing Try again opens the picker, which says the apps are ticked because they were asked for or an earlier try had to wait' ($null -ne $dlg -and (Get-WindowText -Window $dlg) -match 'ticked because you asked for them, or because an earlier try had to wait') $(if ($dlg) { '' } else { Format-DesktopWindows })
            if ($dlg) {
                Click-WindowButton -Window $dlg -NamePattern '^Skip$' | Out-Null
                Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 1) } | Out-Null
            }

            # ---- a list that cannot be read: the line stays, and pressing it says so
            Set-Content -LiteralPath $pending -Value '{ this is not a list' -Encoding utf8
            $still = Get-TrayMenuItemState -Pattern '^Try again: install'
            Add-Check 'With the list damaged the line stays as it was (a list that cannot be read is "do not know", not "nothing waiting")' ($null -ne $still) $(if ($still) { $still.Name } else { 'the line vanished' })
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Try again: install' -Modal | Out-Null
            $box = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 20
            $boxText = if ($box) { Get-WindowText -Window $box } else { '' }
            Add-Check 'Pressing it says "Deluno could not read the list of apps waiting to be installed just now" instead of doing nothing' ($boxText -match 'could not read the list of apps waiting to be installed') $(if ($box) { $boxText } else { 'no message: ' + (Format-DesktopWindows) })
            if ($box) { Click-WindowButton -Window $box -NamePattern '^OK$' | Out-Null }
        }
        finally {
            foreach ($w in @(Get-DesktopWindows | Where-Object { $_.Process -eq 'Deluno' -and $_.Name -match '^Set up download clients and Weir$|^Deluno$' -and $_.Class -match 'WindowsForms' })) { Close-DesktopWindow -Window $w }
            if ($hadPending) { Set-Content -LiteralPath $pending -Value $pendingBefore -Encoding utf8 } else { Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue }
        }
    }
}

function Set-PickerBox {
    param([Parameter(Mandatory)] $Window, [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)
    $box = Get-WindowControls -Window $Window | Where-Object { $_.Type -eq 'Edit' -and $_.Name -eq $Name } | Select-Object -First 1
    if (-not $box) { return $false }
    $box.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
    $true
}

function Invoke-PickerUsenetLoginAndSubset {
    param([Parameter(Mandatory)] [string] $CandidateSetup, [Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'picker.usenet-login-and-subset' -Title "The picker with only NZBGet ticked: the Usenet login is asked for, an empty login and an unsafe one are refused in words with the window still open and nothing installed, a good one is accepted once and written to NZBGet's own settings, only NZBGet is installed, the finish screen offers Open Deluno, and the browser opens only if that is pressed" -Body {
        # A machine with none of the clients: the previous release, installed without the picker.
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        # The picker under test is the candidate's: update to it first (the previous release's picker is the one #1468 was found in).
        $setupRun = Start-Process -FilePath $CandidateSetup -ArgumentList '--silent' -PassThru
        $setupDone = $setupRun.WaitForExit(600000)
        Add-Check 'Setup.exe --silent over the previous release finished' ($setupDone -and $setupRun.ExitCode -eq 0) "exit $(if ($setupDone) { $setupRun.ExitCode } else { 'none' })"
        if (-not (Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion)) { return }
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $pending = Join-Path $dataRoot 'config\pending-component-installs.json'
        $hadPending = Test-Path $pending
        $pendingBefore = if ($hadPending) { Get-Content -Raw $pending } else { $null }
        $title = '^Set up download clients and Weir$'
        $conf = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NZBGet\nzbget.conf'
        $user = 'golden-nzb'
        $password = 'Gp-Nzb-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $watching = $false
        try {
            $o = Open-PickerByAsk -Pending $pending -Title $title
            Add-Check 'The picker opens when NZBGet is asked for' ($o.Ask.Status -eq 202 -and $null -ne $o.Window) $(if ($o.Window) { "HTTP $($o.Ask.Status)" } else { 'windows: ' + (Format-DesktopWindows) })
            $dlg = $o.Window
            if (-not $dlg) { return }
            Save-Evidence -Name 'picker-controls-at-open.txt' -Text ((Get-WindowControls -Window $dlg | ForEach-Object { "$($_.Type) | $($_.Name) | enabled=$($_.Enabled) offscreen=$($_.Offscreen)" }) -join "`n")
            $r0 = $dlg.Element.Current.BoundingRectangle
            Save-Evidence -Name 'picker-window-rect.txt' -Text ("window {0},{1} {2}x{3}" -f $r0.X, $r0.Y, $r0.Width, $r0.Height)
            $rows = @(Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'CheckBox' -and $_.Name -match '\(' })
            $stateOf = { param($r) try { "$($r.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)" } catch { 'unknown' } }
            $ticked = @($rows | Where-Object { (& $stateOf $_) -eq 'On' } | ForEach-Object { ($_.Name -split ' ')[0] })
            Add-Check 'Only NZBGet is ticked (a subset: the other apps are left alone)' (($ticked -join ',') -eq 'NZBGet') "ticked: $($ticked -join ', ')"
            $text = Get-WindowText -Window $dlg
            Add-Check 'The Usenet login is asked for (NZBGet username and password boxes)' ($text -match 'NZBGet username' -and $text -match 'NZBGet password' -or @(Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Edit' -and $_.Name -like 'NZBGet*' }).Count -ge 2) ''

            # An empty login is refused in words, and the window stays.
            Click-WindowButton -Window $dlg -NamePattern '^Install selected$' | Out-Null
            Start-Sleep -Seconds 2 # reason: UI settle after input; Windows raises no event for it
            $stillOpen = Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 5
            if ($stillOpen) { Save-Evidence -Name 'picker-controls-after-empty-install.txt' -Text ((Get-WindowControls -Window $stillOpen | ForEach-Object { "$($_.Type) | $($_.Name) | enabled=$($_.Enabled) offscreen=$($_.Offscreen)" }) -join "`n") }
            $says = if ($stillOpen) { Get-WindowText -Window $stillOpen } else { '' }
            Add-Check 'An empty login is refused in words and the window stays open' ($null -ne $stillOpen -and $says -match 'Choose a username and a password for NZBGet') $says
            # A login with a quote in it is refused too.
            [void](Set-PickerBox -Window $stillOpen -Name 'NZBGet username' -Value "bad'name")
            [void](Set-PickerBox -Window $stillOpen -Name 'NZBGet password' -Value 'Whatever-1234')
            Click-WindowButton -Window $stillOpen -NamePattern '^Install selected$' | Out-Null
            Start-Sleep -Seconds 2 # reason: UI settle after input; Windows raises no event for it
            $again = Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 5
            Add-Check 'A login with a quote in it is refused in words and the window stays open' ($null -ne $again -and (Get-WindowText -Window $again) -match 'NZBGet') ''
            Add-Check 'Nothing was installed by the refusals' (-not (Test-Path $conf)) $conf
            if (-not $again) { return }

            # A good login is accepted.
            [void](Set-PickerBox -Window $again -Name 'NZBGet username' -Value $user)
            [void](Set-PickerBox -Window $again -Name 'NZBGet password' -Value $password)
            Start-EdgeLaunchWatch; $watching = $true
            Click-WindowButton -Window $again -NamePattern '^Install selected$' | Out-Null
            $finished = Wait-Until -TimeoutSec 1200 -IntervalSec 5 -Condition {
                $w = Wait-DesktopWindow -NamePattern $title -Process 'Deluno' -TimeoutSec 2
                if ($w -and @(Get-WindowControls -Window $w | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Open Deluno' -and -not $_.Offscreen -and $_.Enabled }).Count -gt 0) { $w } else { $null }
            }
            Add-Check 'The install finishes and the finish screen offers Open Deluno and Close' ($finished.Ok -and @(Get-WindowControls -Window $finished.Value | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Close' }).Count -gt 0) $(if ($finished.Ok) { Get-WindowText -Window $finished.Value } else { 'windows: ' + (Format-DesktopWindows) })
            if (-not $finished.Ok) { return }
            Add-Check 'The browser was not opened by itself' ((Get-EdgeLaunchCount) -eq 0) "$(Get-EdgeLaunchCount) browser start(s)"
            $confText = if (Test-Path $conf) { Get-Content -Raw $conf } else { '' }
            Add-Check 'The login was written to NZBGet''s own settings' ($confText -match "(?m)^ControlUsername=$([regex]::Escape($user))\s*$" -and $confText -match "(?m)^ControlPassword=$([regex]::Escape($password))\s*$") $conf
            $log = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
            $logs = @($log, (Join-Path $env:LOCALAPPDATA 'Deluno\logs\component-installs.log') | Where-Object { Test-Path $_ } | ForEach-Object { Get-Content -Raw $_ })
            Add-Check 'The password is never printed in the install log' (@($logs | Where-Object { $_ -match [regex]::Escape($password) }).Count -eq 0) ''
            $others = @('qbittorrent', 'transmission', 'deluge', 'sabnzbd') | Where-Object { Get-NetTCPConnection -State Listen -LocalPort @{ qbittorrent = 8081; transmission = 9091; deluge = 8112; sabnzbd = 8080 }[$_] -ErrorAction SilentlyContinue }
            $nzb = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition { Get-NetTCPConnection -State Listen -LocalPort 6789 -ErrorAction SilentlyContinue }
            Add-Check 'Only NZBGet was installed: it answers, and none of the other clients does' ($nzb.Ok -and @($others).Count -eq 0) "others answering: $(@($others) -join ', ')"
            Click-WindowButton -Window $finished.Value -NamePattern '^Open Deluno$' | Out-Null
            $opened = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { if ((Get-EdgeLaunchCount) -ge 1) { $true } else { $null } }
            Add-Check 'Pressing Open Deluno opens the browser' $opened.Ok ''
        }
        finally {
            if ($watching) { Stop-EdgeLaunchWatch }
            foreach ($w in @(Get-DesktopWindows | Where-Object { $_.Process -eq 'Deluno' -and $_.Name -match 'Set up download clients|Third-party licences' })) { Close-DesktopWindow -Window $w }
            if ($hadPending) { Set-Content -LiteralPath $pending -Value $pendingBefore -Encoding utf8 } else { Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue }
        }
    }
}
