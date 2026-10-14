# Installs Jellyfin, Emby and Plex Media Server in the golden VM (one at a time), gives each a small real library (the suite's base
# film as a movie, and the same video as a two-episode series), and records the replies Deluno's media server clients read (#1237):
# MediaServerClientTests, ConnectionVersionParserTests and the refresh, collection, watched and delete paths of
# EmbyFamilyMediaServerClient.cs and PlexMediaServerClient.cs. Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1 and Install.Lib.ps1.
#
# Where each comes from, and how it is checked:
#   Jellyfin  the project's own Windows installer (repo.jellyfin.org). The project publishes no checksum next to it, so the file's
#             SHA-256 goes in each recording's note. Run as its service, bound to 127.0.0.1 by its network.xml.
#   Emby      the project's own GitHub release (MediaBrowser/Emby.Releases), the Windows x64 .7z, SHA-256 from GitHub's asset digest;
#             unpacked with 7-Zip's own 7zr.exe.
#   Plex      Plex's own download feed (plex.tv/api/downloads/5.json), which gives the address and the SHA-1 of the file.
# Logins are made in the VM and kept DPAPI-protected (Save-VmSecret), so Seerr's script can sign in to Jellyfin without any value ever
# passing through a command line or the console.
#
# Plex needs an account to be CLAIMED by its owner; no account is made here. An unclaimed Plex accepts local calls without a
# token, so the identity, library, item, section and playback calls are recorded that way; whatever then needs plex.tv (the
# user list, the resources list, a token a person signed in with) goes in MISSING.md.

$script:MediaClientAuth = 'Client="Deluno Recorder", Device="Recorder", DeviceId="deluno-recorder-1", Version="1.0"'

function Get-EmbyFamilySetup([string] $Kind, $p) {
    if ($Kind -eq 'jellyfin') {
        [pscustomobject]@{ Kind = 'jellyfin'; Prefix = ''; Port = $p.JellyfinPort; UserHeader = 'Authorization'; AuthPrefix = 'MediaBrowser ' }
    } else {
        [pscustomobject]@{ Kind = 'emby'; Prefix = '/emby'; Port = $p.EmbyPort; UserHeader = 'X-Emby-Authorization'; AuthPrefix = 'MediaBrowser ' }
    }
}

function Install-Jellyfin($p) {
    $dir = Get-AppFolder 'jellyfin'
    $listing = (Invoke-WebRequest -Uri 'https://repo.jellyfin.org/files/server/windows/latest-stable/amd64/' -UseBasicParsing).Content
    $name = ([regex]::Matches($listing, 'jellyfin_[\d.]+_windows-x64\.exe') | Select-Object -First 1).Value
    if (-not $name) { throw 'repo.jellyfin.org lists no Windows installer in latest-stable/amd64' }
    $setup = Save-Verified -Url "https://repo.jellyfin.org/files/server/windows/latest-stable/amd64/$name" -OutFile (Join-Path $dir $name) -NoChecksumReason 'the project publishes none beside the installer'
    $service = Get-Service -Name 'JellyfinServer' -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-RunLog 'installing Jellyfin (silent)'
        $proc = Start-Process -FilePath $setup -ArgumentList '/S' -PassThru -Wait
        $service = Get-Service -Name 'JellyfinServer' -ErrorAction SilentlyContinue
        if (-not $service) { throw "The Jellyfin installer finished (exit code $($proc.ExitCode)) but there is no JellyfinServer service; install it by hand in the VM and run this again." }
    }
    Stop-Service -Name 'JellyfinServer' -Force -ErrorAction SilentlyContinue
    Stop-AppByName 'jellyfin'
    $config = Join-Path $env:ProgramData 'Jellyfin\Server\config'
    New-Item -ItemType Directory -Force -Path $config | Out-Null
    $network = Join-Path $config 'network.xml'
    if (-not (Test-Path $network)) {
        @'
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <InternalHttpPort>8096</InternalHttpPort>
  <PublicHttpPort>8096</PublicHttpPort>
  <EnableRemoteAccess>false</EnableRemoteAccess>
</NetworkConfiguration>
'@ | Set-Content -LiteralPath $network -Encoding utf8
    }
    Start-Service -Name 'JellyfinServer'
}

