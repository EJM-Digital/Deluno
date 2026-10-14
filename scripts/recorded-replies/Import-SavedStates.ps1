<#
.SYNOPSIS
    Turns the recordings a stopped run left in the VM (C:\golden\recorded\saved-states.jsonl, written by Save-State in Record.Lib.ps1) into the files
    a finished run writes, and scrubs them. Copy the jsonl file out of the VM first; nothing here touches a VM.
.EXAMPLE
    ./scripts/recorded-replies/Import-SavedStates.ps1 -File .\saved-states.jsonl -Name <vm name> -Name <vm user>
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $File, [string[]] $Name = @(), [string] $OutDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot 'tests\fixtures\recorded' }
$written = [System.Collections.Generic.List[string]]::new()
foreach ($line in Get-Content -LiteralPath $File) {
    if (-not $line.Trim()) { continue }
    $recording = $line | ConvertFrom-Json
    $folder = Join-Path $OutDirectory (Join-Path $recording.app $recording.version)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $path = Join-Path $folder ($recording.state + '.json')
    [IO.File]::WriteAllText($path, (($recording | ConvertTo-Json -Depth 30) + "`n"), (New-Object Text.UTF8Encoding($false)))
    $written.Add($path)
}
$nameArgs = $Name | ForEach-Object { '--name'; $_ }
& node (Join-Path $PSScriptRoot 'scrub.mjs') @($written) @nameArgs
if ($LASTEXITCODE -ne 0) { throw 'scrub.mjs failed' }
& node (Join-Path $PSScriptRoot 'scrub.mjs') @($written) @nameArgs --check
if ($LASTEXITCODE -ne 0) { throw 'A recording still carries something private after scrubbing.' }
Write-Host "Wrote $($written.Count) recordings to $OutDirectory."
