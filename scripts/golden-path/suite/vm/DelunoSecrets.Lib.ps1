# Reads, inside the VM, what Deluno keeps protected for itself (DPAPI): the secret it shares with Weir and the logins it saved for the
# bundled apps. Shared by the recorder (scripts/recorded-replies/Record.Secrets.ps1, which also registers each value it reads so it is scrubbed
# from every recording) and the scenario suite (G04-Resend.ps1, which asks Weir for a hand-off's status). Windows PowerShell 5.1. A value is
# never printed. Register-Secret is the recorder's, and is called only where it exists.

Add-Type -AssemblyName System.Security

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
                        if ($secret) { if (Get-Command Register-Secret -ErrorAction SilentlyContinue) { Register-Secret $secret }; return $secret }
                    }
                }
            }
        }
    }
    $null
}
