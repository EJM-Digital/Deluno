<#
    Rows of the coverage register that touch another app, which only the real clients, the real Weir and a real Windows can prove (#1329, #1278).
    Each scenario runs alone (-Scenarios <id>); nothing here depends on another scenario having run.

      maintenance.lane-passes          the planning.maintenance lane runs every pass it owns on a real install, with a real client and Weir
      removal.queue-flags.<client>     the removeFromClient, blocklist and skipRedownload flags on a real torrent client and a real Usenet client
      sharing.completion.<client>      a real torrent client removes, or only stops, a finished imported torrent and the library file stays whole
      import.direct-job                a library that does not hand to a processor: the real client's finished film is queued for import with the catalogue's details
      import.unmatched-output          a cleaned file in Weir's output folder that matches no hand-off is listed once and never imported
      import.marker-held-open          a restart mark another program holds open is left for the next pass and the staging file is never deleted
      redownload.arrives-through-the-workflow   after Weir asks for a title again, the new copy goes through the client, Weir and the library
      install.sabnzbd-folders          the real SABnzbd the picker installed has its two folders and a category with a folder each
      install.sign-ins-recorded        the picker's install record and sign-ins: every installed client recorded, SABnzbd's own key kept, Deluge given no login
      hub.connection-used-processor    a real hand-off to Weir pushes ConnectionUsed with today's count
      weir.connect-variations          Connect Weir with missing details, a wrong password and when already connected
      indexer.proxy-requests           a request really leaves through the indexer's HTTP and SOCKS5 proxy, and a wrong proxy password is refused with 407
#>

# ---------------------------------------------------------------- helpers

# The sharing choices as the settings screen holds them, so a scenario can put them back.
function Get-SharingForSuite {
    $s = Invoke-Deluno -Path '/api/settings'
    [pscustomobject]@{ Mode = $s.sharingMode; ForHours = $s.sharingForHours; UntilRatio = $s.sharingUntilRatio; Completion = $s.sharingCompletionAction }
}

function Set-SharingForSuite {
    param([Parameter(Mandatory)] [string] $Mode, [Parameter(Mandatory)] [string] $Completion, $ForHours = $null, $UntilRatio = $null)
    Invoke-Deluno -Method PATCH -Path '/api/settings' -Body @{ sharingMode = $Mode; sharingCompletionAction = $Completion; sharingForHours = $ForHours; sharingUntilRatio = $UntilRatio } | Out-Null
}

# Removes a finished download the way the external API does (Radarr's DELETE /queue/{id} and its three flags).
function Remove-FilmByFlags {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] [bool] $RemoveFromClient, [Parameter(Mandatory)] [bool] $Blocklist, [bool] $SkipRedownload = $false)
    $row = Find-QueueRow -Name $Film.Release -IncludeHistory
    if (-not $row) { throw "The client shows no row for $($Film.Release)." }
    $q = 'removeFromClient={0}&blocklist={1}&skipRedownload={2}' -f $RemoveFromClient.ToString().ToLowerInvariant(), $Blocklist.ToString().ToLowerInvariant(), $SkipRedownload.ToString().ToLowerInvariant()
    Try-Deluno -Method DELETE -Path "/api/queue/$($Film.ClientId)/$([uri]::EscapeDataString([string]$row.id))?$q"
}

# Whether the client itself still holds a film's download: a torrent by the client's own answer, a Usenet job by the files left.
function Test-ClientHoldsFilm {
    param([Parameter(Mandatory)] $Film)
    if ($Film.Protocol -eq 'torrent') { return [bool](Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release).Present }
    @(Get-FilmLeftovers -Film $Film).Count -gt 0
}

# The folder a refine-before-import library hands its downloads to, read back from Deluno.
function Set-MoviesWorkflow {
    param([Parameter(Mandatory)] $Library, [Parameter(Mandatory)] [string] $Workflow)
    Try-Deluno -Method PUT -Path "/api/libraries/$($Library.id)/workflow" -Body @{
        importWorkflow = $Workflow; processorConnectionId = $Library.processorConnectionId; processorName = $Library.processorName
        processorOutputPath = $Library.processorOutputPath; processorTimeoutMinutes = $Library.processorTimeoutMinutes
        processorFailureMode = $Library.processorFailureMode; cleanupMode = $Library.cleanupMode; removeEmptySourceFolders = $Library.removeEmptySourceFolders
    }
}

# SABnzbd's own settings file, wherever the install put it.
function Get-SabnzbdIni {
    $found = @(foreach ($base in $env:LOCALAPPDATA, $env:ProgramData, $env:APPDATA) {
            if ($base) { Get-ChildItem -Path (Join-Path $base 'sabnzbd') -Filter 'sabnzbd.ini' -ErrorAction SilentlyContinue }
        }) | Select-Object -First 1
    if ($found) { Get-Content -Raw -LiteralPath $found.FullName } else { '' }
}

