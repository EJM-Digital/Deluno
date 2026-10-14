# Records a real Torznab implementation (Jackett) and a real Newznab implementation (NZBHydra2) in the golden VM (#1237).
# Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1 and Install.Lib.ps1.
#
# Torznab: Jackett's own Windows build, from the project's GitHub release (the SHA-256 GitHub records for the asset is checked). Its
# Torznab endpoint answers t=caps, t=search (a feed with no term), a text search, t=movie and t=tvsearch with ids, and the errors a
# key or a function gets wrong. It is pointed at public trackers that need no account (YTS for films, EZTV for series, and the
# others tried in order if one is down or blocks the VM); what the run records is whatever they really answered that day.
#
# Newznab: NZBHydra2's own Windows build (its Windows build carries its own Java runtime) answers the Newznab
# API of its own. No Usenet indexer lets you search without an account, so it records caps, the empty search and the errors only;
# a real indexer's search results are listed in MISSING.md.

$script:PublicMovieIndexers = @('yts', 'thepiratebay', 'limetorrents', 'torrentgalaxy', 'kickasstorrents-ws')
$script:PublicTvIndexers = @('eztv', 'thepiratebay', 'limetorrents', 'torrentgalaxy', 'kickasstorrents-ws')

# Replaces the bytes of a downloaded .torrent in a recording by a sentence: a binary body does not belong in text.
function Reduce-BinaryReply($Exchange) {
    $type = [string]$Exchange.reply.headers['content-type']
    if ($Exchange.reply.status -eq 200 -and $type -match 'bittorrent|octet-stream') {
        $Exchange.reply.bodyText = "<$($Exchange.reply.bodyText.Length) characters of a torrent file, not kept>"
    }
    $Exchange
}

function Get-FirstXmlItems([string] $Xml) {
    try { @(([xml]$Xml).rss.channel.item) } catch { @() }
}

