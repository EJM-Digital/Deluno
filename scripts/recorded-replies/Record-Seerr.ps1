# Builds Seerr (seerr-team/seerr) from its release source in the golden VM, with no Docker, runs it on 127.0.0.1, signs it in to the
# Jellyfin the media-server script left in the VM, and records the replies Deluno's Seerr client reads (SeerrClientTests,
# ConnectionVersionParserTests): /api/v1/status, settings/main, the request list in every state, users, media, availability and
# the errors (#1237). Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1, Install.Lib.ps1 and Record-MediaServers.ps1
# (run `-App jellyfin` first: Seerr's first sign-in is a media server's own account).
#
# Seerr has no Windows release, only a Node.js server built from source. This follows its own documented route: Node 22 from nodejs.org
# (SHASUMS256.txt checked), pnpm from the npm registry at the version the project's package.json pins, the release tag's source from
# GitHub (the project publishes no checksum for a source archive; the tag is fetched over TLS from GitHub's API), then
# `pnpm install --frozen-lockfile`, `pnpm build` and `pnpm start`. The build needs about 4 GB of memory and several minutes.
# If a native module has no prebuilt Windows binary for this Node, the install stops and the run says Seerr cannot be built here
# (it is then listed in MISSING.md rather than worked around).
#
# Seerr asks TMDB for the titles of a request with its own built-in key. No account of any kind is made: the one user is the
# Jellyfin administrator this run made in the VM.

function Install-SeerrBuild($p) {
    $ErrorActionPreference = 'Continue'   # npm and pnpm write notices to stderr; their exit codes are what is checked
    $dir = Get-AppFolder 'seerr'
    $nodeSums = (Invoke-WebRequest -Uri 'https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt' -UseBasicParsing).Content
    if ($nodeSums -is [byte[]]) { $nodeSums = [Text.Encoding]::ASCII.GetString($nodeSums) }
    $nodeZipName = ([regex]::Match($nodeSums, 'node-v[\d.]+-win-x64\.zip')).Value
    if (-not $nodeZipName) { throw 'nodejs.org lists no Windows x64 zip for Node 22' }
    $nodeZip = Save-Verified -Url "https://nodejs.org/dist/latest-v22.x/$nodeZipName" -OutFile (Join-Path $dir $nodeZipName) -ChecksumUrl 'https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt' -ChecksumName $nodeZipName
    $node = Join-Path $dir 'node'
    if (-not (Test-Path $node)) { Expand-Fresh $nodeZip $node }
    $nodeHome = (Get-ChildItem -Path $node -Directory | Select-Object -First 1).FullName
    $env:Path = "$nodeHome;$nodeHome\node_modules\.bin;$env:Path"

    # The newest release tag from the project's own release page redirect (no API call, so no rate limit), and its source archive from the same site.
    $req = [Net.HttpWebRequest]::Create('https://github.com/seerr-team/seerr/releases/latest'); $req.AllowAutoRedirect = $false; $req.UserAgent = 'deluno-recorder'
    $res = $req.GetResponse(); $tag = ([string]$res.Headers['Location'] -split '/')[-1]; $res.Close()
    if ($tag -notmatch '^v[0-9.]+') { throw "Could not read Seerr's newest release tag ($tag)" }
    $src = Join-Path $dir "src-$tag"
    if (-not (Test-Path (Join-Path $src 'package.json'))) {
        $zip = Join-Path $dir "seerr-$tag.zip"
        if (-not (Test-Path $zip)) { Invoke-WebRequest -Uri "https://github.com/seerr-team/seerr/archive/refs/tags/$tag.zip" -OutFile $zip -UseBasicParsing }
        $script:Downloads.Add("seerr-$tag.zip from github.com: the project publishes no checksum for a source archive; SHA-256 $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())")
        $tmp = Join-Path $dir "unpack-$tag"; Expand-Fresh $zip $tmp
        Move-Item -LiteralPath (Get-ChildItem -Path $tmp -Directory | Select-Object -First 1).FullName -Destination $src
    }
    $package = Get-Content -Raw (Join-Path $src 'package.json') | ConvertFrom-Json
    $pnpmVersion = ([string]$package.packageManager -replace '^pnpm@', '' -replace '\+.*$', '')
    if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
        & npm.cmd install -g "pnpm@$pnpmVersion" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "npm could not install pnpm@$pnpmVersion" }
    }
    Push-Location $src
    try {
        if (-not (Test-Path (Join-Path $src '.next'))) {
            $env:NODE_OPTIONS = '--max-old-space-size=4096'
            Write-RunLog 'seerr: pnpm install'
            & pnpm.cmd install --frozen-lockfile 2>&1 | Out-File (Join-Path (Get-AppFolder 'logs') 'seerr-install.log')
            if ($LASTEXITCODE -ne 0) { throw 'pnpm install failed; Seerr cannot be built in this VM (see seerr-install.log)' }
            Write-RunLog 'seerr: pnpm build'
            & pnpm.cmd build 2>&1 | Out-File (Join-Path (Get-AppFolder 'logs') 'seerr-build.log')
            if ($LASTEXITCODE -ne 0) { throw 'pnpm build failed; Seerr cannot be built in this VM (see seerr-build.log)' }
        }
    } finally { Pop-Location }
    [pscustomobject]@{ Src = $src; Tag = $tag.TrimStart('v'); NodeHome = $nodeHome }
}