function Install-Emby($p) {
    $dir = Get-AppFolder 'emby'
    $asset = Get-GitHubAsset -Repo 'MediaBrowser/Emby.Releases' -NamePattern '^embyserver-win-x64-[\d.]+\.7z$'
    $archive = Save-Verified -Url $asset.Url -OutFile (Join-Path $dir $asset.Name) -Sha256 $asset.Sha256
    $app = Join-Path $dir 'app'
    if (-not (Test-Path $app)) {
        New-Item -ItemType Directory -Force -Path $app | Out-Null
        $seven = Get-SevenZip
        & $seven x $archive "-o$app" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7zr could not unpack the Emby archive (exit code $LASTEXITCODE)" }
    }
    $exe = (Get-ChildItem -Path $app -Filter 'EmbyServer.exe' -Recurse | Select-Object -First 1).FullName
    if (-not $exe) { throw 'The Emby archive holds no EmbyServer.exe' }
    $data = Join-Path $dir 'data'
    $cfgDir = Join-Path $data 'config'; New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $system = Join-Path $cfgDir 'system.xml'
    if (-not (Test-Path $system)) {
        "<?xml version=`"1.0`"?><ServerConfiguration><HttpServerPortNumber>$($p.EmbyPort)</HttpServerPortNumber><PublicPort>$($p.EmbyPort)</PublicPort><EnableUPnP>false</EnableUPnP><IsStartupWizardCompleted>false</IsStartupWizardCompleted></ServerConfiguration>" | Set-Content -LiteralPath $system -Encoding utf8
    }
    Stop-AppByName 'EmbyServer'
    Start-AppProcess -File $exe -Arguments @('-programdata', $data, '-noautorunwebapp') -WorkingDirectory (Split-Path $exe) -LogName 'emby.log'
}

# The setup wizard of a fresh Jellyfin or Emby, then a login. Returns the admin's login (kept DPAPI-protected for later runs).
function Complete-EmbyFamilyWizard($S, $client) {
    $pfx = $S.Prefix
    $name = Read-VmSecret "$($S.Kind)-admin-name"
    $pass = Read-VmSecret "$($S.Kind)-admin-password"
    $wizard = [System.Collections.Generic.List[object]]::new()
    $state = Send-Recorded -Client $client -Path "$pfx/System/Info/Public"
    $done = (Get-ReplyJson $state).StartupWizardCompleted
    if (-not $name) { $name = 'rec' + (New-VmSecret 4); $pass = New-VmSecret 12; Save-VmSecret "$($S.Kind)-admin-name" $name; Save-VmSecret "$($S.Kind)-admin-password" $pass }
    if ($done -ne $true) {
        $wizard.Add((Send-Recorded -Client $client -Path "$pfx/Startup/Configuration"))
        $wizard.Add((Send-Recorded -Client $client -Method POST -Path "$pfx/Startup/Configuration" -Json ([ordered]@{ UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en' })))
        $wizard.Add((Send-Recorded -Client $client -Path "$pfx/Startup/User"))
        $wizard.Add((Send-Recorded -Client $client -Method POST -Path "$pfx/Startup/User" -Json ([ordered]@{ Name = $name; Password = $pass })))
        $wizard.Add((Send-Recorded -Client $client -Method POST -Path "$pfx/Startup/RemoteAccess" -Json ([ordered]@{ EnableRemoteAccess = $false; EnableAutomaticPortMapping = $false })))
        $wizard.Add((Send-Recorded -Client $client -Method POST -Path "$pfx/Startup/Complete"))
    }
    [pscustomobject]@{ Name = $name; Password = $pass; Wizard = @($wizard); StateBefore = $state }
}

function Get-EmbyToken($S, $client, [string] $Name, [string] $Password) {
    $auth = Send-Recorded -Client $client -Method POST -Path "$($S.Prefix)/Users/AuthenticateByName" -Headers @{ $S.UserHeader = $S.AuthPrefix + $script:MediaClientAuth } -Json ([ordered]@{ Username = $Name; Pw = $Password })
    $j = Get-ReplyJson $auth
    if ($j.AccessToken) { Register-Secret ([string]$j.AccessToken) }
    [pscustomobject]@{ Exchange = $auth; Token = [string]$j.AccessToken; UserId = [string]$j.User.Id }
}

function Invoke-RecordEmbyFamily($p, [string] $Kind) {
    Assert-FreeSpace 8 $Kind
    Assert-Internet
    $S = Get-EmbyFamilySetup $Kind $p
    $media = New-MediaLibraryFolders $p.VmMediaFile
    if ($Kind -eq 'jellyfin') { Install-Jellyfin $p; $proc = $null } else { $proc = Install-Emby $p }
    try {
        $base = "http://127.0.0.1:$($S.Port)"
        if (-not (Wait-Http -Url "$base$($S.Prefix)/System/Info/Public" -Seconds 240 -Status 200)) { Add-Missing "${Kind}: it did not answer on 127.0.0.1:$($S.Port) after 4 minutes (see $(Get-AppFolder 'logs'))"; return }
        $anon = New-RecordedClient -BaseUrl $base
        $pub = Send-Recorded -Client $anon -Path "$($S.Prefix)/System/Info/Public"
        $version = [string](Get-ReplyJson $pub).Version
        if (-not $version) { Add-Missing "${Kind}: /System/Info/Public carried no Version"; return }
        $wiz = Complete-EmbyFamilyWizard $S $anon
        $login = Get-EmbyToken $S $anon $wiz.Name $wiz.Password
        if (-not $login.Token) { Add-Missing "${Kind}: the administrator login made in the wizard was refused"; return }
        $adminHeader = @{ $S.UserHeader = $S.AuthPrefix + $script:MediaClientAuth + ", Token=`"$($login.Token)`"" }

        # An API key, as a person makes one in the dashboard: this is what Deluno is given.
        $mk = Send-Recorded -Client $anon -Method POST -Path "$($S.Prefix)/Auth/Keys?app=Deluno" -Headers $adminHeader
        $keys = Send-Recorded -Client $anon -Path "$($S.Prefix)/Auth/Keys" -Headers $adminHeader
        $key = [string](@((Get-ReplyJson $keys).Items | Where-Object { $_.AppName -eq 'Deluno' })[0]).AccessToken
        if (-not $key) { Add-Missing "${Kind}: no API key could be made (POST /Auth/Keys answered $($mk.reply.status))"; return }
        Register-Secret $key
        $kh = if ($Kind -eq 'jellyfin') { @{ Authorization = "MediaBrowser Token=`"$key`"" } } else { @{ 'X-Emby-Token' = $key } }
        $bad = if ($Kind -eq 'jellyfin') { @{ Authorization = 'MediaBrowser Token="not-the-key"' } } else { @{ 'X-Emby-Token' = 'not-the-key' } }
        $c = New-RecordedClient -BaseUrl $base -DefaultHeaders @{ Accept = 'application/json' }
        $get = { param($path) Send-Recorded -Client $c -Path "$($S.Prefix)$path" -Headers $kh }
        $send = { param($method, $path, $json = $null) if ($null -ne $json) { Send-Recorded -Client $c -Method $method -Path "$($S.Prefix)$path" -Headers $kh -Json $json } else { Send-Recorded -Client $c -Method $method -Path "$($S.Prefix)$path" -Headers $kh } }
        $dl = Get-DownloadNote
        $n = { param($what) "$what ($Kind $version, a library of one film and a two-episode series, the film being the suite's base video)." + $dl }
        $fields = 'ProviderIds,Path,DateCreated,ProductionYear,PremiereDate,Genres,People,Tags,TagItems,CommunityRating,CriticRating,MediaSources,SeriesId,SeasonId,ParentIndexNumber,IndexNumber'

        Save-State $Kind $version 'wizard' (& $n 'The first-run setup wizard, step by step, and the administrator''s sign-in (the login and its token are removed)') @(@($pub, $wiz.StateBefore) + $wiz.Wizard + @($login.Exchange, $mk, $keys))
        Save-State $Kind $version 'identity' (& $n 'The public identity and the full system info Deluno reads for the version and server id (ConnectionVersionParserTests)') @($pub, (& $get '/System/Info'))
        Save-State $Kind $version 'wrong-token' (& $n 'System info, the user list and the library list with a key the server does not know') @(
            (Send-Recorded -Client $c -Path "$($S.Prefix)/System/Info" -Headers $bad), (Send-Recorded -Client $c -Path "$($S.Prefix)/Users" -Headers $bad), (Send-Recorded -Client $c -Path "$($S.Prefix)/Library/VirtualFolders" -Headers $bad))
        Save-State $Kind $version 'no-token' (& $n 'System info with no key at all') @((Send-Recorded -Client $c -Path "$($S.Prefix)/System/Info"))
        Save-State $Kind $version 'library-empty' (& $n 'Before any library exists: the virtual folders, the users and an empty catalogue') @(
            (& $get '/Library/VirtualFolders'), (& $get '/Users'), (& $get "/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Fields=$fields&StartIndex=0&Limit=500"), (& $get '/Sessions'))

        # Libraries: the folders hold the film and the series; the server scans them and fetches their details from the internet.
        $mkMovies = & $send 'POST' "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=$([Uri]::EscapeDataString($media.Movies))&refreshLibrary=true" ([ordered]@{ LibraryOptions = [ordered]@{} })
        $mkShows = & $send 'POST' "/Library/VirtualFolders?name=Shows&collectionType=tvshows&paths=$([Uri]::EscapeDataString($media.Shows))&refreshLibrary=true" ([ordered]@{ LibraryOptions = [ordered]@{} })
        Save-State $Kind $version 'libraries-created' (& $n 'A movies library and a TV library made over the two folders') @($mkMovies, $mkShows, (& $get '/Library/VirtualFolders'))
        $scanned = Wait-Until -Seconds 300 -EverySeconds 5 {
            $m = Get-ReplyJson (& $get '/Items?Recursive=true&IncludeItemTypes=Movie'); $t = Get-ReplyJson (& $get '/Items?Recursive=true&IncludeItemTypes=Episode')
            if ($m.TotalRecordCount -ge 1 -and $t.TotalRecordCount -ge 2) { $true }
        }
        if (-not $scanned) { Add-Missing "${Kind}: the library scan found no film and two episodes in 5 minutes"; return }
        Start-Sleep -Seconds 20   # let the details (provider ids, people) arrive from the metadata sources
        $userId = [string]@((Get-ReplyJson (& $get '/Users')))[0].Id
        $movies = & $get "/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Fields=$fields&StartIndex=0&Limit=500"
        $series = & $get "/Items?Recursive=true&IncludeItemTypes=Series,Season,Episode&CollapseBoxSetItems=false&Fields=$fields&StartIndex=0&Limit=500"
        $movieId = [string]@((Get-ReplyJson $movies).Items)[0].Id
        $seriesItem = @((Get-ReplyJson $series).Items | Where-Object { $_.Type -eq 'Series' })[0]
        $seriesId = [string]$seriesItem.Id
        Save-State $Kind $version 'catalogue-movies' (& $n 'The movie catalogue as Deluno asks for it (Fields with provider ids, paths, media sources; unpaged first page)') @($movies, (& $get "/Users/$userId/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Filters=IsPlayed&Fields=UserData"), (& $get "/Users/$userId/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Filters=IsUnplayed&Fields=UserData"))
        Save-State $Kind $version 'catalogue-series' (& $n 'The series catalogue: the series, its seasons and episodes') @($series, (& $get "/Items?Ids=$seriesId&Fields=ProviderIds"))
        Save-State $Kind $version 'libraries' (& $n 'The libraries and users as the connection test lists them') @((& $get '/Library/VirtualFolders'), (& $get '/Users'))

        # Watched, as a person's play history shows it.
        $played = & $send 'POST' "/Users/$userId/PlayedItems/$movieId"
        Save-State $Kind $version 'watched' (& $n 'The film marked watched by the administrator, then the catalogue showing it played') @($played, (& $get "/Users/$userId/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Filters=IsPlayed&Fields=UserData"), (& $get "/Users/$userId/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Filters=IsUnplayed&Fields=UserData"))
        $null = & $send 'DELETE' "/Users/$userId/PlayedItems/$movieId"

        # Playback: a second sign-in (a device playing) reports a play, the way a client does; the server then lists it under Sessions.
        $idle = & $get '/Sessions'
        $player = Get-EmbyToken $S $anon $wiz.Name $wiz.Password
        if ($player.Token) {
            $ph = @{ $S.UserHeader = $S.AuthPrefix + $script:MediaClientAuth + ", Token=`"$($player.Token)`"" }
            $startPlay = Send-Recorded -Client $c -Method POST -Path "$($S.Prefix)/Sessions/Playing" -Headers $ph -Json ([ordered]@{ ItemId = $movieId; PlayMethod = 'DirectPlay'; CanSeek = $true; PositionTicks = 6000000000; IsPaused = $false })
            Start-Sleep -Seconds 2
            $playing = & $get '/Sessions'
            Save-State $Kind $version 'sessions-playing' (& $n 'A client reports the film playing at the ten-minute mark: the session list before and during') @($idle, $startPlay, $playing)
            $null = Send-Recorded -Client $c -Method POST -Path "$($S.Prefix)/Sessions/Playing/Stopped" -Headers $ph -Json ([ordered]@{ ItemId = $movieId; PositionTicks = 6000000000 })
            Save-State $Kind $version 'sessions-stopped' (& $n 'The same client stopped playing: the session list afterwards') @((& $get '/Sessions'))
        } else { Add-Missing "${Kind} sessions-playing: a second sign-in for a playing device was refused" }
        Save-State $Kind $version 'sessions-idle' (& $n 'No one is playing') @($idle)

        # Refresh, update and the directory browser.
        Save-State $Kind $version 'refresh' (& $n 'A refresh of the whole library, and a media update for the film''s path (Modified), then one for a path the server does not hold') @(
            (& $send 'POST' '/Library/Refresh'),
            (& $send 'POST' '/Library/Media/Updated' ([ordered]@{ Updates = @([ordered]@{ Path = $media.MovieFile; UpdateType = 'Modified' }) })),
            (& $send 'POST' '/Library/Media/Updated' ([ordered]@{ Updates = @([ordered]@{ Path = 'C:\nowhere\Not A Film (1999).mkv'; UpdateType = 'Created' }) })))
        Save-State $Kind $version 'directory-contents' (& $n 'The folder browser Deluno uses to map paths: a folder that exists, a folder that does not') @(
            (& $get "/Environment/DirectoryContents?path=$([Uri]::EscapeDataString($media.Movies))&includeFiles=false"), (& $get "/Environment/DirectoryContents?path=$([Uri]::EscapeDataString('C:\nowhere\none'))&includeFiles=false"))

        # Collections, as Deluno's collection sync makes them.
        $made = & $send 'POST' "/Collections?Name=$([Uri]::EscapeDataString('Recorded collection'))&Ids=$movieId"
        $collectionId = [string](Get-ReplyJson $made).Id
        if ($collectionId) {
            $read = & $get "/Items?Ids=$collectionId&Fields=Overview,Tags,Genres,ProviderIds,People,Studios"
            $item = @((Get-ReplyJson $read).Items)[0]
            $item | Add-Member -NotePropertyName Overview -NotePropertyValue 'Recorded description' -Force
            $describe = Send-Recorded -Client $c -Method POST -Path "$($S.Prefix)/Items/$collectionId" -Headers $kh -Json $item
            $fav = & $send 'POST' "/Users/$userId/FavoriteItems/$collectionId"
            $addMore = & $send 'POST' "/Collections/$collectionId/Items?Ids=$seriesId"
            $remove = & $send 'DELETE' "/Collections/$collectionId/Items?Ids=$seriesId"
            Save-State $Kind $version 'collection-sync' (& $n 'A collection made with the film, described, put on the home screen (favourite), a series added and taken out again, then listed with its members') @(
                $made, $read, $describe, $fav, $addMore, $remove, (& $get "/Users/$userId/Items?Recursive=true&IncludeItemTypes=BoxSet&CollapseBoxSetItems=false"), (& $get "/Users/$userId/Items?ParentId=$collectionId&CollapseBoxSetItems=false"), (& $get "/Users/$userId/Items?Recursive=true&IncludeItemTypes=Playlist&CollapseBoxSetItems=false"))
            Save-State $Kind $version 'collection-deleted' (& $n 'The collection deleted (DELETE /Items/{id}), and again') @((& $send 'DELETE' "/Items/$collectionId"), (& $send 'DELETE' "/Items/$collectionId"))
        } else { Add-Missing "${Kind} collection-sync: a collection could not be made (POST /Collections answered $($made.reply.status))" }

        # An item deleted from the library: a throwaway copy of the film, scanned in, then deleted by id.
        $throw = Join-Path $media.Movies 'Recorded Delete (2021)'; New-Item -ItemType Directory -Force -Path $throw | Out-Null
        Copy-Item -LiteralPath $media.MovieFile -Destination (Join-Path $throw 'Recorded Delete (2021).mkv') -Force
        $null = & $send 'POST' '/Library/Refresh'
        $thrown = Wait-Until -Seconds 120 -EverySeconds 4 { @((Get-ReplyJson (& $get '/Items?Recursive=true&IncludeItemTypes=Movie')).Items | Where-Object { $_.Name -like 'Recorded Delete*' })[0] }
        if ($thrown) {
            $before = & $get '/Items?Recursive=true&IncludeItemTypes=Movie'
            $del = & $send 'DELETE' "/Items/$($thrown.Id)"
            Save-State $Kind $version 'item-deleted' (& $n 'A film deleted by its id (the server removes the item and its file), then the list; and a delete of an id it does not have') @($before, $del, (& $get '/Items?Recursive=true&IncludeItemTypes=Movie'), (& $send 'DELETE' '/Items/00000000000000000000000000000000'))
        } else { Add-Missing "${Kind} item-deleted: the throwaway film was not scanned in within 2 minutes" }

        # A user made, given a policy, and removed.
        $uname = 'rec' + (New-VmSecret 3)
        $newUser = & $send 'POST' '/Users/New' ([ordered]@{ Name = $uname })
        $newId = [string](Get-ReplyJson $newUser).Id
        if ($newId) {
            $pol = (Get-ReplyJson (& $get "/Users/$newId")).Policy
            $setPol = Send-Recorded -Client $c -Method POST -Path "$($S.Prefix)/Users/$newId/Policy" -Headers $kh -Json $pol
            Save-State $Kind $version 'user-created' (& $n 'A user made (POST /Users/New), given its policy back, listed, then removed') @($newUser, $setPol, (& $get '/Users'), (& $send 'DELETE' "/Users/$newId"))
        }
        Save-State $Kind $version 'not-answering' 'A request sent to a port nothing listens on: the connection is refused.' @((Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path "$($S.Prefix)/System/Info"))
    } finally {
        if ($Kind -eq 'jellyfin') { Stop-Service -Name 'JellyfinServer' -Force -ErrorAction SilentlyContinue } else { Stop-AppByName 'EmbyServer' }
    }
}

# ------------------------------------------------------------------ Plex

function Install-Plex {
    $dir = Get-AppFolder 'plex'
    $feed = Invoke-RestMethod -Uri 'https://plex.tv/api/downloads/5.json' -UseBasicParsing
    $rel = @($feed.computer.Windows.releases | Where-Object { $_.build -eq 'windows-x86_64' })[0]
    if (-not $rel) { throw 'Plex''s download feed lists no Windows x64 build' }
    $setup = Save-Verified -Url $rel.url -OutFile (Join-Path $dir ([IO.Path]::GetFileName(([Uri]$rel.url).AbsolutePath))) -Sha1 $rel.checksum
    $exe = Join-Path ${env:ProgramFiles(x86)} 'Plex\Plex Media Server\Plex Media Server.exe'
    if (-not (Test-Path $exe)) {
        Write-RunLog 'installing Plex Media Server (silent)'
        $proc = Start-Process -FilePath $setup -ArgumentList '/install', '/quiet', '/norestart' -PassThru -Wait
        if (-not (Test-Path $exe)) { throw "The Plex installer finished (exit code $($proc.ExitCode)) but $exe is not there." }
    }
    Stop-AppByName 'Plex Media Server'
    Remove-DesktopTask 'RecorderPlex'
    Start-InDesktopSession -TaskName 'RecorderPlex' -File $exe -WorkingDirectory (Split-Path $exe)
    $rel.version
}

function Invoke-RecordPlex($p) {
    Assert-FreeSpace 8 'Plex'
    Assert-Internet
    $media = New-MediaLibraryFolders $p.VmMediaFile
    $installed = Install-Plex
    try {
        $base = 'http://127.0.0.1:32400'
        if (-not (Wait-Http -Url "$base/identity" -Seconds 240 -Status 200)) { Add-Missing 'plex: it did not answer /identity on 127.0.0.1:32400 after 4 minutes (is anyone signed in to the VM''s desktop?)'; return }
        $c = New-RecordedClient -BaseUrl $base -DefaultHeaders @{ Accept = 'application/json'; 'X-Plex-Client-Identifier' = 'deluno-recorder-1'; 'X-Plex-Product' = 'Deluno Recorder' }
        $identity = Send-Recorded -Client $c -Path '/identity'
        $version = [string](Get-ReplyJson $identity).MediaContainer.version
        if (-not $version) { $version = $installed }
        $dl = Get-DownloadNote
        $n = { param($what) "$what (Plex Media Server $version, UNCLAIMED: no account is made, so every call is local and carries no token)." + $dl }
        Save-State plex $version 'identity' (& $n '/identity: the unauthenticated identity reply that carries the version and whether the server is claimed (ConnectionVersionParserTests)') @($identity)
        $root = Send-Recorded -Client $c -Path '/'
        if ($root.reply.status -ne 200) {
            Save-State plex $version 'unclaimed-root-refused' (& $n 'The server root and the library list asked with no token') @($root, (Send-Recorded -Client $c -Path '/library/sections'))
            Add-Missing "plex: an unclaimed server answered $($root.reply.status) to '/' without a token, so only /identity was recorded. Everything else (libraries, items, sessions, collections) needs the server claimed by its owner at plex.tv; no account was made"
            return
        }
        Save-State plex $version 'root' (& $n 'The server root: name, version, machine id, claimed or not, myPlex state') @($root)
        $mk = {
            param($name, $type, $agent, $scanner, $path)
            Send-Recorded -Client $c -Method POST -Path "/library/sections?name=$([Uri]::EscapeDataString($name))&type=$type&agent=$agent&scanner=$([Uri]::EscapeDataString($scanner))&language=en-US&location=$([Uri]::EscapeDataString($path))"
        }
        $empty = Send-Recorded -Client $c -Path '/library/sections'
        $mkM = & $mk 'Movies' 'movie' 'tv.plex.agents.movie' 'Plex Movie' $media.Movies
        $mkS = & $mk 'Shows' 'show' 'tv.plex.agents.series' 'Plex TV Series' $media.Shows
        Save-State plex $version 'libraries-created' (& $n 'No library, then a movies section and a TV section made over the two folders') @($empty, $mkM, $mkS, (Send-Recorded -Client $c -Path '/library/sections'))
        $sections = @((Get-ReplyJson (Send-Recorded -Client $c -Path '/library/sections')).MediaContainer.Directory)
        if ($sections.Count -lt 2) { Add-Missing "plex: the server would not make library sections without an account (POST /library/sections answered $($mkM.reply.status))"; return }
        $mKey = [string]($sections | Where-Object { $_.type -eq 'movie' })[0].key
        $sKey = [string]($sections | Where-Object { $_.type -eq 'show' })[0].key
        $scanned = Wait-Until -Seconds 300 -EverySeconds 5 {
            $a = Get-ReplyJson (Send-Recorded -Client $c -Path "/library/sections/$mKey/all?type=1&includeGuids=1")
            $b = Get-ReplyJson (Send-Recorded -Client $c -Path "/library/sections/$sKey/all?type=2&includeGuids=1")
            if ($a.MediaContainer.size -ge 1 -and $b.MediaContainer.size -ge 1) { $true }
        }
        if (-not $scanned) { Add-Missing 'plex: the sections found no film and series in 5 minutes'; return }
        Start-Sleep -Seconds 15
        $movies = Send-Recorded -Client $c -Path "/library/sections/$mKey/all?type=1&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500"
        $shows = Send-Recorded -Client $c -Path "/library/sections/$sKey/all?type=2&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500"
        $movieKey = [string]@((Get-ReplyJson $movies).MediaContainer.Metadata)[0].ratingKey
        $showKey = [string]@((Get-ReplyJson $shows).MediaContainer.Metadata)[0].ratingKey
        Save-State plex $version 'sections' (& $n 'The library sections as Deluno lists them') @((Send-Recorded -Client $c -Path '/library/sections'))
        Save-State plex $version 'catalogue-movies' (& $n 'The movie section with guids (provider ids)') @($movies, (Send-Recorded -Client $c -Path "/library/metadata/$movieKey"))
        Save-State plex $version 'catalogue-series' (& $n 'The TV section with guids, and one show in full') @($shows, (Send-Recorded -Client $c -Path "/library/metadata/$showKey"), (Send-Recorded -Client $c -Path "/library/metadata/$showKey/allLeaves"))
        Save-State plex $version 'accounts' (& $n 'The server''s own account list and the myPlex account state (unclaimed: no plex.tv user)') @((Send-Recorded -Client $c -Path '/accounts'), (Send-Recorded -Client $c -Path '/myplex/account'))
        Save-State plex $version 'sessions-idle' (& $n 'No one is playing') @((Send-Recorded -Client $c -Path '/status/sessions'))
        # Watched: scrobble the film, then read it back with its view count.
        $scrobble = Send-Recorded -Client $c -Path "/:/scrobble?key=$movieKey&identifier=com.plexapp.plugins.library"
        Save-State plex $version 'watched' (& $n 'The film scrobbled (marked watched), then read with its viewCount') @($scrobble, (Send-Recorded -Client $c -Path "/library/metadata/$movieKey"))
        $null = Send-Recorded -Client $c -Path "/:/unscrobble?key=$movieKey&identifier=com.plexapp.plugins.library"
        # A timeline report makes a play: the way a player says it is playing.
        $tl = Send-Recorded -Client $c -Path "/:/timeline?ratingKey=$movieKey&key=%2Flibrary%2Fmetadata%2F$movieKey&state=playing&time=600000&duration=888000&X-Plex-Client-Identifier=deluno-recorder-2"
        Start-Sleep -Seconds 2
        Save-State plex $version 'sessions-playing' (& $n 'A player reports the film playing at the ten-minute mark (/:/timeline), and the server lists it under /status/sessions') @($tl, (Send-Recorded -Client $c -Path '/status/sessions'))
        $null = Send-Recorded -Client $c -Path "/:/timeline?ratingKey=$movieKey&key=%2Flibrary%2Fmetadata%2F$movieKey&state=stopped&time=600000&duration=888000&X-Plex-Client-Identifier=deluno-recorder-2"
        # Refresh, empty trash, folder browser.
        Save-State plex $version 'refresh' (& $n 'A refresh of one section, of one folder in it, and of a folder the server does not hold; the trash emptied') @(
            (Send-Recorded -Client $c -Path "/library/sections/$mKey/refresh"), (Send-Recorded -Client $c -Path "/library/sections/$mKey/refresh?path=$([Uri]::EscapeDataString((Join-Path $media.Movies 'Sintel (2010)')))"),
            (Send-Recorded -Client $c -Path "/library/sections/$mKey/refresh?path=$([Uri]::EscapeDataString('C:\nowhere'))"), (Send-Recorded -Client $c -Method PUT -Path "/library/sections/$mKey/emptyTrash"))
        Save-State plex $version 'directory-contents' (& $n 'The folder browser (/services/browse): a folder that exists, then one that does not') @(
            (Send-Recorded -Client $c -Path "/services/browse?path=$([Uri]::EscapeDataString($media.Movies))&includeFiles=0"), (Send-Recorded -Client $c -Path "/services/browse?path=$([Uri]::EscapeDataString('C:\nowhere'))&includeFiles=0"))
        # Collections, as Deluno's collection sync makes them.
        $machine = [string](Get-ReplyJson $identity).MediaContainer.machineIdentifier
        $uri = [Uri]::EscapeDataString("server://$machine/com.plexapp.plugins.library/library/metadata/$movieKey")
        $made = Send-Recorded -Client $c -Method POST -Path "/library/collections?type=1&title=$([Uri]::EscapeDataString('Recorded collection'))&smart=0&sectionId=$mKey&uri=$uri"
        $collection = [string]@((Get-ReplyJson $made).MediaContainer.Metadata)[0].ratingKey
        if ($collection) {
            $desc = Send-Recorded -Client $c -Method PUT -Path "/library/sections/$mKey/all?type=18&id=$collection&summary.value=$([Uri]::EscapeDataString('Recorded description'))&summary.locked=1"
            Save-State plex $version 'collection-sync' (& $n 'A collection made with the film, described, its home-screen promotion read, listed with its children') @(
                (Send-Recorded -Client $c -Path "/library/sections/$mKey/collections?includeGuids=0"), $made, $desc, (Send-Recorded -Client $c -Path "/library/collections/$collection/children?excludeAllLeaves=1"),
                (Send-Recorded -Client $c -Path "/hubs/sections/$mKey/manage?metadataItemId=$collection"),
                (Send-Recorded -Client $c -Method POST -Path "/hubs/sections/$mKey/manage?metadataItemId=$collection&promotedToRecommended=1&promotedToOwnHome=1&promotedToSharedHome=1"))
            Save-State plex $version 'collection-deleted' (& $n 'The collection deleted, and the same delete again') @((Send-Recorded -Client $c -Method DELETE -Path "/library/collections/$collection"), (Send-Recorded -Client $c -Method DELETE -Path "/library/collections/$collection"))
        } else { Add-Missing "plex collection-sync: a collection could not be made (answered $($made.reply.status))" }
        Save-State plex $version 'playlists' (& $n 'The video playlists (none made)') @((Send-Recorded -Client $c -Path '/playlists?playlistType=video'))
        Save-State plex $version 'wrong-token' (& $n 'The library list with a token the server does not know') @((Send-Recorded -Client $c -Path '/library/sections' -Headers @{ 'X-Plex-Token' = 'not-a-token' }))
        Save-State plex $version 'not-answering' 'A request sent to a port nothing listens on: the connection is refused.' @((Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path '/identity'))
        Add-Missing 'plex: the plex.tv replies Deluno asks for (the user list, shared servers, resources, a token that identifies an owner) need the server claimed by its owner at plex.tv; no account was made'
    } finally {
        Stop-AppByName 'Plex Media Server'
        Remove-DesktopTask 'RecorderPlex'
    }
}