function Invoke-RecordJackett($p) {
    Assert-FreeSpace 3 'Jackett'
    Assert-Internet
    $dir = Get-AppFolder 'jackett'
    $asset = Get-GitHubAsset -Repo 'Jackett/Jackett' -NamePattern '^Jackett\.Binaries\.Windows\.zip$'
    $zip = Save-Verified -Url $asset.Url -OutFile (Join-Path $dir $asset.Name) -Sha256 $asset.Sha256
    $app = Join-Path $dir 'app'
    if (-not (Test-Path (Join-Path $app 'Jackett\JackettConsole.exe'))) { Expand-Fresh $zip $app }
    $exe = (Get-ChildItem -Path $app -Filter JackettConsole.exe -Recurse | Select-Object -First 1).FullName
    $data = Join-Path $dir 'data'; New-Item -ItemType Directory -Force -Path $data | Out-Null
    $port = $p.JackettPort
    Stop-AppByName 'JackettConsole'
    $proc = Start-AppProcess -File $exe -Arguments @('--DataFolder', $data, '--Port', "$port") -WorkingDirectory (Split-Path $exe) -LogName 'jackett.log'
    try {
        if (-not (Wait-Http -Url "http://127.0.0.1:$port/UI/Dashboard" -Seconds 180)) { Add-Missing "jackett: it did not answer on 127.0.0.1:$port after 3 minutes (see $(Get-AppFolder 'logs'))"; return }
        $configFile = Get-ChildItem -Path $data -Filter ServerConfig.json -Recurse | Select-Object -First 1
        $key = if ($configFile) { [string](Get-Content -Raw $configFile.FullName | ConvertFrom-Json).APIKey } else { $null }
        if (-not $key) { Add-Missing 'jackett: no API key in ServerConfig.json'; return }
        Register-Secret $key
        $c = New-RecordedClient -BaseUrl "http://127.0.0.1:$port"
        # With no admin password Jackett still wants its cookie: the login page sends the browser through /UI/TestCookie, which sets it.
        $next = '/UI/Login?ReturnUrl=/UI/Dashboard'
        for ($hop = 0; $hop -lt 5 -and $next; $hop++) {
            $step = Send-Recorded -Client $c -Path $next -KeepHeaders @('location', 'content-type')
            $next = [string]$step.reply.headers['location']
            if ($next -match '^https?://[^/]+(/.*)$') { $next = $Matches[1] } elseif ($next -and -not $next.StartsWith('/')) { $next = '/UI/' + $next }
            if ($next -like '/UI/Dashboard*') { break }
        }
        $serverConfig = Send-Recorded -Client $c -Path '/api/v2.0/server/config'
        $version = ([string](Get-ReplyJson $serverConfig).app_version).TrimStart('v')
        if (-not $version) { $version = $asset.Tag.TrimStart('v') }
        $dl = Get-DownloadNote
        Save-State jackett $version 'server-config' ('Jackett''s own configuration reply (the API key and any secret in it are removed).' + $dl) @($serverConfig)

        # Configure each public tracker by sending its own default configuration back to it, as the dashboard does for a tracker with no fields to fill in.
        $usable = @{}
        foreach ($id in @($script:PublicMovieIndexers + $script:PublicTvIndexers | Select-Object -Unique)) {
            $cfg = Send-Recorded -Client $c -Path "/api/v2.0/indexers/$id/config"
            if ($cfg.reply.status -ne 200) { Write-RunLog "jackett: $id has no definition ($($cfg.reply.status))"; continue }
            $set = Send-Recorded -Client $c -Method POST -Path "/api/v2.0/indexers/$id/config" -Body $cfg.reply.bodyText -ContentType 'application/json'
            $test = Send-Recorded -Client $c -Method POST -Path "/api/v2.0/indexers/$id/test"
            Write-RunLog "jackett: $id configured $($set.reply.status), test $($test.reply.status)"
            if ($test.reply.status -in 200, 204) { $usable[$id] = @($set, $test) }
        }
        if ($usable.Count -eq 0) { Add-Missing ('jackett: none of the public trackers (' + (($script:PublicMovieIndexers + $script:PublicTvIndexers | Select-Object -Unique) -join ', ') + ') passed its own test from the VM, so no search was recorded'); return }

        $torznab = { param($id, $query) Send-Recorded -Client $c -Path "/api/v2.0/indexers/$id/results/torznab/api?apikey=$key&$query" }.GetNewClosure()
        $movieId = @($script:PublicMovieIndexers | Where-Object { $usable.ContainsKey($_) })[0]
        $tvId = @($script:PublicTvIndexers | Where-Object { $usable.ContainsKey($_) })[0]
        $firstId = if ($movieId) { $movieId } else { $tvId }
        $note = { param($what, $id) "$what, from Jackett's Torznab endpoint for the public tracker '$id' (no account). Jackett $version." }.GetNewClosure()

        Save-State jackett $version 'caps' (& $note 'The capabilities (t=caps): server title, limits, search modes with their parameters, and the category tree' $firstId) @((& $torznab $firstId 't=caps'))
        if ($movieId -and $tvId -and $tvId -ne $movieId) { Save-State jackett $version 'caps-tv' (& $note 'The capabilities of a second tracker' $tvId) @((& $torznab $tvId 't=caps')) }
        Save-State jackett $version 'caps-all' 'The capabilities of Jackett''s aggregate indexer "all" (every configured tracker at once).' @((Send-Recorded -Client $c -Path "/api/v2.0/indexers/all/results/torznab/api?apikey=$key&t=caps"))
        Save-State jackett $version 'caps-wrong-api-key' 'The capabilities asked with a wrong API key.' @((Send-Recorded -Client $c -Path "/api/v2.0/indexers/$firstId/results/torznab/api?apikey=not-the-key&t=caps"))
        Save-State jackett $version 'caps-no-api-key' 'The capabilities asked with no API key.' @((Send-Recorded -Client $c -Path "/api/v2.0/indexers/$firstId/results/torznab/api?t=caps"))
        Save-State jackett $version 'unknown-indexer' 'A Torznab call for a tracker that is not configured.' @((Send-Recorded -Client $c -Path "/api/v2.0/indexers/not-a-tracker/results/torznab/api?apikey=$key&t=caps"))
        Save-State jackett $version 'unknown-function' 'A function the Torznab API does not have (t=bogus).' @((& $torznab $firstId 't=bogus'))
        Save-State jackett $version 'search-feed' (& $note 'RSS sync: t=search with no term and the movie and TV categories (Deluno''s feed request, ReadingAnIndexerFeedTests)' $firstId) @((& $torznab $firstId 't=search&cat=2000,5000&limit=50'))
        Save-State jackett $version 'search-no-results' (& $note 'A text search nothing matches' $firstId) @((& $torznab $firstId 't=search&q=zzqqxxnotarelease&cat=2000,5000'))

        if ($movieId) {
            $text = & $torznab $movieId 't=search&q=Sintel&cat=2000'
            Save-State jackett $version 'search-text' (& $note 'A text search for a film (Sintel, 2010) in the movie categories' $movieId) @($text)
            $byId = & $torznab $movieId 't=movie&imdbid=tt1727587&cat=2000'
            Save-State jackett $version 'search-movie-imdb' (& $note 'A movie search by IMDb id (Sintel, tt1727587)' $movieId) @($byId, (& $torznab $movieId 't=movie&q=Sintel&year=2010&cat=2000'))
            $first = @(Get-FirstXmlItems $text.reply.bodyText)[0]
            if ($first -and $first.link) {
                $link = [string]$first.link
                $path = ([Uri]$link).PathAndQuery
                $dlEx = Send-Recorded -Client $c -Path $path
                Save-State jackett $version 'download-link' (& $note 'The link of the first result followed (Jackett fetches the torrent from the tracker, or answers a redirect to a magnet link)' $movieId) @((Reduce-BinaryReply $dlEx))
            }
        }
        if ($tvId) {
            Save-State jackett $version 'search-tv' (& $note 'A TV search with season and episode (Breaking Bad S01E01)' $tvId) @((& $torznab $tvId 't=tvsearch&q=Breaking+Bad&season=1&ep=1&cat=5000'), (& $torznab $tvId 't=tvsearch&q=Breaking+Bad&cat=5000'))
        }
    } finally {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        Stop-AppByName 'JackettConsole'
    }
}

