<#
.SYNOPSIS
    Sets Deluno up in the golden-path VM the way the guided setup does, through Deluno's own API, with throwaway logins made
    here (golden path scenario suite).

.DESCRIPTION
    Dot-source after Suite.Lib.ps1 and Fixtures.Lib.ps1, then call Initialize-SuiteEnvironment. It creates the first account,
    connects the five download clients and Weir (Weir with a new account of its own), makes the Movies and TV libraries with
    their folders and quality profile, routes them, links them to Weir ("refine before import"), points Deluno at the Usenet stub
    and the local Torznab feed, and turns automation on. Every call is one the web screens make; nothing edits an app's files.
    The logins are generated here, kept in secrets.json beside the suite and never printed. Windows PowerShell 5.1.
#>

function Wait-DelunoUp {
    param([int] $TimeoutSec = 120)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 2 -Condition {
        $r = Try-Deluno -Path '/api/auth/bootstrap-status' -TimeoutSec 5
        if ($r.Status -eq 200) { $r.Data } else { $null }
    }
    if (-not $w.Ok) { throw 'Deluno did not answer on its port.' }
    $w.Value
}

# Weir has its own first-run wizard on its first visit (a Weir-side step the earlier hand-driven runs pressed through: sign in, Finish
# setup, Continue to Weir). Best effort: it never stops the run, and says in the log what it found.
function Complete-WeirWizard {
    try {
        Start-Edge
        $page = New-CdpPage 'about:blank'
        try {
            Send-Cdp -Page $page -Method 'Page.navigate' -Params @{ url = $script:S.WeirUrl + '/' } | Out-Null
            Start-Sleep -Seconds 3
            $text = Get-PageText -Page $page
            if ($text -match 'Sign in|Log in') {
                Set-PageInput -Page $page -Selector 'input[name=username],input[type=text],input:not([type])' -Value 'golden' | Out-Null
                Set-PageInput -Page $page -Selector 'input[type=password]' -Value (Get-SuiteSecret 'weir-password') | Out-Null
                Click-PageText -Page $page -Pattern '^(Sign in|Log in)' | Out-Null
                Start-Sleep -Seconds 3
            }
            $steps = @()
            foreach ($pattern in '^Finish setup', '^Continue to Weir') {
                if (Click-PageText -Page $page -Pattern $pattern) { $steps += $pattern; Start-Sleep -Seconds 2 }
            }
            $now = (Get-PageText -Page $page) -replace '\s+', ' '
            Write-SuiteLog ("   Weir's first-run wizard: pressed [{0}]; the page now reads: {1}" -f ($steps -join ', '), $now.Substring(0, [math]::Min(120, $now.Length)))
        }
        finally { Close-CdpPage -Page $page }
    }
    catch { Write-SuiteLog "   Weir's first-run wizard could not be driven: $($_.Exception.Message)" }
}

