# Records SABnzbd and NZBGet through their own APIs, downloading the golden path's Usenet stub film (#1237).
# Runs INSIDE the VM after Record.Lib.ps1. The stub (scripts/golden-path/usenet-stub) serves a real film as yEnc
# articles on 127.0.0.1, so a job downloads for real. Needs: -Params.SabnzbdApiKey (or the sabnzbd.ini that holds it),
# the clients' ports and logins, and the stub's Newznab port.

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
    throw 'SABnzbd API key not found. Pass -SabnzbdApiKey or -SabnzbdConfigPath.'
}

# ---------------------------------------------------------------- SABnzbd

function Invoke-RecordSabnzbd($p) {
    $key = if ($p.SabnzbdApiKey) { $p.SabnzbdApiKey } else { Find-SabApiKey $p.SabnzbdConfigPath }
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
        $kind = if ($storage -and (Test-Path -LiteralPath $storage -PathType Container)) { 'finished-folder' } elseif ($storage -and (Test-Path -LiteralPath $storage -PathType Leaf)) { 'finished-single-file' } else { 'finished' }
        Save-State sabnzbd $ver $kind "The film finished. SABnzbd's storage here is a $(if ($kind -eq 'finished-folder') { 'folder' } elseif ($kind -eq 'finished-single-file') { 'file' } else { 'path that is not on disk' })." @($history)
        if ($kind -eq 'finished-folder') { Add-Missing 'sabnzbd finished-single-file: needs a Usenet release that unpacks to one bare file (a second stub release from New-UsenetRelease.ps1)' }
        elseif ($kind -eq 'finished-single-file') { Add-Missing 'sabnzbd finished-folder: needs a Usenet release that unpacks to a folder (a second stub release from New-UsenetRelease.ps1)' }
        # removed with data, then twice
        $removeB = Api "mode=history&name=delete&value=$idB&del_files=1"
        Save-State sabnzbd $ver 'removed-with-data' 'The finished job removed from the history with del_files=1.' @($removeB, (Api 'mode=history'))
        $again = Api "mode=history&name=delete&value=$idB&del_files=1"
        Save-State sabnzbd $ver 'removed-twice' 'The same removal sent a second time.' @($again, (Api 'mode=history'))
    } else { Add-Missing 'sabnzbd finished / removed-with-data / removed-twice: the film did not finish in 25 minutes' }

    # failed: an address that does not serve an NZB, which SABnzbd reports as a failed fetch.
    $bad = [uri]::EscapeDataString('http://127.0.0.1:1/missing.nzb')
    $addBad = Api "mode=addurl&name=$bad&nzbname=Recorded.Failed"
    $null = Wait-Until -Seconds 60 { $h = Get-ReplyJson (Api 'mode=history'); @($h.history.slots) | Where-Object { $_.status -eq 'Failed' } }
    Save-State sabnzbd $ver 'failed' 'A job whose NZB address does not answer, left to fail.' @($addBad, (Api 'mode=queue'), (Api 'mode=history'))

    # not answering
    $none = Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path "/api?mode=queue&output=json&apikey=$key"
    Save-State sabnzbd $ver 'not-answering' 'The request sent to a port nothing listens on: the connection is refused.' @($none)

    Add-Missing 'sabnzbd stalled-near-end: needs a stub release with articles missing near the end, so the job sticks just short of 100%'
}

# ---------------------------------------------------------------- NZBGet

function Invoke-RecordNzbget($p) {
    $cred = New-Object pscredential($p.NzbgetUser, (ConvertTo-SecureString $p.NzbgetPassword -AsPlainText -Force))
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
        $kind = if ($dest -and (Test-Path -LiteralPath $dest -PathType Container)) { 'finished-folder' } else { 'finished' }
        Save-State nzbget $ver $kind "The film finished; NZBGet reports its folder in DestDir/FinalDir. The size fields in this reply are 32-bit halves (FileSizeHi/FileSizeLo)." @($history)
        Add-Missing 'nzbget finished-single-file: needs a Usenet release that unpacks to one bare file'
        $removeB = Rpc 'editqueue' @('HistoryFinalDelete', '', @($idB))
        Save-State nzbget $ver 'removed-with-data' 'The finished job removed from the history together with its files (HistoryFinalDelete).' @($removeB, (Rpc 'history' @($false)))
        $again = Rpc 'editqueue' @('HistoryFinalDelete', '', @($idB))
        Save-State nzbget $ver 'removed-twice' 'The same removal sent a second time.' @($again)
    } else { Add-Missing 'nzbget finished / removed-with-data / removed-twice: the film did not finish in 25 minutes' }

    $addBad = Rpc 'append' @('Recorded.Failed.nzb', 'http://127.0.0.1:1/missing.nzb', '', 0, $false, $false, '', 0, 'SCORE', @())
    $null = Wait-Until -Seconds 60 { $h = Get-ReplyJson (Rpc 'history' @($false)); @($h.result) | Where-Object { $_.Status -like 'FAILURE*' } }
    Save-State nzbget $ver 'failed' 'A job whose NZB address does not answer, left to fail.' @($addBad, (Rpc 'listgroups'), (Rpc 'history' @($false)))

    $none = Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/jsonrpc' -Json ([ordered]@{ method = 'version'; params = @(); id = 1 })
    Save-State nzbget $ver 'not-answering' 'The request sent to a port nothing listens on: the connection is refused.' @($none)
    Add-Missing 'nzbget stalled-near-end: needs a stub release with articles missing near the end'
}