function Invoke-RecordNzbhydra($p) {
    Assert-FreeSpace 3 'NZBHydra2'
    Assert-Internet
    $dir = Get-AppFolder 'nzbhydra2'
    $asset = Get-GitHubAsset -Repo 'theotherp/nzbhydra2' -NamePattern '^nzbhydra2-[\d.]+-windows\.zip$'
    $zip = Save-Verified -Url $asset.Url -OutFile (Join-Path $dir $asset.Name) -Sha256 $asset.Sha256
    $app = Join-Path $dir 'app'
    if (-not (Test-Path $app)) { Expand-Fresh $zip $app }
    # NZBHydra2's Windows build is a native image that needs Microsoft's Visual C++ runtime (a clean server has none: exit code 0xC0000135).
    if (-not (Test-Path "$env:windir\System32\vcruntime140_1.dll")) {
        $vc = Save-Verified -Url 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile (Join-Path $dir 'vc_redist.x64.exe') -NoChecksumReason 'Microsoft lists none beside the download; the signature is checked'
        Assert-SignedBy $vc 'Microsoft Corporation'
        $null = Start-Process -FilePath $vc -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    }
    $core = (Get-ChildItem -Path $app -Filter 'core.exe' -Recurse | Select-Object -First 1).FullName
    if (-not $core) { throw 'The NZBHydra2 archive holds no core.exe' }
    $data = Join-Path $dir 'data'; New-Item -ItemType Directory -Force -Path $data | Out-Null
    $port = $p.NzbhydraPort
    $proc = Start-AppProcess -File $core -Arguments @('directstart', '--datafolder', $data, '--host', '127.0.0.1', '--port', "$port", '--nobrowser') -WorkingDirectory (Split-Path $core) -LogName 'nzbhydra2.log'
    try {
        if (-not (Wait-Http -Url "http://127.0.0.1:$port/" -Seconds 240)) { Add-Missing "nzbhydra2: it did not answer on 127.0.0.1:$port after 4 minutes (see $(Get-AppFolder 'logs'))"; return }
        # The configuration stores its API key obfuscated ({OBF}...), so a key of this run's own is written in its place: stop, edit, start again.
        $yml = Get-ChildItem -Path $data -Filter 'nzbhydra.yml' -Recurse | Select-Object -First 1
        if (-not $yml) { Add-Missing 'nzbhydra2: no nzbhydra.yml after the first start'; return }
        $key = (New-VmSecret 16)
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Stop-AppByName 'core'; Start-Sleep -Seconds 5
        $lines = Get-Content -LiteralPath $yml.FullName
        $mainAt = [array]::IndexOf($lines, 'main:')
        for ($i = $mainAt + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s+apiKey:') { $lines[$i] = '  apiKey: "' + $key + '"'; break } }
        [IO.File]::WriteAllLines($yml.FullName, $lines, (New-Object Text.UTF8Encoding($false)))
        $proc = Start-AppProcess -File $core -Arguments @('directstart', '--datafolder', $data, '--host', '127.0.0.1', '--port', "$port", '--nobrowser') -WorkingDirectory (Split-Path $core) -LogName 'nzbhydra2-2.log'
        if (-not (Wait-Http -Url "http://127.0.0.1:$port/" -Seconds 240)) { Add-Missing 'nzbhydra2: it did not come back after its API key was set'; return }

        Register-Secret $key
        $c = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -KeepHeaders @('content-type')
        $version = $asset.Tag.TrimStart('v')
        $dl = Get-DownloadNote
        $note = { param($what) "${what}: NZBHydra2 $version's own Newznab API with no indexer configured (every Usenet indexer needs an account)." + $dl }.GetNewClosure()
        Save-State nzbhydra2 $version 'caps' (& $note 't=caps') @((Send-Recorded -Client $c -Path "/api?t=caps&apikey=$key"))
        Save-State nzbhydra2 $version 'caps-wrong-api-key' (& $note 't=caps with a wrong API key') @((Send-Recorded -Client $c -Path '/api?t=caps&apikey=not-the-key'))
        Save-State nzbhydra2 $version 'caps-no-api-key' (& $note 't=caps with no API key') @((Send-Recorded -Client $c -Path '/api?t=caps'))
        Save-State nzbhydra2 $version 'search-feed' (& $note 'The feed request (t=search, no term, the movie and TV categories)') @((Send-Recorded -Client $c -Path "/api?t=search&cat=2000,5000&limit=50&apikey=$key"))
        Save-State nzbhydra2 $version 'search-text' (& $note 'A text search (Sintel)') @((Send-Recorded -Client $c -Path "/api?t=search&q=Sintel&cat=2000&apikey=$key"))
        Save-State nzbhydra2 $version 'search-movie-imdb' (& $note 'A movie search by IMDb id') @((Send-Recorded -Client $c -Path "/api?t=movie&imdbid=tt1727587&cat=2000&apikey=$key"))
        Save-State nzbhydra2 $version 'search-tv' (& $note 'A TV search with season and episode') @((Send-Recorded -Client $c -Path "/api?t=tvsearch&q=Breaking+Bad&season=1&ep=1&cat=5000&apikey=$key"))
        Save-State nzbhydra2 $version 'unknown-function' (& $note 'A function that does not exist (t=bogus)') @((Send-Recorded -Client $c -Path "/api?t=bogus&apikey=$key"))
        Add-Missing 'nzbhydra2: a real Usenet indexer''s search results, its NZB download (t=get) and its account errors need an account at an indexer; not recorded'
    } finally {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        Stop-AppByName 'core'
    }
}
