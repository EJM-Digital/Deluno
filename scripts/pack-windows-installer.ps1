<#
.SYNOPSIS
    Builds the Windows Setup executable — the artifact people actually install.

.DESCRIPTION
    The one way to produce a Deluno installer. `release.yml` calls this rather
    than carrying its own copy of the steps.

    It exists because the packaging used to live only in CI, which meant the
    installer could not be built or tested without cutting a GitHub release.
    Nobody runs an artifact they cannot make, so nobody found that the packaged
    build had drifted from the one the lab runs: it shipped a GPL FFmpeg build
    at `master-latest` and copied only `ffprobe.exe`, leaving subtitle timing
    sync dark on every installed copy while working perfectly on the machine
    used to test it.

    That is the whole argument for this file. A release path you can only
    exercise by releasing is a release path nobody exercises.

.PARAMETER Version
    Three-part version for the package, e.g. 1.0.0 or 1.0.0-rc.3. Defaults to a
    local build stamp so an unversioned run still produces something installable.

.PARAMETER Channel
    `rc` or `stable`. Defaults the way release.yml defaults it: anything 0.x or
    carrying a prerelease suffix is `rc`, so a validation build cannot push a
    fresh install onto the stable feed.

.EXAMPLE
    ./scripts/pack-windows-installer.ps1 -Version 1.0.0-rc.3
