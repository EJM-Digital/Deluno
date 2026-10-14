# Reads, inside the VM, the logins the installer and Deluno wrote there, so no secret is ever passed to the capture
# script, printed or sent back to the host (#1237). Runs INSIDE the VM after Record.Lib.ps1.
#
#  - qBittorrent's generated login (and Transmission's, when Deluno gave it one) is in Deluno's component-credentials
#    file under the data root: DPAPI-protected, readable here (apps/windows-tray, Deluno.Api BundledComponentCredentials).
#  - Weir's webhook secret is the secret of Deluno's processor connection to Weir: DPAPI-protected in Deluno's database (read by
#    scripts/golden-path/suite/vm/DelunoSecrets.Lib.ps1, shared with the scenario suite; it calls Register-Secret below).
#  - SABnzbd's API key is in its own sabnzbd.ini; NZBGet and Deluge keep their shipped logins (build-rules.md).
#
# Every value read here is registered, and Save-State replaces it with <scrubbed> in whatever it writes, as a second
# line behind scrub.mjs. Nothing here writes a value to the console.

Add-Type -AssemblyName System.Security

$script:KnownSecrets = [System.Collections.Generic.List[string]]::new()
function Register-Secret([string] $Value) { if ($Value -and $Value.Length -ge 4 -and -not $script:KnownSecrets.Contains($Value)) { $script:KnownSecrets.Add($Value) } }

# Replaces every registered secret in a piece of text.
function Remove-KnownSecrets([string] $Text) {
    foreach ($secret in $script:KnownSecrets) { $Text = $Text.Replace($secret, '<scrubbed>') }
    $Text
}

# The login the installer generated for an app whose kind matches the pattern (for example 'qbittorrent').
function Get-ComponentLogin([string] $KindPattern) {
    foreach ($root in Find-DelunoDataRoots) {
        $file = Get-ChildItem -Path $root -Filter 'component-credentials.json' -Recurse -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $file) { continue }
        $entries = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        foreach ($kind in $entries.PSObject.Properties.Name) {
            if ($kind -notmatch $KindPattern) { continue }
            $password = Unprotect-DelunoSecret $entries.$kind.Password "bundled-component:$kind"
            if ($password) {
                Register-Secret $password; Register-Secret $entries.$kind.Username
                return [pscustomobject]@{ User = $entries.$kind.Username; Password = $password }
            }
        }
    }
    $null
}