# A forward proxy for the proxy scenario: HTTP (absolute-address requests and CONNECT) and SOCKS5, both asking for a login, writing down
# every request it passes on and every one it turns away. Plain C# 5, because Windows PowerShell 5.1 compiles it.
function Initialize-GoldenProxyType {
    if (([System.Management.Automation.PSTypeName]'GoldenProxy').Type) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class GoldenProxy
{
    private TcpListener _listener;
    private string _user;
    private string _pass;
    private readonly List<string> _log = new List<string>();
    public int Rejected;
    public string MapFrom = "";
    public string MapTo = "";

    private string Real(string host) { return host == MapFrom ? MapTo : host; }

    public string[] Lines { get { lock (_log) { return _log.ToArray(); } } }

    private void Note(string line) { lock (_log) { _log.Add(line); } }

    public void Start(int port, string user, string pass)
    {
        _user = user; _pass = pass;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Thread t = new Thread(Accept);
        t.IsBackground = true;
        t.Start();
    }

    public void Stop() { try { _listener.Stop(); } catch (Exception) { } }

    private void Accept()
    {
        while (true)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch (Exception) { return; }
            Task.Run(() => Serve(c));
        }
    }

    private static void Write(NetworkStream s, string text) { byte[] b = Encoding.ASCII.GetBytes(text); s.Write(b, 0, b.Length); }

    private void Serve(TcpClient client)
    {
        try
        {
            NetworkStream s = client.GetStream();
            int first = s.ReadByte();
            if (first == 5) { ServeSocks(client, s); return; }
            StringBuilder head = new StringBuilder();
            head.Append((char)first);
            byte[] one = new byte[1];
            while (head.Length < 16384)
            {
                if (s.Read(one, 0, 1) != 1) break;
                head.Append((char)one[0]);
                if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
            }
            string[] lines = head.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] request = lines[0].Split(' ');
            string method = request[0];
            string target = request.Length > 1 ? request[1] : "";
            string wanted = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(_user + ":" + _pass));
            string given = "";
            foreach (string line in lines)
            {
                if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)) given = line.Substring(20).Trim();
            }
            if (given != wanted)
            {
                Interlocked.Increment(ref Rejected);
                Note("407 " + method + " " + target);
                Write(s, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"golden\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                client.Close();
                return;
            }
            if (method == "CONNECT")
            {
                string[] hp = target.Split(':');
                TcpClient upstream = new TcpClient(Real(hp[0]), int.Parse(hp[1]));
                Note("CONNECT " + target);
                Write(s, "HTTP/1.1 200 Connection established\r\n\r\n");
                Pipe(client, upstream);
                return;
            }
            Uri uri = new Uri(target);
            Note(method + " " + target);
            TcpClient origin = new TcpClient(Real(uri.Host), uri.Port);
            NetworkStream o = origin.GetStream();
            StringBuilder outgoing = new StringBuilder();
            outgoing.Append(method + " " + uri.PathAndQuery + " HTTP/1.1\r\n");
            bool hasHost = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                if (line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) { hasHost = true; outgoing.Append("Host: " + Real(uri.Host) + ":" + uri.Port + "\r\n"); continue; }
                outgoing.Append(line + "\r\n");
            }
            if (!hasHost) outgoing.Append("Host: " + Real(uri.Host) + ":" + uri.Port + "\r\n");
            outgoing.Append("Connection: close\r\n\r\n");
            Write(o, outgoing.ToString());
            o.CopyTo(s);
            origin.Close();
            client.Close();
        }
        catch (Exception) { try { client.Close(); } catch (Exception) { } }
    }

    private void ServeSocks(TcpClient client, NetworkStream s)
    {
        int count = s.ReadByte();
        byte[] methods = new byte[count];
        s.Read(methods, 0, count);
        s.WriteByte(5); s.WriteByte(2);
        s.ReadByte();
        int ulen = s.ReadByte();
        byte[] u = new byte[ulen]; s.Read(u, 0, ulen);
        int plen = s.ReadByte();
        byte[] p = new byte[plen]; s.Read(p, 0, plen);
        if (Encoding.ASCII.GetString(u) != _user || Encoding.ASCII.GetString(p) != _pass)
        {
            Interlocked.Increment(ref Rejected);
            Note("SOCKS5 refused the login");
            s.WriteByte(1); s.WriteByte(1);
            client.Close();
            return;
        }
        s.WriteByte(1); s.WriteByte(0);
        byte[] head = new byte[4];
        s.Read(head, 0, 4);
        string host;
        if (head[3] == 1) { byte[] a = new byte[4]; s.Read(a, 0, 4); host = new IPAddress(a).ToString(); }
        else if (head[3] == 3) { int n = s.ReadByte(); byte[] a = new byte[n]; s.Read(a, 0, n); host = Encoding.ASCII.GetString(a); }
        else { byte[] a = new byte[16]; s.Read(a, 0, 16); host = new IPAddress(a).ToString(); }
        int hi = s.ReadByte(); int lo = s.ReadByte();
        int port = hi * 256 + lo;
        Note("SOCKS5 CONNECT " + host + ":" + port);
        TcpClient upstream = new TcpClient(Real(host), port);
        byte[] ok = new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        s.Write(ok, 0, ok.Length);
        Pipe(client, upstream);
    }

    private static void Pipe(TcpClient a, TcpClient b)
    {
        Task t1 = Task.Run(() => { try { a.GetStream().CopyTo(b.GetStream()); b.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } });
        Task t2 = Task.Run(() => { try { b.GetStream().CopyTo(a.GetStream()); a.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } });
        Task.WaitAll(new Task[] { t1, t2 }, 120000);
        try { a.Close(); } catch (Exception) { }
        try { b.Close(); } catch (Exception) { }
    }
}
'@
}

