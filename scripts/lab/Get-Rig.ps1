<#
.SYNOPSIS
    Reads rig.json - where the simulation rig is. An untracked rig.local.json beside it, if present, overrides it with the real values.

.DESCRIPTION
    Dot-source this and call Get-Rig. Every lab script used to carry the rig's
    address as its own parameter default, seven copies of one fact, so moving
    the rig meant finding all seven and a default left behind pointed at a
    machine that no longer existed.

    Scripts still take -ComputerName and friends; this only supplies the
    default when nothing was passed.

.EXAMPLE
    . (Join-Path $PSScriptRoot 'Get-Rig.ps1')
    $rig = Get-Rig
    if (-not $ComputerName) { $ComputerName = $rig.host }
#>

function Get-Rig {
    [CmdletBinding()]
    param(
        # Only for a second rig, or a test. Normally leave it.
        [string] $Path
    )

    if (-not $Path) {
        $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
        $Path = Join-Path $here 'rig.json'
    }

    if (-not (Test-Path $Path)) {
        throw "The rig configuration is missing: $Path"
    }

    $rig = Get-Content -Path $Path -Raw | ConvertFrom-Json

    # rig.json holds placeholders and is committed. The real addresses and NAS
    # paths live in rig.local.json beside it, which is not committed and wins
    # over rig.json for every key it sets.
    $localPath = Join-Path (Split-Path -Parent $Path) ([System.IO.Path]::GetFileNameWithoutExtension($Path) + '.local.json')
    if (Test-Path $localPath) {
        Merge-RigObject -Base $rig -Over (Get-Content -Path $localPath -Raw | ConvertFrom-Json)
    }

    return $rig
}

function Merge-RigObject {
    param($Base, $Over)

    foreach ($p in $Over.PSObject.Properties) {
        $existing = $Base.PSObject.Properties[$p.Name]
        if ($existing -and $existing.Value -is [System.Management.Automation.PSCustomObject] -and $p.Value -is [System.Management.Automation.PSCustomObject]) {
            Merge-RigObject -Base $existing.Value -Over $p.Value
        } elseif ($existing) {
            $existing.Value = $p.Value
        } else {
            $Base | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value
        }
    }
}

function Get-RigCredential {
    [CmdletBinding()]
    param($Rig)

    if (-not $Rig) { $Rig = Get-Rig }

    # An environment variable wins, so a rig whose password is not the lab one
    # does not need the file edited.
    $password = if ($env:DELUNO_LAB_PASSWORD) { $env:DELUNO_LAB_PASSWORD } else { $Rig.password }
    if (-not $password) {
        throw 'Set DELUNO_LAB_PASSWORD; the Windows administrator password is not stored in rig.json.'
    }

    return New-Object System.Management.Automation.PSCredential(
        $Rig.userName, (ConvertTo-SecureString $password -AsPlainText -Force))
}

function Get-RigAppPassword {
    [CmdletBinding()]
    param()

    # The rig's Deluno sign-in is not stored in the repository. Set it in the
    # environment of whoever runs the lab scripts.
    if (-not $env:DELUNO_LAB_APP_PASSWORD) {
        throw 'Set DELUNO_LAB_APP_PASSWORD to the rig''s Deluno sign-in password; it is not stored in rig.json.'
    }

    return $env:DELUNO_LAB_APP_PASSWORD
}