function Initialize-SuiteEnvironment {
    param([string] $MoviesRoot = 'C:\Media\Movies', [string] $TvRoot = 'C:\Media\TV', [string] $WeirMovies = 'C:\Weir\Ready\Movies', [string] $WeirTv = 'C:\Weir\Ready\TV')

    $status = Wait-DelunoUp
    New-SuiteSecret 'deluno-password' | Out-Null
    New-SuiteSecret 'weir-password' | Out-Null
    foreach ($f in $MoviesRoot, $TvRoot, $WeirMovies, $WeirTv) { New-Item -ItemType Directory -Force -Path $f | Out-Null }

    # 1. The first account.
    if ($status.requiresSetup) {
        $r = Try-Deluno -Method POST -Path '/api/auth/bootstrap' -Body @{ username = 'golden'; displayName = 'Golden suite'; password = (Get-SuiteSecret 'deluno-password') }
        if ($r.Status -ne 200) { throw (Hide-Secrets "Creating the first account answered $($r.Status). $($r.Raw)") }
    }
    Connect-Deluno
    Write-SuiteLog 'Deluno: first account made and signed in.'

    # 2. The clients, then Weir with an account of its own.
    $all = Invoke-Deluno -Method POST -Path '/api/system/bundled-components/connect-all' -TimeoutSec 240
    foreach ($row in @($all.results)) { Write-SuiteLog ("   connect {0}: healthy={1} {2}" -f $row.kind, $row.healthy, $row.message) }
    $weir = Try-Deluno -Method POST -Path '/api/system/bundled-components/weir/connect' -Body @{ username = 'golden'; password = (Get-SuiteSecret 'weir-password') } -TimeoutSec 120
    if ($weir.Status -ne 200 -or -not $weir.Data.healthy) { throw (Hide-Secrets "Connecting Weir answered $($weir.Status). $($weir.Raw)") }
    Write-SuiteLog ("   connect weir: {0}" -f $weir.Data.message)

    Complete-WeirWizard

    # 3. Quality profile and the two libraries, with the folders the clients finish in.
    $profile = @(Invoke-Deluno -Path '/api/quality-profiles') | Where-Object { $_.mediaType -eq 'movies' } | Select-Object -First 1
    if (-not $profile) {
        $profile = Invoke-Deluno -Method POST -Path '/api/quality-profiles' -Body @{
            name = 'Balanced 1080p - Suite'; mediaType = 'movies'; cutoffQuality = 'WEB 1080p'; allowedQualities = 'WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p'
            customFormatIds = ''; upgradeUntilCutoff = $true; upgradeUnknownItems = $false
        }
    }
    $tvProfile = @(Invoke-Deluno -Path '/api/quality-profiles') | Where-Object { $_.mediaType -eq 'tv' } | Select-Object -First 1
    if (-not $tvProfile) {
        $tvProfile = Invoke-Deluno -Method POST -Path '/api/quality-profiles' -Body @{
            name = 'Balanced 1080p - Suite TV'; mediaType = 'tv'; cutoffQuality = 'WEB 1080p'; allowedQualities = 'WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p'
            customFormatIds = ''; upgradeUntilCutoff = $true; upgradeUnknownItems = $false
        }
    }
    foreach ($spec in @(
            @{ Name = 'Movies'; Type = 'movies'; Root = $MoviesRoot; Down = 'C:\Downloads\Completed\Movies'; Profile = $profile; Out = $WeirMovies },
            @{ Name = 'TV Shows'; Type = 'tv'; Root = $TvRoot; Down = 'C:\Downloads\Completed\TV'; Profile = $tvProfile; Out = $WeirTv })) {
        $lib = @(Invoke-Deluno -Path '/api/libraries') | Where-Object { $_.mediaType -eq $spec.Type } | Select-Object -First 1
        if (-not $lib) {
            # Nothing automatic is on until a person says so (#936); the suite sends every release itself.
            $lib = Invoke-Deluno -Method POST -Path '/api/libraries' -Body @{
                name = $spec.Name; mediaType = $spec.Type; purpose = 'Main library'; rootPath = $spec.Root; downloadsPath = $spec.Down
                qualityProfileId = $spec.Profile.id; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
                removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
            }
        }
        $conn = @(Invoke-Deluno -Path '/api/integrations/processors/connections') | Where-Object { $_.isEnabled -and $_.name -match 'weir' } | Select-Object -First 1
        if (-not $conn) { throw 'Weir is not a processor connection in Deluno after Connect Weir.' }
        Invoke-Deluno -Method PUT -Path "/api/libraries/$($lib.id)/workflow" -Body @{
            importWorkflow = 'refine-before-import'; processorConnectionId = $conn.id; processorName = $conn.name; processorOutputPath = $spec.Out
            processorTimeoutMinutes = 2880; processorFailureMode = 'block'; cleanupMode = 'keep-source'; removeEmptySourceFolders = $false
        } | Out-Null
    }

    # 4. The Usenet provider and the Newznab stub, through the script the harness already copied in.
    $wire = 'C:\golden\usenet-stub\Set-UsenetStubInDeluno.ps1'
    if (Test-Path $wire) {
        $out = & $wire -Username 'golden' -Password (Get-SuiteSecret 'deluno-password') 2>&1 | Out-String
        Write-SuiteLog (Hide-Secrets $out.Trim())
    }
    else { throw 'The Usenet stub scripts are not in the VM (run the harness with -UsenetStub).' }

    # 5. Automation on, and each download client's own folders answered.
    Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $true } | Out-Null
    $clients = Get-DownloadClients
    Write-SuiteLog ("Deluno: clients {0}" -f (($clients | ForEach-Object { "$($_.protocol)=$($_.healthStatus)" }) -join ', '))
    Start-FeedAndStubs
    Initialize-Indexers
    Initialize-BaseVideos
}

# Download-health rules for a phase of the stuck-download scenarios: only the named rules on, each at the strike count and check
# interval given, every other transfer rule off.
function Set-HealthRules {
    param(
        [Parameter(Mandatory)] [hashtable] $On,       # kind -> @{ Strikes = 2; Threshold = 5 }
        [int] $CheckMinutes = 1
    )
    $rules = @(Invoke-Deluno -Path '/api/download-health/rules')
    foreach ($kind in 'no-progress', 'too-slow', 'low-peers', 'dead-torrent', 'magnet-metadata', 'client-failure') {
        $rule = $rules | Where-Object { $_.kind -eq $kind } | Select-Object -First 1
        $want = $On.ContainsKey($kind)
        if (-not $rule -and -not $want) { continue }
        $base = if ($rule) { $rule } else {
            [pscustomobject]@{ id = 'new'; kind = $kind; enabled = $false; strikeLimit = 3; timerMinutes = 5; appliesTo = 'both'; minCompletionPercent = 0; maxCompletionPercent = 100
                resetStrikesOnProgress = $true; actionPreset = 'replace'; threshold = 0; privacyScope = 'both'; tooSlowMaxTotalMinutes = $null; ignoreAboveSizeBytes = $null
                skipWhileAlternateSpeedLimitOn = $false; resetStrikesMinimumProgressBytes = $null }
        }
        $body = @{}
        foreach ($p in $base.PSObject.Properties) { $body[$p.Name] = $p.Value }
        $body.enabled = $want
        if ($want) {
            $body.strikeLimit = [int]$On[$kind].Strikes
            $body.timerMinutes = $CheckMinutes
            $body.actionPreset = 'replace'
            if ($On[$kind].ContainsKey('Threshold')) { $body.threshold = [double]$On[$kind].Threshold }
        }
        Invoke-Deluno -Method PUT -Path "/api/download-health/rules/$($base.id)" -Body $body | Out-Null
    }
}
