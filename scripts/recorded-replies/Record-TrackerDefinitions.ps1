# Records the tracker-definition archive Deluno downloads (#756, #1181, #1301): the real unauthenticated GET of
# https://codeload.github.com/Prowlarr/Indexers/zip/refs/heads/master, shortened to the entries Deluno reads or must ignore.
#
# The real archive is large. The zip kept beside the recording holds, byte for byte with their upstream names, the definitions in
# definitions/v11 that the tests name (-Keep), the first -Extra others from v11 and the top README (which Deluno must ignore). Every kept entry's file time is set to 1980-01-01. Nothing in a definition is a secret.
#
#   pwsh scripts/recorded-replies/Record-TrackerDefinitions.ps1
param(
    [string[]]$Keep = @('yts', 'eztv', 'thepiratebay', '1337x', 'nyaasi', 'torrentleech', 'iptorrents', 'passthepopcorn', 'hdbits', 'bitsearch'),
    [int]$Extra = 3
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $repo 'tests\fixtures\recorded\github\2022-11-28'
$work = Join-Path ([IO.Path]::GetTempPath()) ("deluno-defs-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $url = 'https://codeload.github.com/Prowlarr/Indexers/zip/refs/heads/master'
    $source = Join-Path $work 'real.zip'
    Invoke-WebRequest -Uri $url -OutFile $source -UserAgent 'Deluno-test-recording'
    $realBytes = (Get-Item $source).Length
    $zip = [IO.Compression.ZipFile]::OpenRead($source)
    $names = $zip.Entries | ForEach-Object { $_.FullName }
    $top = ($names | Select-Object -First 1).Split('/')[0]
    $v11 = $names | Where-Object { $_ -like "$top/definitions/v11/*.yml" -and ($_.Substring(("$top/definitions/v11/").Length) -notlike '*/*') }
    $wanted = @()
    foreach ($id in $Keep) { $e = "$top/definitions/v11/$id.yml"; if ($v11 -contains $e) { $wanted += $e } }
    $wanted += ($v11 | Where-Object { $wanted -notcontains $_ } | Select-Object -First $Extra)
    $wanted += ($names | Where-Object { $_ -like "$top/definitions/v10/*.yml" } | Select-Object -First 2)
    $wanted += ($names | Where-Object { $_ -like "$top/definitions/v11/*/*.yml" } | Select-Object -First 1)
    $wanted += "$top/README.md"
    $wanted = $wanted | Where-Object { $names -contains $_ } | Select-Object -Unique
    $kept = Join-Path $out 'prowlarr-indexers-archive.zip'
    if (Test-Path $kept) { Remove-Item $kept }
    $new = [IO.Compression.ZipFile]::Open($kept, 'Create')
    foreach ($name in $wanted) {
        $entry = $zip.GetEntry($name)
        $copy = $new.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $copy.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $in = $entry.Open(); $dest = $copy.Open(); $in.CopyTo($dest); $dest.Dispose(); $in.Dispose()
    }
    $new.Dispose(); $zip.Dispose()
    $sha = (Get-FileHash $kept -Algorithm SHA256).Hash
    $size = (Get-Item $kept).Length
    $recording = [ordered]@{
        app = 'github'; version = '2022-11-28'; state = 'prowlarr-indexers-archive'
        recordedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        note = "A real unauthenticated GET on $((Get-Date).ToString('d MMM yyyy')) of $($url.Replace('https://', '')) (the source of Deluno's tracker definition updates). The real archive is $([math]::Round($realBytes / 1MB, 1)) MB. The zip beside this file holds $($wanted.Count) of its entries, each copied byte for byte with its upstream name: definitions in definitions/v11 (the ones the tests name, and the first $Extra others) and the top README, which Deluno must ignore. Every other entry is left out (the upstream repository holds no v10 folder or v11 sub-folder today, so a test of those ignored paths builds its own entries). The entries' file times are set to 1980-01-01."
        exchanges = @(@{
            request = [ordered]@{ method = 'GET'; path = $url.Replace('https://', ''); body = $null }
            reply = [ordered]@{ status = 200; headers = [ordered]@{ 'content-type' = 'application/zip' }; bodyFile = 'prowlarr-indexers-archive.zip'; bodySha256 = $sha; bodyBytes = $size }
        })
    }
    ($recording | ConvertTo-Json -Depth 8) + "`n" | Set-Content -Path (Join-Path $out 'prowlarr-indexers-archive.json') -Encoding utf8NoBOM
    Write-Host "recorded $($wanted.Count) entries, $size bytes"
    $wanted | ForEach-Object { Write-Host "  $_" }
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
