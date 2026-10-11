# Reads, inside the VM, the logins the installer and Deluno wrote there, so no secret is ever passed to the capture
# script, printed or sent back to the host (#1237). Runs INSIDE the VM after Record.Lib.ps1.
#
#  - qBittorrent's generated login (and Transmission's, when Deluno gave it one) is in Deluno's component-credentials
#    file under the data root: DPAPI-protected, readable here (apps/windows-tray, Deluno.Api BundledComponentCredentials).
#  - Weir's webhook secret is the secret of Deluno's processor connection to Weir: DPAPI-protected in Deluno's database.
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

function Find-DelunoDataRoots {
    $roots = foreach ($base in $env:ProgramData, $env:LOCALAPPDATA, $env:APPDATA) {
        if ($base -and (Test-Path $base)) { Get-ChildItem -Path $base -Filter 'Deluno*' -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName }
    }
    @($roots)
}

# DPAPI text as Deluno writes it: dpapi:v1:<base64>, entropy "Deluno.Platform.Secrets:<purpose>". The scope depends on
# who wrote it (the tray uses the machine, Deluno's own store the user), so both are tried.
function Unprotect-DelunoSecret([string] $Blob, [string] $Purpose) {
    if (-not $Blob -or -not $Blob.StartsWith('dpapi:v1:')) { return $null }
    try { $cipher = [Convert]::FromBase64String($Blob.Substring('dpapi:v1:'.Length)) } catch { return $null }
    $entropy = [Text.Encoding]::UTF8.GetBytes('Deluno.Platform.Secrets:' + $Purpose)
    foreach ($scope in 'LocalMachine', 'CurrentUser') {
        try { return [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($cipher, $entropy, [Security.Cryptography.DataProtectionScope]::$scope)) } catch { }
    }
    $null
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

# Weir's webhook secret: the secret_value of Deluno's processor connection to Weir. Deluno's database is SQLite; rather
# than load a SQLite library the file is scanned for the protected values and the 32-hex connection ids, and each pair
# is tried (the id is part of the DPAPI entropy), so only the right pair opens.
function Get-WeirWebhookSecret {
    foreach ($root in Find-DelunoDataRoots) {
        foreach ($db in Get-ChildItem -Path $root -Include '*.db', '*.db-wal', '*.sqlite' -Recurse -Depth 4 -ErrorAction SilentlyContinue) {
            try {
                $stream = [IO.File]::Open($db.FullName, 'Open', 'Read', 'ReadWrite')
                try { $bytes = New-Object byte[] $stream.Length; $null = $stream.Read($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
            } catch { continue }
            $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
            if ($text -notmatch 'processor_connections|intake/webhook') { continue }
            # A row starts with its id, so the id of a protected value is among the 32-hex ids just before it. The value may run on
            # into the next bytes of the page, so every length that could be a whole DPAPI value is tried, longest first.
            foreach ($m in [regex]::Matches($text, 'dpapi:v1:[A-Za-z0-9+/=]{20,}')) {
                $from = [Math]::Max(0, $m.Index - 1200)
                $ids = [regex]::Matches($text.Substring($from, $m.Index - $from), '(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])') | ForEach-Object Value | Select-Object -Unique
                $encoded = $m.Value.Substring('dpapi:v1:'.Length)
                for ($length = $encoded.Length - ($encoded.Length % 4); $length -ge 80; $length -= 4) {
                    $blob = 'dpapi:v1:' + $encoded.Substring(0, $length)
                    foreach ($id in $ids) {
                        $secret = Unprotect-DelunoSecret $blob "processor-connection:$id"
                        if ($secret) { Register-Secret $secret; return $secret }
                    }
                }
            }
        }
    }
    $null
}
