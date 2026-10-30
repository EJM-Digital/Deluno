<#
    Two ways an import goes wrong on a real machine (#1278):

      importfaults.bad-files      the download check is turned on and a real client finishes a film whose file is corrupt (its header
                                  is zeroed, so the real ffprobe cannot open it) and another whose picture is far smaller than its
                                  name claims; each is removed from the real client with its files, blocklisted, never handed to
                                  Weir or imported, and written up in Activity saying it is searching for another copy
      importfaults.kill-mid-copy  Deluno's own process is killed while a large file is being copied into the library, then started
                                  again: it recovers by itself, leaves no half-copied file, staging file or restart mark, and the
                                  source is still whole
#>

function Get-FreeBytes {
    param([Parameter(Mandatory)] [string] $Drive)
    (New-Object System.IO.DriveInfo($Drive)).AvailableFreeSpace
}

function Invoke-GroupImportFaults {
    Invoke-Scenario -Id 'importfaults.bad-files' -Title 'A corrupt file and a too-small picture, finished by a real client, are removed from it, blocklisted and never imported' -Body {
        $saved = $script:Fx.Base['small']
        $was = [bool](Invoke-Deluno -Path '/api/settings').downloadIntegrityCheckEnabled
        $dir = Join-Path (Get-SuiteRoot) 'base\integrity'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        try {
            # A real video with its first 64 KB zeroed: the real ffprobe cannot open it.
            $corrupt = Join-Path $dir 'corrupt.mkv'
            Copy-Item -LiteralPath $saved -Destination $corrupt -Force
            $fs = [System.IO.File]::Open($corrupt, 'Open', 'ReadWrite', 'None')
            try { $fs.Position = 0; $fs.Write((New-Object byte[] 65536), 0, 65536) } finally { $fs.Dispose() }
            Invoke-Deluno -Method PATCH -Path '/api/settings' -Body @{ downloadIntegrityCheckEnabled = $true } | Out-Null
            Add-Check 'The download check is switched on' ([bool](Invoke-Deluno -Path '/api/settings').downloadIntegrityCheckEnabled) ''

            foreach ($case in @(
                    @{ Name = 'corrupt'; File = $corrupt; Why = 'will not open or play' },
                    @{ Name = 'small picture'; File = $script:Fx.Base['extra']; Why = 'resolution' })) {
                $script:Fx.Base['small'] = $case.File
                $film = New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label ('bad' + ($case.Name -replace ' ', ''))
                $script:Fx.Base['small'] = $saved
                Send-Film -Film $film -Kind 'qbittorrent' | Out-Null
                $blocked = Wait-Until -TimeoutSec 360 -IntervalSec 5 -Condition {
                    $b = Try-Deluno -Path '/api/blocked-releases?take=200'
                    if ($b.Status -eq 200 -and $b.Raw -match [regex]::Escape($film.Release)) { $b } else { $null }
                }
                Save-Evidence -Name ("importfaults-{0}-blocklist.json" -f ($case.Name -replace ' ', '')) -Text $(if ($blocked.Ok) { $blocked.Value.Raw } else { 'not blocklisted' })
                Add-Check "$($case.Name): the release is blocklisted" $blocked.Ok ("after {0} s" -f $blocked.Seconds)
                $removed = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition { -not (Get-ClientTorrent -Kind 'qbittorrent' -Hash $film.Hash -For $film.Release).Present }
                Add-Check "$($case.Name): the real client has forgotten the download" $removed.Ok ("after {0} s" -f $removed.Seconds)
                $leftovers = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { @(Get-FilmLeftovers -Film $film).Count -eq 0 }
                Add-Check "$($case.Name): its files are gone from the client's folders" $leftovers.Ok (@(Get-FilmLeftovers -Film $film | Select-Object -First 3 | ForEach-Object { $_.FullName }) -join '; ')
                Add-Check "$($case.Name): it was never handed to Weir" (@(Find-Handoffs -Name $film.Release).Count -eq 0) ''
                Add-Check "$($case.Name): it was never imported" ($null -eq (Find-Imported -Title $film.Title)) ''
                $said = Wait-Until -TimeoutSec 60 -IntervalSec 3 -Condition { @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match 'searching for another copy' }).Count -gt 0 }
                Add-Check "$($case.Name): Activity says it removed it, blocklisted it and is searching for another copy" $said.Ok ((@(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) }) | Select-Object -First 2) -join ' || ')
            }
        }
        finally {
            $script:Fx.Base['small'] = $saved
            try { Invoke-Deluno -Method PATCH -Path '/api/settings' -Body @{ downloadIntegrityCheckEnabled = $was } | Out-Null } catch { }
        }
    }

    Invoke-Scenario -Id 'importfaults.kill-mid-copy' -Title 'Deluno is killed in the middle of copying a large file into the library and started again: no half-copied file is left and the source is whole' -Body {
        $token = New-FilmToken
        $srcRoot = (Join-Path $script:VolDir "src_Kill$token")
        $libRoot = "C:\Media\Kill$token"
        $lib = $null; $movie = $null
        try {
            # A real video with zeros after it: a big file that the real ffprobe still opens, and that takes a few seconds to copy.
            $size = [long]30GB
            $free = Get-FreeBytes -Drive 'C'
            if ($free -lt [long]75GB) { $size = [long]8GB }
            $title = "Kill Copy $token"
            $src = New-VolumeSource -Folder $srcRoot -Title $title
            $fs = [System.IO.File]::Open($src, 'Open', 'ReadWrite', 'None')
            try { $fs.SetLength($size) } finally { $fs.Dispose() }
            (Get-Item -LiteralPath $src).LastWriteTime = (Get-Date).AddHours(-1)
            Add-Note ("Source is {0:N0} MB; the drive has {1:N0} MB free" -f ($size / 1MB), ($free / 1MB))
            $lib = New-VolumeLibrary -Name "Kill $token" -Root $libRoot
            $movie = New-VolumeMovie -Title $title -Library $lib

            # Start the import without waiting for it, then kill Deluno as soon as the copy has begun.
            $pv = [ordered]@{ sourcePath = $src; fileName = (Split-Path $src -Leaf); mediaType = 'movies'; title = $title; year = 2008; genres = @(); tags = @(); libraryId = $lib.id }
            $job = Start-Job -ScriptBlock {
                param($url, $token, $body)
                try {
                    Invoke-WebRequest -Uri "$url/api/filesystem/import/execute-batch" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body $body -UseBasicParsing -TimeoutSec 600 | Out-Null
                } catch { }
            } -ArgumentList $script:S.DelunoUrl, $script:S.Token, (@{ items = @(@{ preview = $pv; transferMode = 'copy'; overwrite = $false; allowCopyFallback = $true }) } | ConvertTo-Json -Depth 8)
            # A tight look at the library folder (no pauses worth the name): the copy of a big file lasts only seconds.
            $caught = $null
            $until = (Get-Date).AddSeconds(180)
            while (-not $caught -and (Get-Date) -lt $until) {
                if (Test-Path -LiteralPath $libRoot) {
                    foreach ($p in [System.IO.Directory]::EnumerateFiles($libRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
                        $len = 0L
                        try { $len = (New-Object System.IO.FileInfo $p).Length } catch { continue }
                        if ((Split-Path $p -Leaf) -match "deluno" -and $p -notmatch "[.]json$") { $caught = [pscustomobject]@{ Name = (Split-Path $p -Leaf); Length = $len }; break }
                    }
                }
                if (-not $caught) { Start-Sleep -Milliseconds 15 } # reason: a pause between looks inside a loop bounded by its deadline
            }
            $begun = [pscustomobject]@{ Ok = [bool]$caught; Value = $caught }
            Get-Process -Name "Deluno" -ErrorAction SilentlyContinue | Stop-Process -Force # killed the moment the temporary copy is seen
            Add-Check 'The copy was under way (the temporary copy of the file was on disk, not yet moved into place)' $begun.Ok $(if ($begun.Ok) { "$($begun.Value.Name) $([math]::Round($begun.Value.Length / 1MB)) MB" } else { 'the copy finished or never began before it could be caught' })
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            $dead = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue) -and -not (Test-DelunoAnswering) }
            Add-Check 'Deluno was killed (no process, nothing answering)' $dead.Ok ''
            $mid = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length })
            Add-Note ("Left on disk by the kill: " + ($mid -join '; '))

            $up = Restore-DelunoTray
            Add-Check 'Deluno starts again and answers' $up ''
            if (-not $up) { return }
            # Recovery runs before import planning: the half-copied file and its restart mark are dealt with, whichever way.
            $settled = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition {
                $rest = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -notin '.mkv', '.mp4', '.avi' })
                if ($rest.Count -eq 0) { $true } else { $null }
            }
            $files = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue)
            Save-Evidence -Name 'importfaults-kill-after.json' -Text (($files | ForEach-Object { [pscustomobject]@{ name = $_.FullName; length = $_.Length } }) | ConvertTo-Json)
            Add-Check 'No half-copied file, staging file or restart mark is left in the library' $settled.Ok (($files | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length }) -join '; ')
            $videos = @($files | Where-Object { $_.Extension -in '.mkv', '.mp4', '.avi' })
            Add-Check 'Either one whole film is in the library or nothing is (never a partial one)' (($videos.Count -eq 0) -or ($videos.Count -eq 1 -and $videos[0].Length -eq $size)) (($videos | ForEach-Object { '{0} {1} of {2}' -f $_.Name, $_.Length, $size }) -join '; ')
            $srcWhole = (Test-Path -LiteralPath $src) -and (Get-Item -LiteralPath $src).Length -eq $size
            Add-Check 'The film is never lost: the library has it whole or the source still does' ($srcWhole -or ($videos.Count -eq 1 -and $videos[0].Length -eq $size)) "source whole: $srcWhole; library: $($videos.Count) file(s)"
            $said = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition { @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($title) -and $_ -match '(?i)restart|interrupted|stopped while importing' }).Count -gt 0 }
            Add-Check 'Activity says in words what the restart did about the interrupted import' $said.Ok ((@(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($title) }) | Select-Object -First 2) -join ' || ')
        }
        finally {
            Remove-VolumeMovie -Movie $movie
            Remove-VolumeLibrary -Library $lib
            Remove-Item -LiteralPath $srcRoot, $libRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
