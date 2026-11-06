<#
.SYNOPSIS
    One-time preparation of the golden VM's clean checkpoint for the uTorrent and rTorrent scenarios (#1276). Run INSIDE the VM,
    in Windows PowerShell 5.1, before the 'clean' checkpoint is taken.

.DESCRIPTION
    The suite installs both clients itself on every run (ExtraClients.Lib.ps1), but turning the Windows Subsystem for Linux on needs a
    restart, which a run cannot make. So: run this once; if it says to restart, restart the VM and run it again. It then has the
    Ubuntu root file system imported (checked against the pinned SHA-256), rTorrent and nginx installed at the pinned versions, and
    uTorrent installed and checked, so a run only has to start them. Take the clean checkpoint after it says "ready".
    It starts nothing and adds nothing to Deluno.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Suite.Lib.ps1')
. (Join-Path $PSScriptRoot 'ExtraClients.Lib.ps1')

$rt = Install-XcRtorrent
if ($rt -eq 'reboot') {
    Write-Host 'The Windows Subsystem for Linux was just turned on. Restart this VM, then run this script again.'
    return
}
$ut = Install-XcUTorrent
Write-Host "ready: rTorrent $rt (Ubuntu package under WSL1) and uTorrent $ut are installed. Take the clean checkpoint now."
