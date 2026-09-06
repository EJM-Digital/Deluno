<#
.SYNOPSIS
    Reads rig.json - where the simulation rig is.

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

    return (Get-Content -Path $Path -Raw | ConvertFrom-Json)
}

function Get-RigCredential {
    [CmdletBinding()]
    param($Rig)

    if (-not $Rig) { $Rig = Get-Rig }

    # An environment variable wins, so a rig whose password is not the lab one
    # does not need the file edited.
    $password = if ($env:DELUNO_LAB_PASSWORD) { $env:DELUNO_LAB_PASSWORD } else { $Rig.password }

    return New-Object System.Management.Automation.PSCredential(
        $Rig.userName, (ConvertTo-SecureString $password -AsPlainText -Force))
}
