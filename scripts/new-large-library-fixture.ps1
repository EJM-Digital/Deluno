<#
.SYNOPSIS
    Builds a synthetic media library on disk for testing Deluno at scale.

.DESCRIPTION
    Deluno's target is 20,000+ movies and TV shows with no assumed upper bound.
    Most defects at that size cannot be found in the dev fixtures, which hold a
    handful of titles - and unlike an unbounded SQL query, an import defect only
    shows up against a real tree.

    The files this writes are empty. Deluno's existing-library import reads file
    names and sizes, never file contents, so 20,000 zero-byte files exercise the
    same code path as 20,000 real ones at none of the disk cost.

    Nothing here touches the real database. Point a new library at the folder
    this creates, or seed a COPY of the dev database - never the dev database
    itself, which holds hand-built fixtures.

.PARAMETER Path
    Where to build the tree. Created if missing; must be empty or -Force given.

.PARAMETER Movies
    How many movie folders to create.

.PARAMETER Series
    How many show folders to create.

.PARAMETER SeasonsPerSeries
    Seasons per show.

.PARAMETER EpisodesPerSeason
    Episodes per season. The default of 12 with 4 seasons puts a show at 48
    episode files, close to the shape of a real library.

.EXAMPLE
    .\new-large-library-fixture.ps1 -Path D:\deluno-fixture\movies -Movies 20000

.EXAMPLE
    .\new-large-library-fixture.ps1 -Path D:\deluno-fixture\tv -Movies 0 -Series 2000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [int]$Movies = 20000,

    [int]$Series = 0,

    [int]$SeasonsPerSeries = 4,

    [int]$EpisodesPerSeason = 12,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Deliberately boring generated names. Real libraries are messier than this, and
# the messy cases belong in unit tests where the expected parse can be asserted;
# what this fixture is for is volume.
$adjectives = @('Silent', 'Crimson', 'Hollow', 'Northern', 'Broken', 'Golden', 'Distant', 'Quiet', 'Iron', 'Pale')
$nouns = @('Harbour', 'Signal', 'Winter', 'Machine', 'Orchard', 'Circuit', 'Meridian', 'Lantern', 'Current', 'Archive')
# Real release names carry more than a resolution: the codec, the audio layout
# and the release group are all read from them, so a fixture that omits them
# exercises none of that parsing.
$qualities = @('1080p.BluRay', '2160p.WEB-DL', '720p.HDTV', '1080p.WEBRip')
$videoCodecs = @('x264', 'x265', 'H.264', 'AV1', 'XviD')
$audio = @('DTS-HD.MA.5.1', 'TrueHD.Atmos.7.1', 'DDP5.1', 'AAC2.0', 'AC3.5.1')
$groups = @('SPARKS', 'NTb', 'FLUX', 'TERMiNAL', 'CMRG')

if (Test-Path $Path) {
    $existing = @(Get-ChildItem -LiteralPath $Path -Force)
    if ($existing.Count -gt 0 -and -not $Force) {
        throw "$Path is not empty. Pass -Force to add to it anyway."
    }
} else {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function New-EmptyFile([string]$FilePath) {
    # Not New-Item -Force: that truncates an existing file, and this script is
    # allowed to run over a tree somebody is already using.
    if (-not (Test-Path -LiteralPath $FilePath)) {
        New-Item -ItemType File -Path $FilePath | Out-Null
    }
}

function Get-Title([int]$Index) {
    $adjective = $adjectives[$Index % $adjectives.Count]
    $noun = $nouns[[math]::Floor($Index / $adjectives.Count) % $nouns.Count]
    return "$adjective.$noun.$Index"
}

$started = Get-Date

for ($index = 1; $index -le $Movies; $index++) {
    $year = 1950 + ($index % 76)
    $quality = $qualities[$index % $qualities.Count]
    $name = "$(Get-Title $index).$year.$quality.$($videoCodecs[$index % $videoCodecs.Count]).$($audio[$index % $audio.Count])-$($groups[$index % $groups.Count])"
    $folder = Join-Path $Path $name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    New-EmptyFile (Join-Path $folder "$name.mkv")

    if ($index % 1000 -eq 0) {
        Write-Host "  $index / $Movies movies"
    }
}

for ($index = 1; $index -le $Series; $index++) {
    $year = 1990 + ($index % 36)
    $showName = "$(Get-Title $index).$year"
    $showFolder = Join-Path $Path $showName
    New-Item -ItemType Directory -Path $showFolder -Force | Out-Null

    for ($season = 1; $season -le $SeasonsPerSeries; $season++) {
        $seasonFolder = Join-Path $showFolder ("Season {0:D2}" -f $season)
        New-Item -ItemType Directory -Path $seasonFolder -Force | Out-Null

        for ($episode = 1; $episode -le $EpisodesPerSeason; $episode++) {
            $quality = $qualities[($index + $episode) % $qualities.Count]
            $fileName = "{0}.S{1:D2}E{2:D2}.{3}.{4}.{5}-{6}.mkv" -f $showName, $season, $episode, $quality,
                $videoCodecs[$episode % $videoCodecs.Count], $audio[$episode % $audio.Count], $groups[$episode % $groups.Count]
            New-EmptyFile (Join-Path $seasonFolder $fileName)
        }
    }

    if ($index % 100 -eq 0) {
        Write-Host "  $index / $Series shows"
    }
}

$elapsed = (Get-Date) - $started
$episodeTotal = $Series * $SeasonsPerSeries * $EpisodesPerSeason

Write-Host ""
Write-Host "Built $Movies movie folders and $Series shows ($episodeTotal episode files) in $Path"
Write-Host ("Took {0:N1}s" -f $elapsed.TotalSeconds)
