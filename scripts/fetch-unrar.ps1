<#
.SYNOPSIS
    Fetches the official UnRAR for Windows, checks it against a pinned SHA-256,
    and leaves UnRAR.exe and its licence in tools\unrar\win-x64.

.DESCRIPTION
    Extraction-only use of the official binary is licence-clean; the source
    licence forbids building a competing archiver from it, which is not what
    this is. The upstream licence ships beside the binary.

    rarlab.com publishes only the current file at one address, so the pin below
    names that file by its hash. When RARLAB releases a new UnRAR the download no
    longer matches and this script stops with a checksum mismatch; that is the
    point. Look at the new release, then change $pinnedSha256 to the hash of the
    file you looked at. The two callers (the release workflow and
    pack-windows-installer.ps1) treat a mismatch as a failed release and any
    other failure (rarlab unreachable, say) as "ship without UnRAR, with a
    warning", as they always have.

    The result is cached in tools\unrar (gitignored). The stamp file holds the
    pinned hash, so a changed pin fetches again.

    Both the release workflow and pack-windows-installer.ps1 call this one
    script. They used to carry two copies of the download, neither of which
    checked what it had downloaded (#1124).
#>
param(
    [string]$RuntimeIdentifier = "win-x64",

    [switch]$Force
)

$ErrorActionPreference = "Stop"

# The pin.
$url = "https://www.rarlab.com/rar/unrarw64.exe"
$pinnedSha256 = "fecc0aa6d3f5434113bc32d4d151c2e9e0938155f71c356ed150bcecf3770f21"

if ($RuntimeIdentifier -ne "win-x64") {
    throw "fetch-unrar.ps1 only knows win-x64; $RuntimeIdentifier needs its own download."
}

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "tools\unrar\$RuntimeIdentifier"
$stamp = Join-Path $target ".sha256"

if (-not $Force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $pinnedSha256) -and (Test-Path (Join-Path $target "UnRAR.exe"))) {
    Write-Host "UnRAR ($pinnedSha256) is already in $target."
    return
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("deluno-unrar-" + [System.Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    $self = Join-Path $staging "unrarw64.exe"
    $previousProgress = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"
    try { Invoke-WebRequest -Uri $url -OutFile $self -UseBasicParsing }
    finally { $ProgressPreference = $previousProgress }

    $actual = (Get-FileHash -Path $self -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $pinnedSha256) {
        throw "UnRAR checksum mismatch: expected $pinnedSha256 but $url is now $actual. Nothing was run. If RARLAB has published a new UnRAR, check it and update the pin in scripts/fetch-unrar.ps1."
    }
    Write-Host "SHA-256 matches the pin."

    # The download is a self-extracting archive. -s is the silent switch:
    # without it the extractor opens a WinRAR dialog and waits, which on a
    # runner means the job hangs rather than fails, and on a packaging machine
    # means a dialog on someone's desktop. The timeout is belt and braces for a
    # future build that decides to prompt anyway.
    $extract = Join-Path $staging "extract"
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    $process = Start-Process -FilePath $self -ArgumentList "-s", "-d$extract" -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw "UnRAR self-extractor did not finish in 60s; it may be waiting for input."
    }

    $unrar = Get-ChildItem $extract -Recurse -Filter "UnRAR.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $unrar) {
        throw "UnRAR.exe was not in the extracted files."
    }

    if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item $unrar.FullName (Join-Path $target "UnRAR.exe") -Force
    $license = Get-ChildItem $extract -Recurse -Filter "license.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $target "license.txt") -Force }

    Set-Content -Path $stamp -Value $pinnedSha256 -Encoding ascii
    Write-Host "UnRAR is in $target."
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