function Invoke-RecordSeerr($p) {
    Assert-FreeSpace 10 'Seerr'
    Assert-Internet
    $jfName = Read-VmSecret 'jellyfin-admin-name'; $jfPass = Read-VmSecret 'jellyfin-admin-password'
    if (-not $jfName) { Add-Missing 'seerr: no Jellyfin login in this VM; run the jellyfin recording first (Seerr signs in with a media server account)'; return }
    if (-not (Get-Service -Name 'JellyfinServer' -ErrorAction SilentlyContinue)) { Add-Missing 'seerr: Jellyfin is not installed in this VM'; return }
    Start-Service -Name 'JellyfinServer' -ErrorAction SilentlyContinue
    $build = Install-SeerrBuild $p
    $port = $p.SeerrPort
    $config = Join-Path (Get-AppFolder 'seerr') 'config'; New-Item -ItemType Directory -Force -Path $config | Out-Null
    $proc = Start-AppProcess -File "$($build.NodeHome)\node.exe" -Arguments @('dist/index.js') -WorkingDirectory $build.Src -LogName 'seerr.log' `
        -Environment @{ NODE_ENV = 'production'; PORT = "$port"; HOST = '127.0.0.1'; CONFIG_DIRECTORY = $config }
    try {
        if (-not (Wait-Http -Url "http://127.0.0.1:$port/api/v1/status" -Seconds 180 -Status 200)) { Add-Missing "seerr: it did not answer on 127.0.0.1:$port after 3 minutes (see $(Get-AppFolder 'logs'))"; return }
        $c = New-RecordedClient -BaseUrl "http://127.0.0.1:$port"
        $status = Send-Recorded -Client $c -Path '/api/v1/status'
        $version = [string](Get-ReplyJson $status).version
        if (-not $version) { $version = $build.Tag }
        $dl = Get-DownloadNote
        $n = { param($what) "$what (Seerr $version built from source, signed in to the VM's Jellyfin, TMDB titles from Seerr's own key)." + $dl }.GetNewClosure()
        Save-State seerr $version 'status' (& $n '/api/v1/status: the version, whether an update is available, whether a restart is needed (ConnectionVersionParserTests)') @($status)

        # First sign-in: the Jellyfin administrator becomes Seerr's owner and links the media server.
        $signin = Send-Recorded -Client $c -Method POST -Path '/api/v1/auth/jellyfin' -Json ([ordered]@{ username = $jfName; password = $jfPass; hostname = '127.0.0.1'; port = $p.JellyfinPort; useSsl = $false; urlBase = ''; email = 'recorder@example.test'; serverType = 2 })
        if ($signin.reply.status -notin 200, 201) {
            Save-State seerr $version 'sign-in-refused' (& $n 'The first sign-in with the Jellyfin administrator (refused here)') @($signin)
            Add-Missing "seerr: the first sign-in with the Jellyfin administrator answered $($signin.reply.status); Seerr's setup flow changed or Jellyfin is not reachable from it. Nothing after it was recorded"
            return
        }
        $libs = Send-Recorded -Client $c -Path '/api/v1/settings/jellyfin/library?sync=true'
        $libList = @(Get-ReplyJson $libs)
        $enable = $null
        if ($libList.Count -gt 0 -and $libList[0].id) { $enable = Send-Recorded -Client $c -Path ("/api/v1/settings/jellyfin/library?enable=" + (($libList | ForEach-Object { $_.id }) -join ',')) }
        $init = Send-Recorded -Client $c -Method POST -Path '/api/v1/settings/initialize'
        $main = Send-Recorded -Client $c -Path '/api/v1/settings/main'
        $key = [string](Get-ReplyJson $main).apiKey
        if (-not $key) { Add-Missing 'seerr: settings/main carried no apiKey after setup'; return }
        Register-Secret $key
        Save-State seerr $version 'setup' (& $n 'The first sign-in with the Jellyfin administrator, the library sync and enable, the initialize call and the main settings (the API key and the session are removed)') @(@($signin, $libs, $enable, $init, $main) | Where-Object { $_ })
        $k = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -DefaultHeaders @{ 'X-Api-Key' = $key }
        $bad = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -DefaultHeaders @{ 'X-Api-Key' = 'not-the-key' }
        $get = { param($path) Send-Recorded -Client $k -Path $path }.GetNewClosure()
        Save-State seerr $version 'wrong-api-key' (& $n 'The request list, the user list and settings with a key Seerr does not know, and with no key') @(
            (Send-Recorded -Client $bad -Path '/api/v1/request?filter=all&sort=added'), (Send-Recorded -Client $bad -Path '/api/v1/user?sort=displayname'),
            (Send-Recorded -Client (New-RecordedClient -BaseUrl "http://127.0.0.1:$port") -Path '/api/v1/settings/main'))
        Save-State seerr $version 'requests-empty' (& $n 'No request yet: the lists Deluno reads') @((& $get '/api/v1/request?filter=all&sort=added'), (& $get '/api/v1/media?filter=all&sort=added'), (& $get '/api/v1/user?sort=displayname'), (& $get '/api/v1/settings/main'))

        # Requests in each state: a film (Sintel, TMDB 45745) pending then approved, a series (Breaking Bad, TMDB 1396, season 1) declined.
        $movie = Send-Recorded -Client $k -Method POST -Path '/api/v1/request' -Json ([ordered]@{ mediaType = 'movie'; mediaId = 45745 })
        $movieRequest = Get-ReplyJson $movie
        $tv = Send-Recorded -Client $k -Method POST -Path '/api/v1/request' -Json ([ordered]@{ mediaType = 'tv'; mediaId = 1396; seasons = @(1) })
        $tvRequest = Get-ReplyJson $tv
        Save-State seerr $version 'requests-made' (& $n 'A film request and a series request made as the owner (an owner''s requests are approved at once unless auto-approve is off), then the lists') @($movie, $tv, (& $get '/api/v1/request?filter=all&sort=added'), (& $get '/api/v1/request?filter=pending&sort=added'), (& $get '/api/v1/media?filter=all&sort=added'))
        if ($tvRequest.id) {
            $decline = Send-Recorded -Client $k -Method POST -Path "/api/v1/request/$($tvRequest.id)/decline"
            Save-State seerr $version 'request-declined' (& $n 'The series request declined, then the list') @($decline, (& $get '/api/v1/request?filter=all&sort=added'), (& $get '/api/v1/request?filter=declined&sort=added'))
        }
        if ($movieRequest.id) {
            Save-State seerr $version 'request-approved' (& $n 'The film request read by id, approved (a no-op if it already was), then the list filtered to approved and to processing') @(
                (& $get "/api/v1/request/$($movieRequest.id)"), (Send-Recorded -Client $k -Method POST -Path "/api/v1/request/$($movieRequest.id)/approve"), (& $get '/api/v1/request?filter=approved&sort=added'), (& $get '/api/v1/request?filter=processing&sort=added'))
            $mediaId = [string]$movieRequest.media.id
            if ($mediaId) {
                Save-State seerr $version 'media-available' (& $n 'The film marked available (POST /media/{id}/available), then the media and request lists') @(
                    (Send-Recorded -Client $k -Method POST -Path "/api/v1/media/$mediaId/available" -Json ([ordered]@{ is4k = $false })), (& $get "/api/v1/media/$mediaId"), (& $get '/api/v1/media?filter=available&sort=added'), (& $get '/api/v1/request?filter=available&sort=added'))
                Save-State seerr $version 'media-deleted' (& $n 'The media record deleted (DELETE /media/{id}), deleted again, then read') @(
                    (Send-Recorded -Client $k -Method DELETE -Path "/api/v1/media/$mediaId"), (Send-Recorded -Client $k -Method DELETE -Path "/api/v1/media/$mediaId"), (& $get "/api/v1/media/$mediaId"))
            }
        }
        Save-State seerr $version 'request-unknown' (& $n 'A request, an approval and a media record that do not exist') @((& $get '/api/v1/request/999999'), (Send-Recorded -Client $k -Method POST -Path '/api/v1/request/999999/approve'), (& $get '/api/v1/media/999999'))
        Save-State seerr $version 'request-refused' (& $n 'A request for a title TMDB does not have, and one with a media type Seerr does not know') @(
            (Send-Recorded -Client $k -Method POST -Path '/api/v1/request' -Json ([ordered]@{ mediaType = 'movie'; mediaId = 1 })), (Send-Recorded -Client $k -Method POST -Path '/api/v1/request' -Json ([ordered]@{ mediaType = 'book'; mediaId = 1 })))
        Save-State seerr $version 'users' (& $n 'The user list and the main settings') @((& $get '/api/v1/user?sort=displayname'), (& $get '/api/v1/settings/main'))
        Save-State seerr $version 'not-answering' 'A request sent to a port nothing listens on: the connection is refused.' @((Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path '/api/v1/status'))
        Add-Missing 'seerr: requests that come from a Radarr or Sonarr being configured (service lists, quality profiles) and a Plex-linked Seerr are not recorded: Deluno replaces those tools, and Plex needs an account'
    } finally {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        Stop-AppByName 'node'
        Stop-Service -Name 'JellyfinServer' -Force -ErrorAction SilentlyContinue
    }
}
