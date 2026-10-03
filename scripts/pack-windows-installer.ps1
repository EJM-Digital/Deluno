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

<#
  Runs a native command and judges it by its exit code.

  PowerShell's "Stop" preference treats anything a native tool writes to stderr
  as a terminating error, and plenty of them write ordinary information there -
  an "npm notice" is not a failed build. Exit code is the only signal that
  actually means success or failure.
#>
function Invoke-Native {
    param([scriptblock]$Command, [string]$What)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Command }
    finally { $ErrorActionPreference = $previous }

    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    Write-Host "== Frontend"
    # $ErrorActionPreference = "Stop" turns anything a native command writes to
    # stderr into a terminating error, and npm writes ordinary notices there -
    # so a routine "npm notice" failed the whole build. The exit code is the
    # only thing that says whether npm succeeded, so that is what is checked.
    Invoke-Native { npm.cmd run build:web } "build:web"

    Write-Host "== Payload"
    # Never reuse a previous payload. Packaging must not pick up a component the
    # build no longer produces - a removed DLL left behind here ships.
    Remove-Item -LiteralPath $payload -Recurse -Force -ErrorAction SilentlyContinue
    # -p:Version is a global MSBuild property: it does not stop at
    # Deluno.Tray.csproj, it applies to every project this publish builds,
    # including its ProjectReferences (Deluno.Worker and the rest of src\*).
    # Those projects' normal bin\ and obj\ are the SAME folders `dotnet build`,
    # publish-windows.ps1 and the IDE use for every other workflow. Publishing
    # this test/rc version into them once put a 0.0.1.0-stamped
    # Deluno.Worker.dll in the shared output, a later `publish-windows.ps1
    # -Fast` reused it unchanged (MSBuild saw its inputs as up to date), and
    # the rig deployed a Deluno.Worker that would not start.
    #
    # Relocating BaseOutputPath/BaseIntermediateOutputPath was tried and
    # reverted: the .NET SDK computes its default Compile-item excludes from
    # those same properties very early, so moving them left whatever a
    # previous ordinary build had already generated under the old obj\Release\
    # sitting there unexcluded, and the compiler tried to build both copies at
    # once (CS0579, duplicate assembly attributes).
    #
    # A `dotnet clean` of the whole solution before and after this publish is
    # blunter but reliable: before, so this run never starts from a build
    # another workflow left in the shared tree; after, so this run never
    # leaves one for another workflow to find "up to date" and reuse
    # unrebuilt. It costs a slower next build elsewhere, never a wrong one.
    $solution = Join-Path $root "Deluno.slnx"
    Invoke-Native { dotnet clean $solution -c Release --verbosity quiet } "cleaning before the tray payload build"
    Invoke-Native {
        # -p:PublishReadyToRun precompiles to native code so the Velopack
        # --veloapp-install hook (and every other cold start) does not have
        # to JIT its way through the ~800-file self-contained payload before
        # it can even reach VelopackApp.Build().Run(), which is what blew
        # Velopack's 30s hook budget on a clean VM (#968).
        dotnet publish (Join-Path $root "apps\windows-tray\Deluno.Tray.csproj") `
            -c Release `
            -r $RuntimeIdentifier `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:PublishReadyToRun=true `
            -p:Version=$Version `
            -p:DelunoUpdateChannel=$Channel `
            -o $payload
    } "publishing the tray payload"
    Invoke-Native { dotnet clean $solution -c Release --verbosity quiet } "cleaning after the tray payload build"

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
        #
        # fetch-unrar.ps1 checks the download against a pinned SHA-256 (#1124),
        # the same script the release workflow runs.
        try {
            & (Join-Path $PSScriptRoot "fetch-unrar.ps1") -RuntimeIdentifier $RuntimeIdentifier
            $unrarSource = Join-Path $root "tools\unrar\$RuntimeIdentifier"
            $unrarTarget = Join-Path $payload "tools\unrar"
            New-Item -ItemType Directory -Force -Path $unrarTarget | Out-Null
            Copy-Item -Path (Join-Path $unrarSource "*") -Destination $unrarTarget -Exclude ".sha256" -Force
            Write-Host "   UnRAR.exe bundled"
        }
        catch {
            # A download that is not the pinned file is never shipped, and it is
            # not a warning either: it stops the packaging.
            if ($_.Exception.Message -like "UnRAR checksum mismatch*") { throw }
            # Otherwise not fatal. A missing UnRAR costs one import path; a
            # failed release costs the release.
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
    # A machine that has never had vpk installed has no `vpk` on PATH at all,
    # which PowerShell treats as a terminating "command not found" error under
    # $ErrorActionPreference = Stop - not a nonzero $LASTEXITCODE from the
    # command itself. Without this try/catch that error aborted the whole
    # packaging run instead of falling through to the install below.
    $installed = $null
    try { $installed = (& vpk --version 2>$null | Select-Object -First 1) } catch { $installed = $null }
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
    Invoke-Native {
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
    } "vpk pack"

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
