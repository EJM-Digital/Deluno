# Installs FlareSolverr (its Windows release) in the golden VM, runs it on 127.0.0.1, and records the replies Deluno's CloudflareHelper
# reads (FlareSolverrCloudflareHelperTests, #1237): the index page with its version, sessions.create / list / destroy, request.get
# and request.post (a page that answers, a host that does not exist, a timeout too short to finish), an unknown command and a bad
# body. Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1 and Install.Lib.ps1.
#
# Where it comes from: the project's own GitHub release (FlareSolverr/FlareSolverr), flaresolverr_windows_x64.zip, checked against the
# SHA-256 GitHub records for the asset. It carries its own Chrome. FlareSolverr listens where its HOST variable says (127.0.0.1 here).
# The pages it fetches are public and need no account. A page behind a real Cloudflare challenge is tried (the reply says whether
# the challenge was solved or not seen); if none of the public pages tried shows a challenge that day, "challenge solved" is listed in MISSING.md.

$script:ChallengeTries = @('https://nowsecure.nl/', 'https://www.cloudflare.com/cdn-cgi/trace')

function Invoke-RecordFlareSolverr($p) {
    Assert-FreeSpace 4 'FlareSolverr'
    Assert-Internet
    $dir = Get-AppFolder 'flaresolverr'
    $asset = Get-GitHubAsset -Repo 'FlareSolverr/FlareSolverr' -NamePattern '^flaresolverr_windows_x64\.zip$'
    $zip = Save-Verified -Url $asset.Url -OutFile (Join-Path $dir $asset.Name) -Sha256 $asset.Sha256
    $app = Join-Path $dir 'app'
    if (-not (Test-Path $app)) { Expand-Fresh $zip $app }
    $exe = (Get-ChildItem -Path $app -Filter 'flaresolverr.exe' -Recurse | Select-Object -First 1).FullName
    if (-not $exe) { throw 'The FlareSolverr archive holds no flaresolverr.exe' }
    $port = $p.FlareSolverrPort
    Stop-AppByName 'flaresolverr'
    $proc = Start-AppProcess -File $exe -WorkingDirectory (Split-Path $exe) -LogName 'flaresolverr.log' -Environment @{ HOST = '127.0.0.1'; PORT = "$port"; LOG_LEVEL = 'info'; HEADLESS = 'true' }
    try {
        if (-not (Wait-Http -Url "http://127.0.0.1:$port/" -Seconds 180 -Status 200)) { Add-Missing "flaresolverr: it did not answer on 127.0.0.1:$port after 3 minutes (see $(Get-AppFolder 'logs'))"; return }
        $c = New-RecordedClient -BaseUrl "http://127.0.0.1:$port"
        $index = Send-Recorded -Client $c -Path '/'
        $version = [string](Get-ReplyJson $index).version
        if (-not $version) { $version = $asset.Tag.TrimStart('v') }
        $dl = Get-DownloadNote
        $n = { param($what) "$what (FlareSolverr $version for Windows, its own Chrome, public pages only)." + $dl }
        $v1 = { param($body) Send-Recorded -Client $c -Method POST -Path '/v1' -Json $body }
        $session = 'recorder-' + (New-VmSecret 3)

        Save-State flaresolverr $version 'index' (& $n 'GET /: the page that says FlareSolverr is ready, with its version and the browser''s user agent') @($index)
        Save-State flaresolverr $version 'sessions-empty' (& $n 'sessions.list with no session') @((& $v1 ([ordered]@{ cmd = 'sessions.list' })))
        $create = & $v1 ([ordered]@{ cmd = 'sessions.create'; session = $session })
        Save-State flaresolverr $version 'session-created' (& $n 'sessions.create, the same again (it already exists), then sessions.list') @($create, (& $v1 ([ordered]@{ cmd = 'sessions.create'; session = $session })), (& $v1 ([ordered]@{ cmd = 'sessions.list' })))
        $get = & $v1 ([ordered]@{ cmd = 'request.get'; url = 'https://example.com/'; session = $session; maxTimeout = 60000 })
        Save-State flaresolverr $version 'request-get-ok' (& $n 'request.get of a plain public page through a session: status ok, the page, its cookies and headers (no challenge there)') @($get)
        Save-State flaresolverr $version 'request-get-no-session' (& $n 'request.get with no session named (FlareSolverr makes a one-off browser)') @((& $v1 ([ordered]@{ cmd = 'request.get'; url = 'https://example.com/'; maxTimeout = 60000 })))
        Save-State flaresolverr $version 'request-post' (& $n 'request.post of a form to a public echo service') @((& $v1 ([ordered]@{ cmd = 'request.post'; url = 'https://httpbin.org/post'; session = $session; postData = 'a=1&b=2'; maxTimeout = 60000 })))
        Save-State flaresolverr $version 'request-host-missing' (& $n 'request.get of a host that does not exist: status error and the browser''s own reason') @((& $v1 ([ordered]@{ cmd = 'request.get'; url = 'http://does-not-exist.invalid/'; session = $session; maxTimeout = 30000 })))
        Save-State flaresolverr $version 'request-timeout' (& $n 'request.get with a maxTimeout too short to finish') @((& $v1 ([ordered]@{ cmd = 'request.get'; url = 'https://example.com/'; session = $session; maxTimeout = 1 })))
        Save-State flaresolverr $version 'request-no-url' (& $n 'request.get with no url') @((& $v1 ([ordered]@{ cmd = 'request.get'; session = $session })))
        Save-State flaresolverr $version 'unknown-command' (& $n 'A command FlareSolverr does not have') @((& $v1 ([ordered]@{ cmd = 'request.bogus' })))
        Save-State flaresolverr $version 'bad-body' (& $n 'A body that is not JSON, and a body with no cmd') @((Send-Recorded -Client $c -Method POST -Path '/v1' -Body 'this is not json' -ContentType 'application/json'), (& $v1 ([ordered]@{ session = 'x' })))
        Save-State flaresolverr $version 'wrong-method' (& $n 'GET /v1 (the endpoint takes POST)') @((Send-Recorded -Client $c -Path '/v1'))
        $challenged = $null
        foreach ($url in $script:ChallengeTries) {
            $r = & $v1 ([ordered]@{ cmd = 'request.get'; url = $url; session = $session; maxTimeout = 120000 })
            Write-RunLog "flaresolverr: challenge try $url -> $((Get-ReplyJson $r).message)"
            if ((Get-ReplyJson $r).message -match 'solved') { $challenged = $r; break }
            if (-not $challenged) { $first = $r }
        }
        if ($challenged) { Save-State flaresolverr $version 'challenge-solved' (& $n 'request.get of a page behind a Cloudflare challenge: the reply says the challenge was solved, with the clearance cookie') @($challenged) }
        else {
            Save-State flaresolverr $version 'challenge-not-seen' (& $n 'request.get of the public pages that sometimes show a Cloudflare challenge: none showed one today, and the replies say so') @($first)
            Add-Missing 'flaresolverr: a "Challenge solved!" reply needs a page that actually shows a Cloudflare challenge to the VM; none of the public pages tried did that day'
        }
        $destroy = & $v1 ([ordered]@{ cmd = 'sessions.destroy'; session = $session })
        Save-State flaresolverr $version 'session-destroyed' (& $n 'sessions.destroy, the same again (no such session), then sessions.list') @($destroy, (& $v1 ([ordered]@{ cmd = 'sessions.destroy'; session = $session })), (& $v1 ([ordered]@{ cmd = 'sessions.list' })))
        Save-State flaresolverr $version 'request-session-missing' (& $n 'request.get naming a session that does not exist (what Deluno handles by making the session again)') @((& $v1 ([ordered]@{ cmd = 'request.get'; url = 'https://example.com/'; session = $session; maxTimeout = 60000 })))
        Save-State flaresolverr $version 'not-answering' 'A request sent to a port nothing listens on: the connection is refused.' @((Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/v1' -Json ([ordered]@{ cmd = 'sessions.list' })))
    } finally {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        Stop-AppByName @('flaresolverr', 'chrome', 'chromedriver', 'undetected_chromedriver')
    }
}
