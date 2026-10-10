# Records SABnzbd and NZBGet through their own APIs, downloading the golden path's Usenet stub film (#1237).
# Runs INSIDE the VM after Record.Lib.ps1. The stub (scripts/golden-path/usenet-stub) serves a real film as yEnc
# articles on 127.0.0.1, so a job downloads for real. Needs: SABnzbd's sabnzbd.ini (the API key is read there, inside the VM),
# the clients' ports, and the stub's Newznab port.

# The NZB the stub's Newznab indexer offers for the film, as an address a client can fetch.
function Get-StubNzbUrl([int] $NewznabPort, [string] $Query = 'bunny') {
    $feed = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$NewznabPort/api?t=search&q=$Query&apikey=stub" -TimeoutSec 30
    $xml = [xml]$feed.Content
    $item = @($xml.SelectNodes('//item'))[0]
    if (-not $item) { throw "The Usenet stub's Newznab offered nothing for '$Query'. Is the stub running (Invoke-GoldenPath.ps1 -UsenetStub)?" }
    if ($item.enclosure -and $item.enclosure.url) { return [string]$item.enclosure.url }
    return [string]$item.link
}

function Find-SabApiKey([string] $ConfigPath) {
    $candidates = @($ConfigPath) + @(
        Get-ChildItem -Path $env:ProgramData, $env:LOCALAPPDATA, $env:APPDATA -Filter 'sabnzbd.ini' -Recurse -ErrorAction SilentlyContinue -Depth 4 | ForEach-Object FullName)
    foreach ($path in $candidates) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            $line = Select-String -LiteralPath $path -Pattern '^\s*api_key\s*=\s*(\S+)' | Select-Object -First 1
            if ($line) { return $line.Matches[0].Groups[1].Value }
        }
    }
    throw 'SABnzbd API key not found. Pass -SabnzbdConfigPath if sabnzbd.ini is somewhere unusual.'
}


# What a finished job left: one file, or a folder with several. A folder with one file in it is a single-file job.
function Get-FinishKind([string] $Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return 'finished' }
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return 'finished-single-file' }
    $count = @(Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue).Count
    if ($count -gt 1) { 'finished-folder' } else { 'finished-single-file' }
}