#>
param(
    [string]$Version,
    [ValidateSet("rc", "stable", "")]
    [string]$Channel = "",
    [string]$RuntimeIdentifier = "win-x64",
    # Skips the UnRAR download, which reaches rarlab and is the one step that
    # fails on a machine with no outbound access to it.
    [switch]$SkipUnrar,

    # Clears previously packed releases first. vpk refuses to pack a version
    # equal to or below one already in the output directory, which is right for
    # a release feed and pure friction locally - it means a second test build
    # of the same version fails with a message about increasing the version.
    [switch]$Clean,

    # Matches the Velopack PackageReference in Deluno.Tray.csproj. Bump both
    # together, or an in-place upgrade is packed by one version and applied by
    # another.
    [string]$VelopackVersion = "0.0.1298"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root "artifacts\windows\bin"
$output = Join-Path $root "artifacts\windows\velopack"

if ([string]::IsNullOrWhiteSpace($Version)) {
    # A local build still needs a version Velopack will accept, and it has to
    # sort below any real release so a test install never looks newer than the
    # thing it is testing against.
    $Version = "0.0.1-local." + (Get-Date -Format "yyyyMMddHHmm")
    Write-Host "No -Version given; building $Version"
}

if ([string]::IsNullOrWhiteSpace($Channel)) {
    $Channel = if ($Version.StartsWith("0.") -or $Version -match "-(rc|beta|preview)([.-]|$)") { "rc" } else { "stable" }
}

Push-Location $root
try {
    Write-Host "== Frontend"
    npm.cmd run build:web
    if ($LASTEXITCODE -ne 0) { throw "build:web failed." }

    Write-Host "== Payload"
    # Never reuse a previous payload. Packaging must not pick up a component the
    # build no longer produces - a removed DLL left behind here ships.
    Remove-Item -LiteralPath $payload -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $root "apps\windows-tray\Deluno.Tray.csproj") `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -p:DelunoUpdateChannel=$Channel `
        -o $payload
    if ($LASTEXITCODE -ne 0) { throw "Publishing the tray payload failed." }

    Write-Host "== FFmpeg"
    & (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1") -RuntimeIdentifier $RuntimeIdentifier
    $ffmpegTarget = Join-Path $payload "tools\ffmpeg"
    New-Item -ItemType Directory -Force -Path $ffmpegTarget | Out-Null
    Copy-Item -Path (Join-Path $root "tools\ffmpeg\$RuntimeIdentifier\*") -Destination $ffmpegTarget -Recurse -Force

    # Asserted rather than trusted. A silently absent binary is how the packaged
    # build came to differ from the lab in the first place.
    foreach ($required in @("ffmpeg.exe", "ffprobe.exe")) {
        $path = Join-Path $ffmpegTarget $required
        if (-not (Test-Path $path)) { throw "$required is missing from the package payload." }
        Write-Host ("   {0} ({1} MB)" -f $required, [math]::Round((Get-Item $path).Length / 1MB, 1))
    }

    if (-not $SkipUnrar) {
        Write-Host "== UnRAR"
        # Extraction-only use of the official binary is license-clean; the
        # source licence forbids building a competing archiver from it, which
        # is not what this is. Upstream licence ships alongside.
        try {
            $self = Join-Path ([System.IO.Path]::GetTempPath()) "unrarw64.exe"
            $extract = Join-Path ([System.IO.Path]::GetTempPath()) ("deluno-unrar-" + [Guid]::NewGuid().ToString("n"))
            $previousProgress = $ProgressPreference
            $ProgressPreference = "SilentlyContinue"
            try { Invoke-WebRequest -Uri "https://www.rarlab.com/rar/unrarw64.exe" -OutFile $self -UseBasicParsing }
            finally { $ProgressPreference = $previousProgress }
            New-Item -ItemType Directory -Force -Path $extract | Out-Null
            & $self -y -d"$extract" | Out-Null
            $unrar = Get-ChildItem $extract -Recurse -Filter "UnRAR.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($unrar) {
                $unrarTarget = Join-Path $payload "tools\unrar"
                New-Item -ItemType Directory -Force -Path $unrarTarget | Out-Null
                Copy-Item $unrar.FullName $unrarTarget -Force
                $license = Get-ChildItem $extract -Recurse -Filter "license.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($license) { Copy-Item $license.FullName $unrarTarget -Force }
                Write-Host "   UnRAR.exe bundled"
            }
            else {
                Write-Warning "UnRAR.exe could not be extracted; RAR archives will need unrar installed separately."
            }
        }
        catch {
            # Not fatal. A missing UnRAR costs one import path; a failed
            # release costs the release.
            Write-Warning "UnRAR could not be fetched ($($_.Exception.Message)); continuing without it."
        }
    }

    Write-Host "== Velopack"
    # Pinned, and pinned to the version of the Velopack library the app
    # references. A newer CLI packs a bundle the older runtime then has to
    # update itself with, and "this can occasionally cause compatibility
    # issues" is not a sentence you want anywhere near an in-place upgrade.
    # A machine that already has a different vpk gets the right one rather
    # than silently packing with whatever it happened to have.
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    $installed = (& vpk --version 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or "$installed" -notmatch [regex]::Escape($VelopackVersion)) {
        Write-Host "   installing vpk $VelopackVersion (had: $installed)"
        # Uninstall first. `dotnet tool update` refuses to move backwards -
        # "The requested version 0.0.1298 is lower than existing version" - so
        # a machine that had picked up a newer vpk would otherwise keep packing
        # with it and quietly ignore the pin.
        dotnet tool uninstall -g vpk 2>$null | Out-Null
        dotnet tool install -g vpk --version $VelopackVersion | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not install vpk $VelopackVersion." }
    }

    if ($Clean -and (Test-Path $output)) {
        Write-Host "   clearing previous releases in $output"
        Remove-Item -Path $output -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $output | Out-Null
    # Deluno ships unsigned, by decision: a code-signing certificate is a
    # recurring cost this project is not paying. The consequence - SmartScreen
    # warning on first install - is documented for users rather than hidden.
    vpk pack `
        --packId Deluno `
        --packVersion $Version `
        --packDir $payload `
        --mainExe Deluno.exe `
        --packTitle Deluno `
        --icon (Join-Path $root "installer\deluno.ico") `
        --channel $Channel `
        --runtime $RuntimeIdentifier `
        --outputDir $output
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

    $setup = Get-ChildItem $output -Filter "*Setup*.exe" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $setup) { throw "vpk produced no Setup executable." }

    Write-Host ""
    Write-Host "Setup : $($setup.FullName)"
    Write-Host "Size  : $([math]::Round($setup.Length / 1MB, 1)) MB"
    Write-Host "SHA256: $((Get-FileHash $setup.FullName -Algorithm SHA256).Hash)"
    Write-Host "Version $Version on the $Channel channel."

    [pscustomobject]@{
        Setup   = $setup.FullName
        Version = $Version
        Channel = $Channel
        Sha256  = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
    }
}
finally {
    Pop-Location
}