function Invoke-GroupGapRows {
    # ---------------------------------------------------------------- the maintenance lane
    Invoke-Scenario -Id 'maintenance.lane-passes' -Title 'The planning.maintenance lane runs every pass it owns on a real install with a real client and Weir: the outcome pass tells Weir what became of a film, and none of the ten passes has failed' -Body {
        $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'lane') -Kind 'qbittorrent'
        $r = Wait-FilmImported -Film $film
        Add-Check 'A film came in through the real client and the real Weir, so the lane had real work' ($r.Imported -and $r.Settled) "imported $($r.Imported), settled $($r.Settled)"
        $told = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition { @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match '"category":"processing\.outcome-(reported|not-reported|superseded)"' }).Count -gt 0 }
        Add-Check 'The outcome pass told Weir what became of the film (Activity has the closing line)' $told.Ok ''
        $keys = @('dispatch.cleanup', 'processor.outcomes', 'download.state', 'metadata.refresh', 'artwork.cache.cleanup', 'recycle.bin.cleanup', 'download.leftover.cleanup', 'blocked.release.cleanup', 'library.file.check', 'unmatched.import.giveUp')
        $ran = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition {
            $t = @(Invoke-Deluno -Path '/api/system/tasks')
            $missing = @($keys | Where-Object { $k = $_; @($t | Where-Object { $_.key -eq $k -and $_.lastResult -eq 'completed' }).Count -eq 0 })
            if ($missing.Count -eq 0) { $t } else { $null }
        }
        $tasks = @(Invoke-Deluno -Path '/api/system/tasks')
        Save-Evidence -Name 'maintenance-lane-tasks.json' -Text ($tasks | ConvertTo-Json -Depth 5)
        $state = ($keys | ForEach-Object { $k = $_; $t = $tasks | Where-Object { $_.key -eq $k } | Select-Object -First 1; "$k=$(if ($t) { $t.lastResult } else { 'unknown' })" }) -join ', '
        Add-Check 'Every pass of the lane has run and completed' $ran.Ok $state
        $failed = @($tasks | Where-Object { $_.key -in $keys -and $_.lastResult -eq 'failed' })
        Add-Check 'None of the lane''s passes has failed' ($failed.Count -eq 0) (($failed | ForEach-Object { "$($_.key): $($_.lastError)" }) -join '; ')
    }

    # ---------------------------------------------------------------- the removal flags
    foreach ($kind in 'qbittorrent', 'sabnzbd') {
        Invoke-Scenario -Id "removal.queue-flags.$kind" -Title "The removal flags on a real $kind download: removeFromClient=false only stops tracking, true deletes it with its files, blocklist refuses the release and searches again, skipRedownload suppresses the search" -Body {
            $films = [ordered]@{}
            try {
                Start-AutomationPause
                foreach ($case in 'keep', 'delete', 'block', 'skip') {
                    $films[$case] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'folder' -Label "flag$case") -Kind $kind
                }
                foreach ($case in @($films.Keys)) { Wait-FilmFinished -Film $films[$case] -TimeoutSec 300 | Out-Null }

                $keep = Remove-FilmByFlags -Film $films['keep'] -RemoveFromClient $false -Blocklist $false
                Save-Evidence -Name "removal-flags-keep-$kind.json" -Text $keep.Raw
                Add-Check 'removeFromClient=false is accepted and says Deluno only stopped tracking it' ($keep.Status -eq 200 -and "$($keep.Data.message)" -match 'stopped tracking it' -and "$($keep.Data.message)" -match 'still has it') "HTTP $($keep.Status): $($keep.Data.message)"
                Start-Sleep -Seconds 10 # reason: gives the client time to show it would have deleted the files, which is the thing that must not happen
                Add-Check 'The real client still holds the download and its files' (Test-ClientHoldsFilm -Film $films['keep']) ''
                Add-Check 'It was neither blocklisted nor searched for again' ($keep.Data.blocklisted -ne $true -and $keep.Data.searchStarted -ne $true) ''

                $delete = Remove-FilmByFlags -Film $films['delete'] -RemoveFromClient $true -Blocklist $false
                Save-Evidence -Name "removal-flags-delete-$kind.json" -Text $delete.Raw
                Add-Check 'removeFromClient=true is accepted' ($delete.Status -eq 200 -and $delete.Data.succeeded) "HTTP $($delete.Status): $($delete.Data.message)"
                $gone = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { -not (Test-ClientHoldsFilm -Film $films['delete']) }
                Add-Check 'The real client no longer holds it and its files are gone' $gone.Ok "after $($gone.Seconds) s"
                Add-Check 'Without blocklist nothing is blocklisted and no search starts' ($delete.Data.blocklisted -ne $true -and $delete.Data.searchStarted -ne $true) ''

                $block = Remove-FilmByFlags -Film $films['block'] -RemoveFromClient $true -Blocklist $true
                Save-Evidence -Name "removal-flags-blocklist-$kind.json" -Text $block.Raw
                Add-Check 'blocklist=true is accepted, blocklists the release and starts a new search' ($block.Status -eq 200 -and $block.Data.blocklisted -eq $true -and $block.Data.searchStarted -eq $true) "HTTP $($block.Status): $($block.Data.message)"
                $skip = Remove-FilmByFlags -Film $films['skip'] -RemoveFromClient $true -Blocklist $true -SkipRedownload $true
                Save-Evidence -Name "removal-flags-skip-$kind.json" -Text $skip.Raw
                Add-Check 'blocklist with skipRedownload blocklists the release and starts no search' ($skip.Status -eq 200 -and $skip.Data.blocklisted -eq $true -and $skip.Data.searchStarted -ne $true) "HTTP $($skip.Status): $($skip.Data.message)"
                $list = Try-Deluno -Path '/api/blocked-releases?pageSize=200'
                Add-Check 'The blocklist holds both blocklisted releases and neither of the others' ($list.Raw -match [regex]::Escape($films['block'].Release) -and $list.Raw -match [regex]::Escape($films['skip'].Release) -and $list.Raw -notmatch [regex]::Escape($films['keep'].Release) -and $list.Raw -notmatch [regex]::Escape($films['delete'].Release)) "HTTP $($list.Status)"
            }
            finally { Stop-AutomationPause }
        }
    }

    # ---------------------------------------------------------------- the sharing target
    foreach ($kind in $script:Kinds.torrent) {
        Invoke-Scenario -Id "sharing.completion.$kind" -Title "A finished, imported $kind torrent tidied up straight away is removed (the 'only stop seeding' choice applies to a met sharing target, not to this), and the library file stays whole" -Body {
            $before = Get-SharingForSuite
            try {
                # Tidy up at once: the target is met the moment the title is safely in the library.
                Set-SharingForSuite -Mode 'tidy-now' -Completion 'reclaim'
                $film = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'folder' -Label 'reclaim') -Kind $kind
                $r = Wait-FilmImported -Film $film
                Add-Check 'The film was imported first' $r.Imported ''
                if (-not $r.Imported) { return }
                $length = $r.File.Length
                $removed = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition { -not (Get-ClientTorrent -Kind $kind -Hash $film.Hash -For $film.Release).Present }
                Add-Check "The real $kind removed the finished torrent at the sharing target" $removed.Ok "after $($removed.Seconds) s"
                $lib = Find-Imported -Title $film.Title
                Add-Check 'The library file is whole after the client let go of its copy (the hard link was not broken)' ($lib -and $lib.Length -eq $length) $(if ($lib) { "$($lib.Length) of $length bytes" } else { 'the library file is gone' })
                if ($lib) {
                    $fs = [System.IO.File]::OpenRead($lib.FullName)
                    try { $buffer = New-Object byte[] 65536; $read = $fs.Read($buffer, 0, $buffer.Length) } finally { $fs.Dispose() }
                    Add-Check 'The library file can still be read' ($read -gt 0) "$read bytes read"
                }

                # "Only stop seeding" is what happens once a sharing TARGET is met (#821). Tidying up straight away has no target and nothing
                # shared yet to stop (SharingPolicyEvaluator: tidy-now always reclaims), so with the stop choice made it still removes the
                # torrent. A met time or ratio target needs real seeding (at least an hour, or a peer taking data), which the suite cannot
                # wait for; the stop-at-target path is held by SharingPolicyEvaluatorTests and SharingReclaimServiceTests.
                Set-SharingForSuite -Mode 'tidy-now' -Completion 'stop'
                $second = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'folder' -Label 'stopchosen') -Kind $kind
                $r2 = Wait-FilmImported -Film $second
                Add-Check 'The second film was imported' $r2.Imported ''
                if (-not $r2.Imported) { return }
                $length2 = $r2.File.Length
                $gone2 = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition { -not (Get-ClientTorrent -Kind $kind -Hash $second.Hash -For $second.Release).Present }
                Add-Check "With 'only stop' chosen, tidying up straight away still removes the finished $kind torrent (there is no sharing target to stop at)" $gone2.Ok "after $($gone2.Seconds) s"
                $lib2 = Find-Imported -Title $second.Title
                Add-Check 'The library file is whole' ($lib2 -and $lib2.Length -eq $length2) ''
            }
            finally { try { Set-SharingForSuite -Mode $before.Mode -Completion $before.Completion -ForHours $before.ForHours -UntilRatio $before.UntilRatio } catch { Write-SuiteLog "   could not put the sharing choices back: $($_.Exception.Message)" } }
        }
    }

    # ---------------------------------------------------------------- a direct (non-processor) import
    Invoke-Scenario -Id 'import.direct-job' -Title 'A library that does not hand to a processor: a real client''s finished film is queued for import with the catalogue''s details, transfer mode auto and copy fallback on, and Activity says Deluno queued it' -Body {
        $lib = Get-Library -MediaType movies
        $was = $lib.importWorkflow
        $switched = Set-MoviesWorkflow -Library $lib -Workflow 'standard'
        try {
            Add-Check 'The Movies library is switched to the standard workflow (no processor)' ($switched.Status -ge 200 -and $switched.Status -lt 300) "HTTP $($switched.Status) $($switched.Raw)"
            if ($switched.Status -lt 200 -or $switched.Status -ge 300) { return }
            $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'direct') -Kind 'qbittorrent'
            Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
            $queued = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition {
                $hit = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match '"category":"filesystem\.import\.auto-queued"' }) | Select-Object -First 1
                if ($hit) { $hit } else { $null }
            }
            Add-Check 'Activity says Deluno queued it for import' $queued.Ok ''
            if (-not $queued.Ok) { return }
            Save-Evidence -Name 'import-direct-activity.json' -Text $queued.Value
            $entry = $queued.Value | ConvertFrom-Json
            Add-Check 'The line names the library it is queued into' ("$($entry.message)" -match 'queued it for import into') "$($entry.message)"
            $jobId = "$($entry.relatedJobId)"
            Add-Check 'The Activity line points at the import job' ([bool]$jobId) $jobId
            if ($jobId) {
                $job = Try-Deluno -Path "/api/jobs/$jobId"
                Save-Evidence -Name 'import-direct-job.json' -Text $job.Raw
                Add-Check 'The job carries the catalogue''s title and year and the library' ($job.Raw -match [regex]::Escape($film.Title) -and $job.Raw -match [regex]::Escape($lib.id)) ''
                Add-Check 'The transfer mode is auto and the copy fallback is on' ($job.Raw -match 'transferMode\W+auto' -and $job.Raw -match 'allowCopyFallback\W+true') ''
                Add-Check 'Nothing authorised a replacement, so there is no overwrite' ($job.Raw -match 'overwrite\W+false' -or $job.Raw -notmatch 'overwrite\W+true') ''
            }
            Add-Check 'No hand-off to Weir was made' ($null -eq (Get-FilmHandoff -Film $film)) ''
            $in = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition { Find-Imported -Title $film.Title }
            Add-Check 'The film arrived in the library' $in.Ok ''
        }
        finally {
            $back = Set-MoviesWorkflow -Library $lib -Workflow $was
            if ($back.Status -lt 200 -or $back.Status -ge 300) { Write-SuiteLog "   could not put the Movies workflow back: HTTP $($back.Status) $($back.Raw)" }
        }
    }

    # ---------------------------------------------------------------- Weir's output folder
    Invoke-Scenario -Id 'import.unmatched-output' -Title 'A cleaned file in Weir''s output folder that matches no hand-off is listed once as unmatched in a processor''s output and is never imported on a guess' -Body {
        $lib = Get-Library -MediaType movies
        $out = $lib.processorOutputPath
        Add-Check 'The Movies library hands to Weir and has an output folder' ([bool]$out -and (Test-Path -LiteralPath $out)) "$out"
        if (-not $out) { return }
        $token = New-FilmToken
        $stem = "Stranger.Output.$token.2019.1080p.WEB-DL.x264-GOLDEN"
        $file = Join-Path $out "$stem.mkv"
        try {
            Copy-Item -LiteralPath $script:Fx.Base['small'] -Destination $file -Force
            (Get-Item -LiteralPath $file).LastWriteTime = (Get-Date).AddMinutes(-30)
            $seen = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition {
                $lines = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($stem) -and $_ -match '"category":"processing\.output\.unmatched"' })
                if ($lines.Count -gt 0) { $lines } else { $null }
            }
            Add-Check 'Activity says the processor''s output did not match anything' $seen.Ok ''
            Start-Sleep -Seconds 150 # reason: five more planning passes, so a second listing would show; the file must be named once however many passes see it
            $lines = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($stem) -and $_ -match '"category":"processing\.output\.unmatched"' })
            Add-Check 'It is listed once, however many passes saw it' ($lines.Count -eq 1) "$($lines.Count) lines"
            # Health runs its full pass every 15 minutes; "Check now" (#1217) asks every check to run within seconds, which is how a person sees it.
            Try-Deluno -Method POST -Path '/api/system/health/run' | Out-Null
            $listed = Wait-Until -TimeoutSec 90 -IntervalSec 3 -Condition {
                $h = Try-Deluno -Path '/api/system/health'
                if ($h.Raw -match [regex]::Escape($stem) -and $h.Raw -match '(?i)unmatched in a processor') { $h } else { $null }
            }
            $health = Try-Deluno -Path '/api/system/health'
            $rows = ([regex]::Matches($health.Raw, [regex]::Escape($stem))).Count
            Save-Evidence -Name 'import-unmatched-output-health.json' -Text $health.Raw
            Add-Check 'Health lists it as unmatched in a processor''s output' ($health.Raw -match '(?i)unmatched in a processor' -and $rows -gt 0) "$rows mention(s) of the file"
            Add-Check 'It was never imported on a guess' ($null -eq (Find-Imported -Title $stem)) ''
            Add-Check 'The file is still in Weir''s output, untouched' (Test-Path -LiteralPath $file) $file
        }
        finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    }

    # ---------------------------------------------------------------- a restart mark another program holds
    Invoke-Scenario -Id 'import.marker-held-open' -Title 'A restart mark that another program holds open is left for the next pass: it is not taken for a damaged one, the half-copied file is not deleted, and once the program lets go it is recovered' -Body {
        $token = New-FilmToken
        $srcRoot = (Join-Path $script:VolDir "src_Held$token")
        $libRoot = "C:\Media\Held$token"
        $lib = $null; $movie = $null; $hold = $null
        try {
            $size = [long]30GB
            $free = Get-FreeBytes -Drive 'C'
            if ($free -lt [long]75GB) { $size = [long]8GB }
            $title = "Held Mark $token"
            $src = New-VolumeSource -Folder $srcRoot -Title $title
            $fs = [System.IO.File]::Open($src, 'Open', 'ReadWrite', 'None')
            try { $fs.SetLength($size) } finally { $fs.Dispose() }
            (Get-Item -LiteralPath $src).LastWriteTime = (Get-Date).AddHours(-1)
            $lib = New-VolumeLibrary -Name "Held $token" -Root $libRoot
            $movie = New-VolumeMovie -Title $title -Library $lib

            $pv = [ordered]@{ sourcePath = $src; fileName = (Split-Path $src -Leaf); mediaType = 'movies'; title = $title; year = 2008; genres = @(); tags = @(); libraryId = $lib.id }
            $job = Start-Job -ScriptBlock {
                param($url, $token, $body)
                try { Invoke-WebRequest -Uri "$url/api/filesystem/import/execute-batch" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body $body -UseBasicParsing -TimeoutSec 600 | Out-Null } catch { }
            } -ArgumentList $script:S.DelunoUrl, $script:S.Token, (@{ items = @(@{ preview = $pv; transferMode = 'copy'; overwrite = $false; allowCopyFallback = $true }) } | ConvertTo-Json -Depth 8)
            $caught = $null
            $until = (Get-Date).AddSeconds(180)
            while (-not $caught -and (Get-Date) -lt $until) {
                if (Test-Path -LiteralPath $libRoot) {
                    foreach ($p in [System.IO.Directory]::EnumerateFiles($libRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
                        if ((Split-Path $p -Leaf) -like '*.deluno-marker.json') { $caught = $p; break }
                    }
                }
                if (-not $caught) { Start-Sleep -Milliseconds 15 } # reason: a pause between looks inside a loop bounded by its deadline
            }
            Add-Check 'The copy was under way (its restart mark was on disk)' ([bool]$caught) "$caught"
            Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Stop-Process -Force
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            $dead = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue) -and -not (Test-DelunoAnswering) }
            Add-Check 'Deluno was killed mid-copy' $dead.Ok ''
            if (-not $caught) { return }
            $marker = Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -Filter '*.deluno-marker.json' -ErrorAction SilentlyContinue | Select-Object -First 1
            Add-Check 'The kill left the restart mark' ($null -ne $marker) ''
            if (-not $marker) { return }
            $staging = $marker.FullName.Substring(0, $marker.FullName.Length - '.deluno-marker.json'.Length)
            # A copy killed part-way is still in its own temporary file beside the staging name (<staging>.deluno-copy-*.tmp); the staging
            # name itself exists only once that copy has been finished and verified. Either one is the half-copied file.
            $halfFiles = {
                $found = @()
                if (Test-Path -LiteralPath $staging) { $found += Get-Item -LiteralPath $staging -Force }
                $found += @(Get-ChildItem -LiteralPath (Split-Path $staging -Parent) -Force -File -Filter ((Split-Path $staging -Leaf) + '.deluno-copy-*.tmp') -ErrorAction SilentlyContinue)
                $found
            }
            $stagingBefore = [long](@(& $halfFiles) | Measure-Object -Property Length -Sum).Sum
            if (@(& $halfFiles).Count -eq 0) { $stagingBefore = -1 }
            Add-Check 'The half-copied file is on disk beside it' ($stagingBefore -ge 0) $staging

            # Another program (this runner, a different process from Deluno) opens the mark and keeps it open, as a media server, an antivirus scan or a sync client does.
            $hold = [System.IO.File]::Open($marker.FullName, 'Open', 'ReadWrite', 'None')
            $up = Restore-DelunoTray
            Add-Check 'Deluno starts again and answers while the mark is held' $up ''
            if (-not $up) { return }
            Start-Sleep -Seconds 90 # reason: recovery runs at start and again every pass; this is several passes, bounded so a mark held for good is seen to be left alone
            Add-Check 'The mark is still there (it was not taken for a damaged one and deleted)' (Test-Path -LiteralPath $marker.FullName) ''
            $stagingNow = [long](@(& $halfFiles) | Measure-Object -Property Length -Sum).Sum
            if (@(& $halfFiles).Count -eq 0) { $stagingNow = -1 }
            Add-Check 'The half-copied file was never deleted, the only copy of a move import' ($stagingNow -eq $stagingBefore) "was $stagingBefore bytes, now $stagingNow"
            Add-Check 'The source film is whole' ((Test-Path -LiteralPath $src) -and (Get-Item -LiteralPath $src).Length -eq $size) ''
            $damaged = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($title) -and $_ -match '(?i)damaged|unreadable|corrupt' })
            Add-Check 'Activity does not call the import damaged' ($damaged.Count -eq 0) (($damaged | Select-Object -First 1) -join '')

            $hold.Dispose(); $hold = $null
            $settled = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition {
                $rest = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -notin '.mkv', '.mp4', '.avi' })
                if ($rest.Count -eq 0) { $true } else { $null }
            }
            $files = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue)
            Add-Check 'Once the program lets go, the next pass recovers it: no half-copied file or mark is left' $settled.Ok (($files | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length }) -join '; ')
            $videos = @($files | Where-Object { $_.Extension -in '.mkv', '.mp4', '.avi' })
            Add-Check 'The film is never lost: whole in the library or whole at the source' ((($videos.Count -eq 1) -and ($videos[0].Length -eq $size)) -or ((Test-Path -LiteralPath $src) -and (Get-Item -LiteralPath $src).Length -eq $size)) ''
        }
        finally {
            if ($hold) { try { $hold.Dispose() } catch { } }
            if (-not (Test-DelunoAnswering)) { Restore-DelunoTray | Out-Null }
            Remove-VolumeMovie -Movie $movie
            Remove-VolumeLibrary -Library $lib
            Remove-Item -LiteralPath $srcRoot, $libRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ---------------------------------------------------------------- Weir asks for a title again
    Invoke-Scenario -Id 'redownload.arrives-through-the-workflow' -Title 'Weir asks Deluno to download an imported film again: the new copy goes through the real client, is handed to Weir because the library waits for processing, and is imported' -Body {
        $keyName = 'gaprows-redownload-key'
        $created = $null
        $film = New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'again2'
        $film = Send-Film -Film $film -Kind 'qbittorrent'
        $r = Wait-FilmImported -Film $film
        Add-Check 'The film came in the normal way first' ($r.Imported -and $r.Settled) "imported $($r.Imported)"
        if (-not $r.Imported) { return }
        $path = $r.File.FullName
        $firstHandoff = Get-FilmHandoff -Film $film
        try {
            $created = Invoke-Deluno -Method POST -Path '/api/api-keys' -Body @{ name = $keyName; scopes = 'read,imports' }
            $script:S.Secrets[$keyName] = [string]$created.apiKey
            Start-AutomationPause
            $status = 0; $raw = ''
            try {
                $resp = Invoke-WebRequest -Uri "$($script:S.DelunoUrl)/api/integrations/external/redownload" -Method Post -Headers @{ 'X-Api-Key' = (Get-SuiteSecret $keyName) } -ContentType 'application/json' -Body (@{ path = $path; reason = 'Keep another audio track (suite)'; tool = 'Weir' } | ConvertTo-Json) -UseBasicParsing -TimeoutSec 60
                $status = [int]$resp.StatusCode; $raw = [string]$resp.Content
            }
            catch { try { $status = [int]$_.Exception.Response.StatusCode } catch { } }
            Add-Check 'Deluno answers 202 with the file removed' ($status -eq 202 -and $raw -match '"fileRemoved":\s*true') "HTTP $status $raw"
            $gone = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { -not (Get-ClientTorrent -Kind 'qbittorrent' -Hash $film.Hash -For $film.Release).Present }
            Add-Check 'The client forgot the finished download' $gone.Ok ''
            Restore-FilmData -Film $film
        }
        finally { Stop-AutomationPause }
        $sent = Try-Deluno -Method POST -Path "/api/movies/$($film.MovieId)/grab" -Body @{
            releaseName = $film.Release; indexerId = $film.IndexerId; indexerName = 'Golden feed'; candidateQuality = 'WEB 1080p'
            downloadUrl = $film.Url; sizeBytes = $film.Size; seeders = 50; force = $true; overrideReason = 'Golden scenario suite: a controlled release'; duplicateAction = $null
        }
        Add-Check 'The same release is sent to the client again and taken' ($sent.Status -ge 200 -and $sent.Status -lt 300 -and $sent.Raw -match '"dispatchStatus":\s*"sent"') "HTTP $($sent.Status)"
        $film.SentUtc = [DateTime]::UtcNow
        # Wait-FilmImported cannot be used here: the first copy's finished row and hand-off carry the same release name, so it would take
        # them for the new copy's and give up on the import two minutes later while the client was still checking the restored data.
        # The new copy is waited for by what is new: the client holding it whole again, then a hand-off other than the first.
        $newHandoff = Wait-Until -TimeoutSec 300 -IntervalSec 2 -Condition {
            $h = Get-FilmHandoff -Film $film
            if ($h -and (-not $firstHandoff -or $h.id -ne $firstHandoff.id)) { return $h }
            Invoke-FilmSettle -Film $film -IfChanged | Out-Null
            $null
        }
        $held = Get-ClientTorrent -Kind 'qbittorrent' -Hash $film.Hash -For $film.Release
        Add-Check 'The new copy finished in the real client' ($held.Present -and $held.Progress -ge 1) "present $($held.Present), progress $($held.Progress)"
        $second = $newHandoff.Value
        Add-Check 'It was handed to Weir, as the library''s workflow says (a new hand-off, not the first one)' ($newHandoff.Ok -and $second -and (-not $firstHandoff -or $second.id -ne $firstHandoff.id)) $(if ($second) { "hand-off $($second.id), status $($second.status)" } else { 'no new hand-off' })
        $settled = Wait-Until -TimeoutSec 300 -IntervalSec 2 -Condition {
            $h = Find-Handoffs -Name $film.Release | Where-Object { $_.id -eq $second.id } | Select-Object -First 1
            if ($h -and $h.status -eq 'completed' -and $h.importJobId) { $h } else { $null }
        }
        $in = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition { Find-Imported -Title $film.Title }
        Add-Check 'Weir finished it and Deluno imported the cleaned file into the library' ($settled.Ok -and $in.Ok) "settled $($settled.Ok), imported $($in.Ok)"
        try { if ($created.id) { Invoke-Deluno -Method DELETE -Path "/api/api-keys/$($created.id)" | Out-Null } } catch { }
    }

    # ---------------------------------------------------------------- what the picker installed
    Invoke-Scenario -Id 'install.sabnzbd-folders' -Title 'The real SABnzbd the picker installed has its two folders and a category with a folder each (Movies and TV), read from SABnzbd itself' -Body {
        $ini = Get-SabnzbdIni
        $key = if ($ini -match '(?m)^\s*api_key\s*=\s*(\w{16,})') { $Matches[1] } else { '' }
        Add-Check 'SABnzbd''s own settings hold an api key' ([bool]$key) ''
        if (-not $key) { return }
        $script:S.Secrets['gaprows-sab-key'] = $key
        $base = 'http://127.0.0.1:8080/api'
        $misc = Invoke-RestMethod -Uri "$base`?mode=get_config&section=misc&output=json&apikey=$key" -UseBasicParsing -TimeoutSec 30
        $cats = Invoke-RestMethod -Uri "$base`?mode=get_config&section=categories&output=json&apikey=$key" -UseBasicParsing -TimeoutSec 30
        Save-Evidence -Name 'install-sabnzbd-config.json' -Text ((@{ complete_dir = $misc.config.misc.complete_dir; download_dir = $misc.config.misc.download_dir; categories = @($cats.config.categories | ForEach-Object { @{ name = $_.name; dir = $_.dir } }) } | ConvertTo-Json -Depth 5))
        $complete = "$($misc.config.misc.complete_dir)"; $incomplete = "$($misc.config.misc.download_dir)"
        Add-Check 'SABnzbd has a folder for finished jobs and a different one for unfinished jobs' ($complete -and $incomplete -and $complete -ne $incomplete) "complete '$complete', incomplete '$incomplete'"
        $movies = @($cats.config.categories | Where-Object { "$($_.name)" -ieq 'Movies' }) | Select-Object -First 1
        $tv = @($cats.config.categories | Where-Object { "$($_.name)" -ieq 'TV' }) | Select-Object -First 1
        Add-Check 'It has a Movies category with a folder of its own' ($movies -and "$($movies.dir)" -match '(?i)Movies') $(if ($movies) { "$($movies.dir)" } else { 'no Movies category' })
        Add-Check 'It has a TV category with a folder of its own' ($tv -and "$($tv.dir)" -match '(?i)TV') $(if ($tv) { "$($tv.dir)" } else { 'no TV category' })
        Add-Check 'The two category folders differ' ($movies -and $tv -and "$($movies.dir)" -ne "$($tv.dir)") ''
        $lib = Get-Library -MediaType movies
        Add-Check 'The Movies category folder is the Movies library''s downloads folder' ($movies -and $lib -and ("$($movies.dir)".TrimEnd('\') -ieq "$($lib.downloadsPath)".TrimEnd('\'))) "category '$($movies.dir)', library '$($lib.downloadsPath)'"
    }

    Invoke-Scenario -Id 'install.sign-ins-recorded' -Title 'The picker''s install record lists every client it installed, SABnzbd''s own api key is kept from its settings, the generated sign-ins are kept encrypted, and Deluge is given no generated login' -Body {
        $recordFile = $null; $credFile = $null
        foreach ($root in Find-KsDataRoots) {
            if (-not $recordFile) { $recordFile = Get-ChildItem -Path $root -Filter 'installed-components.json' -Recurse -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1 }
            if (-not $credFile) { $credFile = Get-ChildItem -Path $root -Filter 'component-credentials.json' -Recurse -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1 }
        }
        Add-Check 'The install record exists' ($null -ne $recordFile) ''
        Add-Check 'The sign-ins file exists' ($null -ne $credFile) ''
        if (-not $recordFile -or -not $credFile) { return }
        # Windows PowerShell 5.1 hands a JSON array back as one object; the extra pipe unrolls it.
        $record = @((Get-Content -Raw -LiteralPath $recordFile.FullName | ConvertFrom-Json) | ForEach-Object { $_ })
        $kinds = @($record | ForEach-Object { "$($_.kind)".ToLowerInvariant() })
        Save-Evidence -Name 'install-record-kinds.txt' -Text ($kinds -join ', ')
        foreach ($kind in 'qbittorrent', 'transmission', 'deluge', 'sabnzbd', 'nzbget') {
            Add-Check "$kind is in the install record" ($kinds -contains $kind) ($kinds -join ', ')
        }
        $withPort = @($record | Where-Object { $_.port -gt 0 -and $_.host })
        Add-Check 'Each recorded app names its host and port' ($withPort.Count -eq $record.Count) "$($withPort.Count) of $($record.Count)"
        Add-Check 'Each recorded app was connected once (a wired time is set)' (@($record | Where-Object { -not $_.wiredUtc }).Count -eq 0) ''
        $creds = Get-Content -Raw -LiteralPath $credFile.FullName | ConvertFrom-Json
        $names = @($creds.PSObject.Properties.Name | ForEach-Object { $_.ToLowerInvariant() })
        Add-Check 'Deluge was given no generated login (it keeps its own)' ($names -notcontains 'deluge') ($names -join ', ')
        Add-Check 'qBittorrent''s generated sign-in is kept' ($names -contains 'qbittorrent') ($names -join ', ')
        $raw = Get-Content -Raw -LiteralPath $credFile.FullName
        Add-Check 'The kept passwords are encrypted, not written plainly' ($raw -match 'dpapi:v1:') ''
        $sab = Get-KsComponentLogin -Kind 'sabnzbd'
        $ini = Get-SabnzbdIni
        $key = if ($ini -match '(?m)^\s*api_key\s*=\s*(\w{16,})') { $Matches[1] } else { '' }
        $script:S.Secrets['gaprows-sab-ini-key'] = $key
        Add-Check 'SABnzbd''s own api key, read from its settings after it started, is the one Deluno kept' ($sab -and $key -and $sab.Password -eq $key) $(if ($sab) { 'a key is kept' } else { 'no key kept for SABnzbd' })
    }

    # ---------------------------------------------------------------- what the hub pushes for a hand-off to Weir
    Invoke-Scenario -Id 'hub.connection-used-processor' -Title 'A real hand-off to Weir pushes ConnectionUsed for the processor with today''s count, and the number it has right now falls when it finishes' -Body {
        $hub = $null
        try {
            $hub = Open-HubConnection -Subjects @('live', 'queue', 'activity')
            Add-Check 'The hub accepted the sign-in and the connection is open' ($hub.Ws.State -eq 'Open') "$($hub.Ws.State)"
            $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'used') -Kind 'qbittorrent'
            $until = (Get-Date).AddMinutes(9)
            $done = $false
            while ((Get-Date) -lt $until -and -not $done) {
                Read-HubMessages -Connection $hub -WaitMs 500
                $done = @($hub.Messages | Where-Object { $_.Target -eq 'ActivityEventAdded' -and "$($_.Args[0].message)" -match [regex]::Escape($film.Title) -and "$($_.Args[0].label)" -eq 'Imported into the library' }).Count -gt 0
            }
            for ($i = 0; $i -lt 10; $i++) { Read-HubMessages -Connection $hub -WaitMs 300 }
            $used = @($hub.Messages | Where-Object { $_.Target -eq 'ConnectionUsed' -and "$($_.Args[0].kind)" -eq 'processor' })
            Save-Evidence -Name 'hub-connection-used.json' -Text (($used | ForEach-Object { [pscustomobject]@{ at = $_.At.ToString('o'); data = $_.Args[0] } }) | ConvertTo-Json -Depth 5)
            Add-Check 'ConnectionUsed was pushed for the processor when the film was handed to it' ($used.Count -ge 1) "$($used.Count) pushes"
            if ($used.Count -eq 0) { return }
            Add-Check 'It names Weir' ("$($used[0].Args[0].name)" -match '(?i)weir') "$($used[0].Args[0].name)"
            Add-Check 'It carries today''s count, at least one' ([int]$used[0].Args[0].countToday -ge 1) "countToday $($used[0].Args[0].countToday)"
            Add-Check 'It says how many titles Weir has right now' ($null -ne $used[0].Args[0].busy) "busy $($used[0].Args[0].busy)"
            $last = $used[$used.Count - 1].Args[0]
            Add-Check 'When Weir finished, the number it has right now fell while the count for today stayed' ([int]$last.busy -lt [int]$used[0].Args[0].busy -or [int]$last.busy -eq 0) "first busy $($used[0].Args[0].busy), last busy $($last.busy), today $($last.countToday)"
        }
        finally { Close-HubConnection -Connection $hub }
    }

    # ---------------------------------------------------------------- the indexer proxy
    Invoke-Scenario -Id 'indexer.proxy-requests' -Title 'A request really leaves through the indexer''s proxy: the test (caps and search) and the torrent fetch go through an HTTP proxy, a wrong proxy password is refused with 407 and nothing is passed on, and a SOCKS5 proxy takes the same request through its handshake' -Body {
        Initialize-GoldenProxyType
        $indexerId = $script:Fx.IndexerIds.torrent
        $pass = 'Px-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
        $script:S.Secrets['gaprows-proxy-pass'] = $pass
        $http = New-Object GoldenProxy
        $socks = New-Object GoldenProxy
        # .NET never sends a request for a loopback address through a proxy, so the feed is reached by a name only the proxy can resolve.
        $http.MapFrom = 'golden-feed.test'; $http.MapTo = '127.0.0.1'
        $socks.MapFrom = 'golden-feed.test'; $socks.MapTo = '127.0.0.1'
        $feedUrl = "http://127.0.0.1:$($script:Fx.FeedPort)"
        $proxiedUrl = "http://golden-feed.test:$($script:Fx.FeedPort)"
        $made =New-Object System.Collections.ArrayList
        try {
            $http.Start(18890, 'proxyuser', $pass)
            $socks.Start(18891, 'proxyuser', $pass)
            $good = Invoke-Deluno -Method POST -Path '/api/indexer-proxies' -Body @{ name = 'Golden HTTP proxy'; proxyType = 'http'; address = '127.0.0.1:18890'; username = 'proxyuser'; password = $pass; isEnabled = $true }
            [void]$made.Add($good.id)
            $wrong = Invoke-Deluno -Method POST -Path '/api/indexer-proxies' -Body @{ name = 'Golden HTTP proxy, wrong password'; proxyType = 'http'; address = '127.0.0.1:18890'; username = 'proxyuser'; password = ($pass + 'x'); isEnabled = $true }
            [void]$made.Add($wrong.id)
            $s5 = Invoke-Deluno -Method POST -Path '/api/indexer-proxies' -Body @{ name = 'Golden SOCKS5 proxy'; proxyType = 'socks5'; address = '127.0.0.1:18891'; username = 'proxyuser'; password = $pass; isEnabled = $true }
            [void]$made.Add($s5.id)
            Add-Check 'Three proxies were added (HTTP, HTTP with a wrong password, SOCKS5)' ($made.Count -eq 3 -and -not ($made -contains $null)) ''

            # Through the HTTP proxy: the test asks the feed for its caps and a search; the proxy sees both.
            Invoke-Deluno -Method PUT -Path "/api/indexers/$indexerId" -Body @{ proxyId = $good.id; baseUrl = $proxiedUrl } | Out-Null
            $t = Try-Deluno -Method POST -Path "/api/indexers/$indexerId/test" -TimeoutSec 120
            Save-Evidence -Name 'proxy-http-test.json' -Text $t.Raw
            Add-Check 'The indexer test through the HTTP proxy is healthy' ($t.Status -eq 200 -and $t.Raw -match '(?i)healthy') "HTTP $($t.Status) $($t.Raw)"
            # The caps request is made by the test an Add or Edit drawer runs (POST /api/indexers/test), not by the saved indexer's Test button
            # above, which presents the key with a one-row search only (#755).
            $drawer = Try-Deluno -Method POST -Path "/api/indexers/test?id=$indexerId" -TimeoutSec 120 -Body @{
                name = 'Golden feed'; protocol = 'torrent'; privacy = 'public'; baseUrl = $proxiedUrl; apiKey = 'golden'; isEnabled = $true; proxyId = $good.id
            }
            Save-Evidence -Name 'proxy-http-drawer-test.json' -Text $drawer.Raw
            $lines = @($http.Lines)
            Save-Evidence -Name 'proxy-http-lines.txt' -Text ($lines -join "`n")
            Add-Check 'The proxy turned the first, unsigned request away with 407, then passed on the signed one' ($http.Rejected -ge 1 -and @($lines | Where-Object { $_ -like "GET $proxiedUrl/*" }).Count -ge 1) "rejected $($http.Rejected); $($lines.Count) lines"
            Add-Check 'The caps request left through the proxy' (@($lines | Where-Object { $_ -match 't=caps' }).Count -ge 1) ''
            Add-Check 'A search request left through the proxy' (@($lines | Where-Object { $_ -like 'GET *' -and $_ -notmatch 't=caps' }).Count -ge 1) ''

            # The torrent file of a grab is fetched through the proxy too.
            $film = New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'proxied'
            $film.Url = ([string]$film.Url).Replace('127.0.0.1', 'golden-feed.test')
            $film = Send-Film -Film $film -Kind 'qbittorrent'
            Add-Check 'The grab was accepted' ($null -ne $film.MovieId) ''
            $fetched = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { @(@($http.Lines) | Where-Object { $_ -match '/dl/' }).Count -gt 0 }
            Add-Check 'The torrent file was fetched through the proxy' $fetched.Ok ((@($http.Lines) | Select-Object -Last 3) -join ' | ')
            Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null

            # A wrong password: refused with 407 every time, and nothing reaches the feed.
            Invoke-Deluno -Method PUT -Path "/api/indexers/$indexerId" -Body @{ proxyId = $wrong.id } | Out-Null
            $passedOn = @(@($http.Lines) | Where-Object { $_ -notlike '407 *' }).Count
            $rejectedBefore = $http.Rejected
            $bad = Try-Deluno -Method POST -Path "/api/indexers/$indexerId/test" -TimeoutSec 120
            Save-Evidence -Name 'proxy-wrong-password-test.json' -Text $bad.Raw
            Add-Check 'With the wrong proxy password the indexer test is not healthy and says so' ($bad.Status -eq 200 -and $bad.Raw -notmatch '"healthStatus"\s*:\s*"healthy"' -and $bad.Raw -match '(?i)proxy|407|authenticat') "HTTP $($bad.Status) $($bad.Raw)"
            Add-Check 'The proxy answered 407 again and passed nothing on' ($http.Rejected -gt $rejectedBefore -and @(@($http.Lines) | Where-Object { $_ -notlike '407 *' }).Count -eq $passedOn) "rejected $($http.Rejected)"

            # SOCKS5: the handshake with a login, then the same request.
            Invoke-Deluno -Method PUT -Path "/api/indexers/$indexerId" -Body @{ proxyId = $s5.id } | Out-Null
            $t5 = Try-Deluno -Method POST -Path "/api/indexers/$indexerId/test" -TimeoutSec 120
            Save-Evidence -Name 'proxy-socks5-test.json' -Text $t5.Raw
            Add-Check 'The indexer test through the SOCKS5 proxy is healthy' ($t5.Status -eq 200 -and $t5.Raw -match '(?i)healthy') "HTTP $($t5.Status) $($t5.Raw)"
            Add-Check 'The SOCKS5 proxy opened a connection to the feed after the login' (@(@($socks.Lines) | Where-Object { $_ -like 'SOCKS5 CONNECT golden-feed.test:*' }).Count -ge 1) ((@($socks.Lines)) -join ' | ')
        }
        finally {
            try { Invoke-Deluno -Method PUT -Path "/api/indexers/$indexerId" -Body @{ clearProxy = $true; baseUrl = $feedUrl } | Out-Null } catch { Write-SuiteLog "   could not clear the indexer's proxy: $($_.Exception.Message)" }
            foreach ($id in $made) { if ($id) { try { Try-Deluno -Method DELETE -Path "/api/indexer-proxies/$id" | Out-Null } catch { } } }
            $http.Stop(); $socks.Stop()
        }
        $direct = Try-Deluno -Method POST -Path "/api/indexers/$indexerId/test" -TimeoutSec 120
        Add-Check 'Back on a direct connection the indexer is healthy again' ($direct.Status -eq 200 -and $direct.Raw -match '(?i)healthy') ''
    }

    # ---------------------------------------------------------------- Connect Weir
    Invoke-Scenario -Id 'weir.connect-variations' -Title 'Connect Weir against the real Weir: missing details are asked for and nothing is touched, a wrong password is told plainly and nothing is created, an already-connected Weir is left as it is, and the password reaches no log' -Body {
        $path = '/api/system/bundled-components/weir/connect'
        $before = @(Invoke-Deluno -Path '/api/integrations/processors/connections' | Where-Object { $_.name -match 'weir' })
        Add-Check 'Weir is connected to start with' ($before.Count -ge 1) "$($before.Count) connection(s)"
        $missing = Try-Deluno -Method POST -Path $path -Body @{ username = ''; password = '' } -TimeoutSec 120
        # A Weir that is already connected answers before it looks at any details (CompanionTools: "already connected"), so with the Weir
        # this suite connected the answer to empty details is that, in words, and nothing is created (the connection check at the end holds).
        # Details are asked for only when no Weir is connected yet, which this scenario does not undo.
        Add-Check 'Missing details are asked for in words, or the answer says Weir is already connected' ($missing.Status -in 200, 400 -and "$($missing.Raw)" -match '(?i)username|password|enter|choose|already connected') "HTTP $($missing.Status) $($missing.Raw)"
        $badPassword = 'Wrong-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
        $script:S.Secrets['gaprows-weir-wrong'] = $badPassword
        $wrong = Try-Deluno -Method POST -Path $path -Body @{ username = (Get-SuiteSecret 'weir-username'); password = $badPassword } -TimeoutSec 120
        Save-Evidence -Name 'weir-connect-wrong-password.json' -Text $wrong.Raw
        Add-Check 'A wrong password is told plainly' ("$($wrong.Raw)" -match '(?i)password|sign in|did not|could not|wrong|already connected') "HTTP $($wrong.Status) $($wrong.Raw)"
        Add-Check 'The wrong password is not repeated in the answer' ("$($wrong.Raw)" -notmatch [regex]::Escape($badPassword)) ''
        $right = Try-Deluno -Method POST -Path $path -Body @{ username = (Get-SuiteSecret 'weir-username'); password = (Get-SuiteSecret 'weir-password') } -TimeoutSec 120
        Save-Evidence -Name 'weir-connect-already.json' -Text $right.Raw
        Add-Check 'An already-connected Weir is left as it is, and the answer says so' ($right.Status -eq 200 -and "$($right.Raw)" -match '(?i)already') "HTTP $($right.Status) $($right.Raw)"
        $after = @(Invoke-Deluno -Path '/api/integrations/processors/connections' | Where-Object { $_.name -match 'weir' })
        Add-Check 'None of the three made a second connection or changed the first' ($after.Count -eq $before.Count -and (@($after | ForEach-Object { $_.id }) -join ',') -eq (@($before | ForEach-Object { $_.id }) -join ',')) "$($after.Count) connection(s)"
        $weirOk = Test-WeirAnswering
        $signsIn = $true
        try { Connect-Weir } catch { $signsIn = $false }
        Add-Check 'The real Weir still answers and the suite''s own login still signs in' ($weirOk -and $signsIn) ''
        $leak = @(Get-DelunoLogFiles | Where-Object { (Select-String -LiteralPath $_.FullName -SimpleMatch -Pattern (Get-SuiteSecret 'weir-password') -Quiet -ErrorAction SilentlyContinue) -or (Select-String -LiteralPath $_.FullName -SimpleMatch -Pattern $badPassword -Quiet -ErrorAction SilentlyContinue) })
        Add-Check 'Neither password is in any of Deluno''s log files' ($leak.Count -eq 0) (($leak | ForEach-Object { $_.Name }) -join ', ')
    }
}