# Brings the VM's Usenet stub up to the version in this repository (the hang file, folder jobs), restarts the news
# server, and posts two more releases of the same real film into the stub's article folder: a folder job (the film and
# a small extra file, the #1199 case) and one whose last article the news server never answers (the Stuckfilm case of
# golden rc.30.19). Returns where their NZBs are, or $null (and a Missing line) when the stub is not there.
function Initialize-UsenetStubReleases($p) {
    if ($script:UsenetStub) { return $script:UsenetStub }
    $stubDir = 'C:\golden\usenet-stub'
    $task =Get-ScheduledTask -TaskName 'GoldenPathUsenetNntp' -ErrorAction SilentlyContinue
    if (-not $task -or -not (Test-Path $stubDir)) { Add-Missing 'usenet folder / stalled-near-end: the golden path''s Usenet stub is not in the VM (run Invoke-GoldenPath.ps1 -UsenetStub first)'; return $null }
    foreach ($f in $p.StubFiles) { Set-Content -LiteralPath (Join-Path $stubDir $f.name) -Value $f.text -Encoding utf8 }
    if ($task.Actions[0].Arguments -notmatch '-Root "([^"]+)"') { Add-Missing 'usenet folder / stalled-near-end: could not tell where the stub serves articles from'; return $null }
    $root = $Matches[1]
    Stop-ScheduledTask -TaskName 'GoldenPathUsenetNntp' -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Start-ScheduledTask -TaskName 'GoldenPathUsenetNntp'
    $greeting = Wait-Until -Seconds 60 { try { $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', [int]$p.NntpPort); $r = New-Object System.IO.StreamReader($c.GetStream()); $g = $r.ReadLine(); $c.Close(); if ($g -like '200*') { $g } } catch { } }
    if (-not $greeting) { Add-Missing 'usenet folder / stalled-near-end: the news server did not answer after it was restarted'; return $null }
    $made = @{}
    foreach ($spec in @(@{ Key = 'Folder'; Name = 'Recorded.Folder.Film.2008.1080p.WEB-DL.x264-GOLDEN'; Extra = 8 }, @{ Key = 'Stuck'; Name = 'Recorded.Stuck.Film.2008.1080p.WEB-DL.x264-GOLDEN'; Extra = 0 })) {
        $out = "C:\golden\recorded\usenet\$($spec.Key)"
        $info = & (Join-Path $stubDir 'New-UsenetRelease.ps1') -OutDirectory $out -Name $spec.Name -SourceFile $p.VmMediaFile -MinimumMegabytes 1 -ExtraKilobytes $spec.Extra
        Copy-Item -Path (Join-Path $out 'articles\*') -Destination (Join-Path $root 'articles') -Force
        $made[$spec.Key] = [pscustomobject]@{ Nzb = (Join-Path $out $info.nzb); Name = $spec.Name; LastArticle = @($info.files)[0].lastArticleId }
    }
    $made['Root'] = $root
    $script:UsenetStub = [pscustomobject]$made
    $script:UsenetStub
}

function Start-HangingArticle($stub) { Set-Content -LiteralPath (Join-Path $stub.Root 'hang.txt') -Value $stub.Stuck.LastArticle -Encoding ascii }
function Stop-HangingArticle($stub) { Remove-Item -LiteralPath (Join-Path $stub.Root 'hang.txt') -Force -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------- SABnzbd

function Invoke-RecordSabnzbd($p) {
    $key = Find-SabApiKey $p.SabnzbdConfigPath
    Register-Secret $key
    $sab = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.SabnzbdPort)"
    function Api($query) { Send-Recorded -Client $sab -Path "/api?$query&output=json&apikey=$key" }
    $nzbUrl = [uri]::EscapeDataString((Get-StubNzbUrl $p.NewznabPort))

    $v = Api 'mode=version'
    $versionText = (Get-ReplyJson $v).version
    if (-not $versionText) { Add-Missing 'sabnzbd: it did not answer mode=version'; return }
    $ver = $versionText
    Save-State sabnzbd $ver 'version' 'mode=version' @($v)
    Save-State sabnzbd $ver 'idle' 'Nothing queued: the queue and the history as SABnzbd answers when empty.' @((Api 'mode=queue'), (Api 'mode=history'))

    # queued: the queue paused, a job added, so it waits.
    $null = Api 'mode=pause'
    $add = Api "mode=addurl&name=$nzbUrl&nzbname=Recorded.Queued.A"
    $idA = @((Get-ReplyJson $add).nzo_ids)[0]
    Start-Sleep -Seconds 3
    Save-State sabnzbd $ver 'queued' 'The whole queue paused, then one job added by address: it waits in the queue.' @($add, (Api 'mode=queue'))

    # removed without data: that queued job removed, nothing downloaded to keep or delete.
    $removeA = Api "mode=queue&name=delete&value=$idA&del_files=0"
    Save-State sabnzbd $ver 'removed-without-data' 'The queued job removed from the queue with del_files=0.' @($removeA, (Api 'mode=queue'))

    # unknown id
    $unknown = Api 'mode=queue&name=delete&value=SABnzbd_nzo_doesnotexist&del_files=1'
    Save-State sabnzbd $ver 'unknown-id' 'A removal of a job id SABnzbd never had.' @($unknown, (Api 'mode=queue'))

    # the real job
    $addB = Api "mode=addurl&name=$nzbUrl&nzbname=Recorded.Film"
    $idB = @((Get-ReplyJson $addB).nzo_ids)[0]
    $null = Api 'mode=resume'
    $downloading = Wait-Until -Seconds 120 { $q = Get-ReplyJson (Api 'mode=queue'); $slot = @($q.queue.slots) | Where-Object { $_.nzo_id -eq $idB }; if ($slot -and $slot.status -eq 'Downloading' -and [double]$slot.percentage -gt 0) { $true } }
    if ($downloading) { Save-State sabnzbd $ver 'downloading' 'The film downloading from the stub.' @((Api 'mode=queue')) }
    else { Add-Missing 'sabnzbd downloading: the stub film never reached Downloading in 120 s' }

    $paused = Api "mode=queue&name=pause&value=$idB"
    Save-State sabnzbd $ver 'paused' 'The one job paused by its id while it downloads.' @($paused, (Api 'mode=queue'))
    $null = Api "mode=queue&name=resume&value=$idB"

    $done = Wait-Until -Seconds 1500 -EverySeconds 5 { $h = Get-ReplyJson (Api 'mode=history'); @($h.history.slots) | Where-Object { $_.nzo_id -eq $idB -and $_.status -in 'Completed', 'Failed' } }
    if ($done) {
        $history = Api 'mode=history'
        $storage = @((Get-ReplyJson $history).history.slots) | Where-Object { $_.nzo_id -eq $idB } | ForEach-Object { $_.storage }
        $kind = Get-FinishKind $storage
        Save-State sabnzbd $ver $kind "The one-file stub release finished. SABnzbd's storage is a $(if ($storage -and (Test-Path -LiteralPath $storage -PathType Leaf)) { 'file' } else { 'folder' })." @($history)
        # removed with data, then twice
        $removeB = Api "mode=history&name=delete&value=$idB&del_files=1"
        Save-State sabnzbd $ver 'removed-with-data' 'The finished job removed from the history with del_files=1.' @($removeB, (Api 'mode=history'))
        $again = Api "mode=history&name=delete&value=$idB&del_files=1"
        Save-State sabnzbd $ver 'removed-twice' 'The same removal sent a second time.' @($again, (Api 'mode=history'))
    } else { Add-Missing 'sabnzbd finished / removed-with-data / removed-twice: the film did not finish in 25 minutes' }

    # a folder job (the film plus a small extra file), then a job that sticks just short of the end.
    $stub = Initialize-UsenetStubReleases $p
    if ($stub) {
        $addF = Api ("mode=addlocalfile&name=" + [uri]::EscapeDataString($stub.Folder.Nzb) + "&nzbname=Recorded.Folder")
        $idF = @((Get-ReplyJson $addF).nzo_ids)[0]
        $doneF = Wait-Until -Seconds 1500 -EverySeconds 5 { $h = Get-ReplyJson (Api 'mode=history'); @($h.history.slots) | Where-Object { $_.nzo_id -eq $idF -and $_.status -in 'Completed', 'Failed' } }
        if ($doneF) {
            $historyF = Api 'mode=history'
            $storageF = @((Get-ReplyJson $historyF).history.slots) | Where-Object { $_.nzo_id -eq $idF } | ForEach-Object { $_.storage }
            Save-State sabnzbd $ver (Get-FinishKind $storageF) 'The folder job (film plus a small extra file) finished.' @($addF, $historyF)
            $null = Api "mode=history&name=delete&value=$idF&del_files=1"
        } else { Add-Missing 'sabnzbd finished-folder: the folder job did not finish in 25 minutes' }

        Start-HangingArticle $stub
        try {
            $addS = Api ("mode=addlocalfile&name=" + [uri]::EscapeDataString($stub.Stuck.Nzb) + "&nzbname=Recorded.Stuck")
            $idS = @((Get-ReplyJson $addS).nzo_ids)[0]
            $near = Wait-Until -Seconds 900 -EverySeconds 5 { $q = Get-ReplyJson (Api 'mode=queue'); $slot = @($q.queue.slots) | Where-Object { $_.nzo_id -eq $idS }; if ($slot -and [double]$slot.mbleft -lt 2 -and [double]$slot.percentage -ge 99) { $slot } }
            if ($near) {
                Start-Sleep -Seconds 40
                Save-State sabnzbd $ver 'stalled-near-end' 'A job whose last article the news server never answers: it sticks at 99% with a few KB left and never finishes.' @($addS, (Api 'mode=queue'), (Api 'mode=queue'))
            } else { Add-Missing 'sabnzbd stalled-near-end: the job never got within 2 MB of the end' }
            $null = Api "mode=queue&name=delete&value=$idS&del_files=1"
        } finally { Stop-HangingArticle $stub }
    }

    # failed: an address that does not serve an NZB, which SABnzbd reports as a failed fetch.
    $bad = [uri]::EscapeDataString('http://127.0.0.1:1/missing.nzb')
    $addBad = Api "mode=addurl&name=$bad&nzbname=Recorded.Failed"
    $null = Wait-Until -Seconds 60 { $h = Get-ReplyJson (Api 'mode=history'); @($h.history.slots) | Where-Object { $_.status -eq 'Failed' } }
    Save-State sabnzbd $ver 'failed' 'A job whose NZB address does not answer, left to fail.' @($addBad, (Api 'mode=queue'), (Api 'mode=history'))

    # not answering
    $none = Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path "/api?mode=queue&output=json&apikey=$key"
    Save-State sabnzbd $ver 'not-answering' 'The request sent to a port nothing listens on: the connection is refused.' @($none)

}

# ---------------------------------------------------------------- NZBGet

function Invoke-RecordNzbget($p) {
    # NZBGet keeps the login it ships with (build-rules.md); it is a public default, not a secret of this install.
    $cred = New-Object pscredential('nzbget', (ConvertTo-SecureString 'tegbzn6789' -AsPlainText -Force))
    $nzb = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.NzbgetPort)" -BasicAuth $cred
    $script:rpcId = 0
    function Rpc($method, $params = @()) { $script:rpcId++; Send-Recorded -Client $nzb -Method POST -Path '/jsonrpc' -Json ([ordered]@{ method = $method; params = @($params); id = $script:rpcId }) }
    $nzbUrl = Get-StubNzbUrl $p.NewznabPort

    $v = Rpc 'version'
    $ver = (Get-ReplyJson $v).result
    if (-not $ver) { Add-Missing 'nzbget: it did not answer version'; return }
    Save-State nzbget $ver 'version' 'JSON-RPC version' @($v)
    Save-State nzbget $ver 'idle' 'Nothing queued: listgroups and history when empty.' @((Rpc 'listgroups'), (Rpc 'history' @($false)))

    # append(NZBFilename, NZBContent (a URL is fetched), Category, Priority, AddToTop, AddPaused, DupeKey, DupeScore, DupeMode, PPParameters)
    $null = Rpc 'pausedownload'
    $addA = Rpc 'append' @('Recorded.Queued.A.nzb', $nzbUrl, '', 0, $false, $true, '', 0, 'SCORE', @())
    $idA = (Get-ReplyJson $addA).result
    Start-Sleep -Seconds 4
    Save-State nzbget $ver 'queued' 'The whole queue paused, then one job appended paused: it waits in the queue.' @($addA, (Rpc 'listgroups'))

    $removeA = Rpc 'editqueue' @('GroupFinalDelete', '', @([int]$idA))
    Save-State nzbget $ver 'removed-without-data' 'The queued job removed with GroupFinalDelete before anything downloaded.' @($removeA, (Rpc 'listgroups'), (Rpc 'history' @($false)))
    $unknown = Rpc 'editqueue' @('GroupFinalDelete', '', @(999999))
    Save-State nzbget $ver 'unknown-id' 'A removal of a job id NZBGet never had.' @($unknown)

    $addB = Rpc 'append' @('Recorded.Film.nzb', $nzbUrl, '', 0, $false, $false, '', 0, 'SCORE', @())
    $idB = [int](Get-ReplyJson $addB).result
    $null = Rpc 'resumedownload'
    $downloading = Wait-Until -Seconds 120 { $g = Get-ReplyJson (Rpc 'listgroups'); @($g.result) | Where-Object { $_.NZBID -eq $idB -and $_.Status -like 'DOWNLOADING*' } }
    if ($downloading) { Save-State nzbget $ver 'downloading' 'The film downloading from the stub.' @((Rpc 'listgroups')) }
    else { Add-Missing 'nzbget downloading: the stub film never reached DOWNLOADING in 120 s' }

    $paused = Rpc 'editqueue' @('GroupPause', '', @($idB))
    Save-State nzbget $ver 'paused' 'The one job paused by its id.' @($paused, (Rpc 'listgroups'))
    $null = Rpc 'editqueue' @('GroupResume', '', @($idB))

    $done = Wait-Until -Seconds 1500 -EverySeconds 5 { $h = Get-ReplyJson (Rpc 'history' @($false)); @($h.result) | Where-Object { $_.NZBID -eq $idB } }
    if ($done) {
        $history = Rpc 'history' @($false)
        $dest = @((Get-ReplyJson $history).result) | Where-Object { $_.NZBID -eq $idB } | ForEach-Object { $_.FinalDir; $_.DestDir } | Where-Object { $_ } | Select-Object -First 1
        $kind = Get-FinishKind $dest
        Save-State nzbget $ver $kind "The one-file stub release finished; NZBGet reports its folder in DestDir/FinalDir. The size fields in this reply are 32-bit halves (FileSizeHi/FileSizeLo)." @($history)
        $removeB = Rpc 'editqueue' @('HistoryFinalDelete', '', @($idB))
        Save-State nzbget $ver 'removed-with-data' 'The finished job removed from the history together with its files (HistoryFinalDelete).' @($removeB, (Rpc 'history' @($false)))
        $again = Rpc 'editqueue' @('HistoryFinalDelete', '', @($idB))
        Save-State nzbget $ver 'removed-twice' 'The same removal sent a second time.' @($again)
    } else { Add-Missing 'nzbget finished / removed-with-data / removed-twice: the film did not finish in 25 minutes' }

    $stub = Initialize-UsenetStubReleases $p
    if ($stub) {
        $b64F = [Convert]::ToBase64String([IO.File]::ReadAllBytes($stub.Folder.Nzb))
        $addF = Rpc 'append' @('Recorded.Folder.nzb', $b64F, '', 0, $false, $false, '', 0, 'SCORE', @())
        $idF = [int](Get-ReplyJson $addF).result
        $doneF = Wait-Until -Seconds 1500 -EverySeconds 5 { $h = Get-ReplyJson (Rpc 'history' @($false)); @($h.result) | Where-Object { $_.NZBID -eq $idF } }
        if ($doneF) {
            $historyF = Rpc 'history' @($false)
            $destF = @((Get-ReplyJson $historyF).result) | Where-Object { $_.NZBID -eq $idF } | ForEach-Object { $_.FinalDir; $_.DestDir } | Where-Object { $_ } | Select-Object -First 1
            Save-State nzbget $ver (Get-FinishKind $destF) 'The folder job (film plus a small extra file) finished.' @($addF, $historyF)
            $null = Rpc 'editqueue' @('HistoryFinalDelete', '', @($idF))
        } else { Add-Missing 'nzbget finished-folder: the folder job did not finish in 25 minutes' }

        Start-HangingArticle $stub
        try {
            $b64S = [Convert]::ToBase64String([IO.File]::ReadAllBytes($stub.Stuck.Nzb))
            $addS = Rpc 'append' @('Recorded.Stuck.nzb', $b64S, '', 0, $false, $false, '', 0, 'SCORE', @())
            $idS = [int](Get-ReplyJson $addS).result
            $near = Wait-Until -Seconds 900 -EverySeconds 5 { $g = Get-ReplyJson (Rpc 'listgroups'); @($g.result) | Where-Object { $_.NZBID -eq $idS -and [double]$_.RemainingSizeMB -lt 2 -and [double]$_.DownloadedSizeMB -gt 1 } }
            if ($near) {
                Start-Sleep -Seconds 40
                Save-State nzbget $ver 'stalled-near-end' 'A job whose last article the news server never answers: it sticks with a few KB left and never finishes.' @($addS, (Rpc 'listgroups'), (Rpc 'listgroups'))
            } else { Add-Missing 'nzbget stalled-near-end: the job never got within 2 MB of the end' }
            $null = Rpc 'editqueue' @('GroupFinalDelete', '', @($idS))
        } finally { Stop-HangingArticle $stub }
    }

    $addBad = Rpc 'append' @('Recorded.Failed.nzb', 'http://127.0.0.1:1/missing.nzb', '', 0, $false, $false, '', 0, 'SCORE', @())
    $null = Wait-Until -Seconds 60 { $h = Get-ReplyJson (Rpc 'history' @($false)); @($h.result) | Where-Object { $_.Status -like 'FAILURE*' } }
    Save-State nzbget $ver 'failed' 'A job whose NZB address does not answer, left to fail.' @($addBad, (Rpc 'listgroups'), (Rpc 'history' @($false)))

    $none = Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/jsonrpc' -Json ([ordered]@{ method = 'version'; params = @(); id = 1 })
    Save-State nzbget $ver 'not-answering' 'The request sent to a port nothing listens on: the connection is refused.' @($none)
}
