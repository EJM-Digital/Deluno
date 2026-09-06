<#
.SYNOPSIS
    Build the browser app and put it on the rig, replacing what is there.

.DESCRIPTION
    A front-end-only change does not need a republish of the host — it needs
    `apps/web/dist` on top of `C:\Deluno\App\wwwroot`. That was done by hand,
    as `Copy-Item -Force` over the top, every time.

    Which is why the rig had 7,523 files and 104 MB in `wwwroot\assets` for a
    build that emits 80: every chunk from every deploy since the box was set
    up, plus `.br`/`.gz` siblings from a compression step that no longer runs.
    Vite hashes its filenames, so nothing was ever overwritten and nothing was
    ever removed. It served the right build the whole time and would have gone
    on growing forever.

    Two things stop it coming back. The assets directory is emptied before the
    copy rather than written over, so what is on the rig is what the build
    emitted and nothing else. And the copy is checked afterwards by reading
    every asset `index.html` asks for back off the rig — a deploy that half
    happened is the one failure this script exists to catch, and a file count
    would not catch it.

    The host is not stopped and does not need to be: `UseStaticFiles` reads
    from disk per request and holds no lock on wwwroot. Stopping it is for a
    C# change, which this is not.

.EXAMPLE
    pwsh scripts/lab/deploy-web.ps1
    pwsh scripts/lab/deploy-web.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [string] $ComputerName,
    [string] $UserName,
    [string] $Password,
    [string] $Destination = 'C:\Deluno\App\wwwroot',
    # For redeploying a build you have already made and inspected.
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = Get-Rig
if (-not $ComputerName) { $ComputerName = $rig.host }
if (-not $UserName) { $UserName = $rig.userName }
if (-not $Password) { $Password = $rig.password }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dist = Join-Path $repo 'apps\web\dist'

if (-not $SkipBuild) {
    Write-Host 'Building apps/web...' -ForegroundColor Cyan
    npm --prefix (Join-Path $repo 'apps\web') run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $dist 'index.html'))) {
    throw "No build at $dist. Run without -SkipBuild."
}

# What the page will ask for. Read from the build rather than from a glob of
# the whole directory: a chunk nobody imports is not evidence the deploy worked,
# and this is the list the verification below is against.
$indexHtml = Get-Content (Join-Path $dist 'index.html') -Raw
$wanted = [regex]::Matches($indexHtml, '(?:src|href)="(/assets/[^"]+)"') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

if ($wanted.Count -eq 0) { throw 'index.html references no assets — refusing to deploy it.' }

$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($UserName, $secure)
$session = New-PSSession -ComputerName $ComputerName -Credential $credential

try {
    $before = Invoke-Command -Session $session -ArgumentList $Destination -ScriptBlock {
        param($destination)
        $assets = Join-Path $destination 'assets'
        if (-not (Test-Path $assets)) { return [pscustomobject]@{ Files = 0; Megabytes = 0 } }
        $files = Get-ChildItem $assets -File -Recurse
        [pscustomobject]@{
            Files = $files.Count
            Megabytes = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
        }
    }

    Write-Host "Rig had $($before.Files) asset files ($($before.Megabytes) MB). Clearing..." -ForegroundColor Cyan

    Invoke-Command -Session $session -ArgumentList $Destination -ScriptBlock {
        param($destination)
        # Everything under assets/ is build output — hashed chunks, and the
        # stale .br/.gz siblings of chunks that are long gone. Nothing writes
        # here at runtime.
        $assets = Join-Path $destination 'assets'
        if (Test-Path $assets) { Remove-Item "$assets\*" -Recurse -Force }

        # The pre-compressed copies at the top level are the same leftovers.
        # `UseStaticFiles` does no content negotiation, so the host has never
        # served one; they are only stale bytes wearing a real filename.
        Get-ChildItem $destination -File -Include '*.br', '*.gz' | Remove-Item -Force
    }

    Write-Host "Copying $dist..." -ForegroundColor Cyan
    Copy-Item -ToSession $session -Path (Join-Path $dist '*') -Destination $Destination -Recurse -Force

    # Read back what the page asks for, through the paths the browser will use.
    $missing = Invoke-Command -Session $session -ArgumentList $Destination, $wanted -ScriptBlock {
        param($destination, $wanted)
        $wanted | Where-Object { -not (Test-Path (Join-Path $destination ($_ -replace '^/', '' -replace '/', '\'))) }
    }

    if ($missing) { throw "Deployed, but these are not on the rig: $($missing -join ', ')" }

    $after = Invoke-Command -Session $session -ArgumentList $Destination -ScriptBlock {
        param($destination)
        $files = Get-ChildItem (Join-Path $destination 'assets') -File -Recurse
        [pscustomobject]@{
            Files = $files.Count
            Megabytes = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
        }
    }

    Write-Host "Deployed. $($after.Files) asset files ($($after.Megabytes) MB), $($wanted.Count) of them asked for by index.html." -ForegroundColor Green
}
finally {
    Remove-PSSession $session
}
