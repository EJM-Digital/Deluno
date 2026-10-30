<#
    Real disks and shares (#1278): a second volume, an exFAT volume, a small volume, an SMB share, a share that does not answer and
    a share that goes offline. Every scenario makes what it needs inside the VM and takes it away again, so the VM is as it was.

      volumes.second-drive   a library on a second NTFS volume (V:): a hard link cannot cross drives, so the copy fallback, the refusal
                             without it, a move across drives, the free space listed for the volume, a volume too full for the minimum,
                             and a whole-folder move that cannot cross drives (or meets a file held open) explained in plain words
      volumes.exfat-volume   an exFAT volume mounted in a folder of C: (the roots look alike, the OS refuses the hard link): the fallback
                             copy, and the refusal without it
      volumes.smb-share      a library on a loopback SMB share (\\localhost\...): an import lands whole with no staging name left, a move
                             onto it, the share listed once with its own space, Review/Import existing change nothing, removal and
                             restore across the share, a read-only share refuses a rename without a half-moved folder, a share that goes
                             offline mid-check marks nothing missing and comes back
      volumes.dead-share     a share that never answers (a documentation address, nothing routes there): the library is reported not
                             reachable within the 5 s the check allows and the page that reads it is not held up
#>

$script:VolDir = 'C:\golden\suite\vols'

function Invoke-DiskPartLines {
    param([Parameter(Mandatory)] [string[]] $Lines)
    $f = Join-Path $env:TEMP ('dp-{0}.txt' -f [guid]::NewGuid().ToString('N'))
    try {
        Set-Content -Path $f -Value $Lines -Encoding ascii
        (& diskpart.exe /s $f 2>&1 | Out-String)
    }
    finally { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
}

# Takes a test volume away (detach the virtual disk, delete its file); safe when it is not there.
function Remove-TestVolume {
    param([Parameter(Mandatory)] [string] $Name)
    $vhd = Join-Path $script:VolDir "$Name.vhdx"
    if (Test-Path -LiteralPath $vhd) {
        Invoke-DiskPartLines -Lines @("select vdisk file=`"$vhd`"", 'detach vdisk') | Out-Null
        Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { try { Remove-Item -LiteralPath $vhd -Force -ErrorAction Stop; $true } catch { $false } } | Out-Null
    }
}

# Makes a volume: a virtual disk of SizeMb, formatted Fs, given a drive letter or mounted in an empty folder. Returns its root.
function New-TestVolume {
    param(
        [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [int] $SizeMb,
        [ValidateSet('ntfs', 'exfat', 'fat32')] [string] $Fs = 'ntfs', [string] $Letter, [string] $MountFolder
    )
    New-Item -ItemType Directory -Force -Path $script:VolDir | Out-Null
    Remove-TestVolume -Name $Name
    $vhd = Join-Path $script:VolDir "$Name.vhdx"
    $assign = if ($Letter) { "assign letter=$Letter" } else { New-Item -ItemType Directory -Force -Path $MountFolder | Out-Null; "assign mount=`"$MountFolder`"" }
    $out = Invoke-DiskPartLines -Lines @(
        "create vdisk file=`"$vhd`" maximum=$SizeMb type=expandable", 'attach vdisk', 'create partition primary',
        "format fs=$Fs quick label=GOLD$($Name.Substring(0, [math]::Min(6, $Name.Length)).ToUpper())", $assign)
    $root = if ($Letter) { "${Letter}:\" } else { $MountFolder }
    $up = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { Test-Path -LiteralPath $root }
    if (-not $up.Ok) { throw "The $Fs volume did not appear at $root. diskpart said: $out" }
    Add-Note "Made a $SizeMb MB $Fs volume at $root"
    $root
}

# Removes the folder a volume was mounted in, once the volume is gone.
function Remove-MountFolder {
    param([string] $Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) { Remove-Item -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue }
}

# A library on a path; the folders are made first.
function New-VolumeLibrary {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Root, [string] $Downloads)
    if (-not (Test-Path -LiteralPath $Root)) { New-Item -ItemType Directory -Force -Path $Root | Out-Null }
    if ($Downloads) { New-Item -ItemType Directory -Force -Path $Downloads | Out-Null }
    $profile = (Get-Library -MediaType 'movies').qualityProfileId
    $body = @{
        name = $Name; mediaType = 'movies'; purpose = 'Main library'; rootPath = $Root
        qualityProfileId = $profile; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
        removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
    }
    if ($Downloads) { $body.downloadsPath = $Downloads }
    $r = Try-Deluno -Method POST -Path '/api/libraries' -Body $body
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "Making the library $Name answered $($r.Status). $($r.Raw)") }
    $r.Data
}

# A source film: a copy of the small test video, with a name that looks like a release, written an hour ago so it counts as settled.
function New-VolumeSource {
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] [string] $Title, [int] $Year = 2008)
    $dir = Join-Path $Folder ("{0}.{1}.1080p.WEB-DL.x264-GOLDEN" -f ($Title -replace ' ', '.'), $Year)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir ("{0}.{1}.1080p.WEB-DL.x264-GOLDEN.mkv" -f ($Title -replace ' ', '.'), $Year)
    Copy-Item -LiteralPath $script:Fx.Base['small'] -Destination $file -Force
    (Get-Item -LiteralPath $file).LastWriteTime = (Get-Date).AddHours(-1)
    $file
}

# The film in the catalogue, in the library, as a person adds it.
function New-VolumeMovie {
    param([Parameter(Mandatory)] [string] $Title, [Parameter(Mandatory)] $Library, [int] $Year = 2008)
    Invoke-Deluno -Method POST -Path '/api/movies' -Body @{ title = $Title; releaseYear = $Year; imdbId = $null; monitored = $false; libraryId = $Library.id }
}

# The manual import: the preview, then the import itself, as the Import screen does it. Returns both, with what the import said.
function Invoke-VolumeImport {
    param(
        [Parameter(Mandatory)] [string] $Source, [Parameter(Mandatory)] $Library, [Parameter(Mandatory)] [string] $Title,
        [int] $Year = 2008, [string] $Mode = 'copy', [bool] $Fallback = $true, [string] $TitleId
    )
    $pv = [ordered]@{ sourcePath = $Source; fileName = (Split-Path $Source -Leaf); mediaType = 'movies'; title = $Title; year = $Year; genres = @(); tags = @(); libraryId = $Library.id }
    $pre = Try-Deluno -Method POST -Path '/api/filesystem/import/preview' -Body $pv -TimeoutSec 120
    $run = Try-Deluno -Method POST -Path '/api/filesystem/import/execute-batch' -TimeoutSec 600 -Body @{
        items = @(@{ preview = $pv; transferMode = $Mode; overwrite = $false; allowCopyFallback = $Fallback })
    }
    $item = if ($run.Data -and $run.Data.results) { @($run.Data.results)[0] } else { $null }
    [pscustomobject]@{
        Preview = $pre.Data; PreviewStatus = $pre.Status; Status = $run.Status; Item = $item; Raw = $run.Raw
        Succeeded = [bool]($item -and $item.succeeded)
        Message = $(if ($item) { [string]$item.message } else { [string]$run.Raw })
        Response = $(if ($item) { $item.response } else { $null })
    }
}

# Every file under a folder, as full names (an empty list when the folder is not there).
function Get-VolumeFiles {
    param([Parameter(Mandatory)] [string] $Path)
    @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
}

# The video of a title under a library's folder.
function Find-VolumeFilm {
    param([Parameter(Mandatory)] [string] $Root, [Parameter(Mandatory)] [string] $Title)
    Get-VolumeFiles -Path $Root | Where-Object { $_.Extension -in '.mkv', '.mp4', '.avi' -and $_.FullName -match [regex]::Escape($Title) } | Select-Object -First 1
}

function Get-MinimumFreeSpaceMb {
    $s = Invoke-Deluno -Path '/api/settings'
    [int]$s.minimumFreeSpaceMb
}

function Set-MinimumFreeSpaceMb {
    param([int] $Mb)
    Invoke-Deluno -Method PATCH -Path '/api/settings' -Body @{ minimumFreeSpaceMb = $Mb } | Out-Null
}

function Remove-VolumeLibrary {
    param($Library)
    if ($Library -and $Library.id) { Try-Deluno -Method DELETE -Path "/api/libraries/$($Library.id)" | Out-Null }
}

function Remove-VolumeMovie {
    param($Movie)
    if ($Movie -and $Movie.id) { Try-Deluno -Method DELETE -Path "/api/movies/$($Movie.id)" | Out-Null }
}

# Name, size and write time of every file under a folder, as text, so two looks can be compared.
function Get-FileSnapshot {
    param([Parameter(Mandatory)] [string] $Path)
    Get-VolumeFiles -Path $Path | Sort-Object FullName | ForEach-Object { '{0}|{1}|{2}' -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.Ticks }
}

# Does the alerts page say the library is not reachable?
function Test-LibraryUnreachable {
    param([Parameter(Mandatory)] [string] $LibraryName)
    $a = Try-Deluno -Path '/api/monitoring/alerts' -TimeoutSec 60
    @($a.Data.alerts | Where-Object { $_.code -eq 'library.unreachable' -and $_.summary -match [regex]::Escape($LibraryName) }).Count -gt 0
}

function Invoke-GroupVolumes {
    # ------------------------------------------------------------------------------------------------ a second drive
    Invoke-Scenario -Id 'volumes.second-drive' -Title 'A library on a second drive: the copy fallback, the refusal without it, a move across drives, the drive''s own free space, a drive too full, and a folder move that cannot cross drives' -Body {
        $token = New-FilmToken
        $srcRoot = (Join-Path $script:VolDir "src_Vol$token")
        $libC = $null; $libV = $null; $movies = @(); $minBefore = $null; $lock = $null
        try {
            $root = New-TestVolume -Name 'seconddrive' -SizeMb 320 -Fs ntfs -Letter 'V'
            $libV = New-VolumeLibrary -Name "Second drive $token" -Root 'V:\Movies'
            $minBefore = Get-MinimumFreeSpaceMb
            Set-MinimumFreeSpaceMb -Mb 20

            # ---------------------------------------------- a hard link cannot cross drives: the copy fallback
            $t1 = "Vol Fallback $token"
            $s1 = New-VolumeSource -Folder $srcRoot -Title $t1
            $size1 = (Get-Item -LiteralPath $s1).Length
            $movies += New-VolumeMovie -Title $t1 -Library $libV
            $r1 = Invoke-VolumeImport -Source $s1 -Library $libV -Title $t1 -Mode 'hardlink' -Fallback $true
            Save-Evidence -Name 'volumes-second-drive-fallback.json' -Text ($r1 | Select-Object Status, Succeeded, Message, Response | ConvertTo-Json -Depth 6)
            Add-Check 'The preview says a hard link is not possible between the two drives' ($r1.Preview -and $r1.Preview.hardlinkAvailable -eq $false -and (@($r1.Preview.warnings) -join ' ') -match 'differs from destination root') (@($r1.Preview.warnings) -join ' | ')
            Add-Check 'Hard link requested across drives with the fallback allowed: the import succeeds' $r1.Succeeded $r1.Message
            Add-Check 'It reports it copied instead (UsedFallback true, mode copy) and says why' ($r1.Response -and $r1.Response.usedFallback -eq $true -and $r1.Response.transferModeUsed -eq 'copy' -and $r1.Message -match 'copy fallback because hardlink') "used fallback: $($r1.Response.usedFallback), mode: $($r1.Response.transferModeUsed); $($r1.Message)"
            $f1 = Find-VolumeFilm -Root 'V:\Movies' -Title $t1
            Add-Check 'The film is on the second drive, whole (same size as the source)' ($f1 -and $f1.Length -eq $size1) $(if ($f1) { "$($f1.FullName) $($f1.Length) bytes" } else { 'not found' })
            Add-Check 'As a copy import does, the original was cleared once the copy was verified, so the film is in exactly one place' (-not (Test-Path -LiteralPath $s1)) ''
            $extra1 = @(Get-VolumeFiles -Path 'V:\Movies' | Where-Object { $_.FullName -match [regex]::Escape($t1) -and $_.Extension -notin '.mkv', '.mp4', '.avi' })
            Add-Check 'No staging file or restart mark is left beside it' ($extra1.Count -eq 0) (($extra1 | ForEach-Object { $_.Name }) -join ', ')

            # ---------------------------------------------- the same without the fallback: refused, nothing lost
            $t2 = "Vol Refused $token"
            $s2 = New-VolumeSource -Folder $srcRoot -Title $t2
            $movies += New-VolumeMovie -Title $t2 -Library $libV
            $r2 = Invoke-VolumeImport -Source $s2 -Library $libV -Title $t2 -Mode 'hardlink' -Fallback $false
            Save-Evidence -Name 'volumes-second-drive-refused.json' -Text ($r2 | Select-Object Status, Succeeded, Message | ConvertTo-Json -Depth 6)
            Add-Check 'Hard link across drives with the fallback off: the import is refused, saying hard links are not available' (-not $r2.Succeeded -and $r2.Message -match 'Hardlinking is not available') $r2.Message
            Add-Check 'The source is untouched and whole' ((Test-Path -LiteralPath $s2) -and (Get-Item -LiteralPath $s2).Length -gt 1MB) ''
            $left2 = @(Get-VolumeFiles -Path 'V:\Movies' | Where-Object { $_.FullName -match [regex]::Escape($t2) })
            Add-Check 'Nothing of it is on the second drive (no film, no staging file)' ($left2.Count -eq 0) (($left2 | ForEach-Object { $_.FullName }) -join ', ')

            # ---------------------------------------------- a move across drives is a copy then a delete
            $t3 = "Vol Move $token"
            $s3 = New-VolumeSource -Folder $srcRoot -Title $t3
            $size3 = (Get-Item -LiteralPath $s3).Length
            $movies += New-VolumeMovie -Title $t3 -Library $libV
            $r3 = Invoke-VolumeImport -Source $s3 -Library $libV -Title $t3 -Mode 'move' -Fallback $true
            Add-Check 'A move onto the second drive succeeds' $r3.Succeeded $r3.Message
            $f3 = Find-VolumeFilm -Root 'V:\Movies' -Title $t3
            Add-Check 'The film is on the second drive, whole, and gone from the source (never in neither place)' ($f3 -and $f3.Length -eq $size3 -and -not (Test-Path -LiteralPath $s3)) $(if ($f3) { "$($f3.FullName) $($f3.Length) of $size3 bytes" } else { 'not found' })
            $extra3 = @(Get-VolumeFiles -Path 'V:\Movies' | Where-Object { $_.FullName -match [regex]::Escape($t3) -and $_.Extension -notin '.mkv', '.mp4', '.avi' })
            Add-Check 'No staging file or restart mark is left after the move' ($extra3.Count -eq 0) (($extra3 | ForEach-Object { $_.Name }) -join ', ')

            # ---------------------------------------------- removal with its files, and restore, on the second drive
            $rm1 = Try-Deluno -Method DELETE -Path "/api/movies/$($movies[0].id)?deleteFiles=true" -TimeoutSec 300
            $held1 = @(Get-VolumeFiles -Path $root | Where-Object { $_.FullName -notlike 'V:\Movies\*' -and $_.Extension -eq '.mkv' -and $_.FullName -match [regex]::Escape($t1) })
            Add-Check 'Removing a title with its files from the second drive puts the film in the holding folder beside the library root, on the same drive' (($rm1.Status -in 200, 204) -and $null -eq (Find-VolumeFilm -Root 'V:\Movies' -Title $t1) -and $held1.Count -eq 1) "HTTP $($rm1.Status); in the holding folder: $($held1.Count) ($(($held1 | Select-Object -First 1).FullName))"
            $bin1 = @(Invoke-Deluno -Path '/api/recycle-bin')
            $binRows1 = if ($bin1.Count -eq 1 -and $bin1[0].PSObject.Properties.Name -contains 'items') { @($bin1[0].items) } else { $bin1 }
            $item1 = @($binRows1 | Where-Object { ($_ | ConvertTo-Json -Depth 4) -match [regex]::Escape($t1) }) | Select-Object -First 1
            $rs1 = if ($item1) { Try-Deluno -Method POST -Path "/api/recycle-bin/$($item1.id)/restore" -TimeoutSec 300 } else { $null }
            $back1 = Find-VolumeFilm -Root 'V:\Movies' -Title $t1
            Add-Check 'Restoring it brings the film back on the second drive, whole' ($rs1 -and $rs1.Status -in 200, 204 -and $back1 -and $back1.Length -eq $size1) $(if ($rs1) { "HTTP $($rs1.Status)" } else { 'the recycle bin did not list it' })

            # ---------------------------------------------- the drive's own free space, and a drive that cannot be read
            $ds = Try-Deluno -Path '/api/diskspace'
            $entries = @($ds.Data)
            $mine = @($entries | Where-Object { $_.path -like 'V:*' })
            $volInfo = New-Object System.IO.DriveInfo('V')
            Add-Check 'Disk space lists the second drive once, with its own total and free space' ($ds.Status -eq 200 -and $mine.Count -eq 1 -and [math]::Abs([long]$mine[0].totalSpace - $volInfo.TotalSize) -lt 1MB -and [math]::Abs([long]$mine[0].freeSpace - $volInfo.AvailableFreeSpace) -lt 20MB) $(if ($mine.Count) { "V: total $($mine[0].totalSpace), free $($mine[0].freeSpace); the drive says total $($volInfo.TotalSize), free $($volInfo.AvailableFreeSpace)" } else { 'no entry for V:' })
            Add-Check 'The system drive is listed too, with different numbers (each volume has its own)' ($mine.Count -ge 1 -and @($entries | Where-Object { $_.path -like 'C:*' -and [long]$_.totalSpace -ne [long]$mine[0].totalSpace }).Count -ge 1) ''
            # ---------------------------------------------- a drive too full for the minimum free space
            Set-MinimumFreeSpaceMb -Mb 5000
            $t4 = "Vol Full $token"
            $s4 = New-VolumeSource -Folder $srcRoot -Title $t4
            $movies += New-VolumeMovie -Title $t4 -Library $libV
            $r4 = Invoke-VolumeImport -Source $s4 -Library $libV -Title $t4 -Mode 'copy' -Fallback $true
            Add-Check 'A drive with less free space than the minimum refuses the import and says how much is free and what the minimum is' (-not $r4.Succeeded -and $r4.Message -match 'below the configured minimum') $r4.Message
            Add-Check 'The refused import left the source alone and put nothing on the drive' ((Test-Path -LiteralPath $s4) -and $null -eq (Find-VolumeFilm -Root 'V:\Movies' -Title $t4)) ''
            Set-MinimumFreeSpaceMb -Mb 20
            $r4b = Invoke-VolumeImport -Source $s4 -Library $libV -Title $t4 -Mode 'copy' -Fallback $true
            Add-Check 'With the minimum lowered the same import goes through' $r4b.Succeeded $r4b.Message

            # ---------------------------------------------- a whole-folder move between drives, and a file held open
            $libC = New-VolumeLibrary -Name "System drive $token" -Root "C:\Media\Vol$token"
            $t5 = "Vol Reassign $token"
            $s5 = New-VolumeSource -Folder $srcRoot -Title $t5
            $m5 = New-VolumeMovie -Title $t5 -Library $libC
            $movies += $m5
            $r5 = Invoke-VolumeImport -Source $s5 -Library $libC -Title $t5 -Mode 'copy' -Fallback $true
            $f5 = Find-VolumeFilm -Root "C:\Media\Vol$token" -Title $t5
            Add-Check 'A film is imported into the library on the system drive first' ($r5.Succeeded -and $null -ne $f5) $r5.Message
            if ($f5) {
                $mv = Try-Deluno -Method POST -Path '/api/movies/bulk/reassign-library' -Body @{ movieIds = @($m5.id); fromLibraryId = $libC.id; toLibraryId = $libV.id; moveFiles = $true } -TimeoutSec 300
                Save-Evidence -Name 'volumes-second-drive-reassign.json' -Text $mv.Raw
                $reasons = @($mv.Data.moveFailureReasons)
                $afterFile = Find-VolumeFilm -Root "C:\Media\Vol$token" -Title $t5
                $movedFile = Find-VolumeFilm -Root 'V:\Movies' -Title $t5
                $rec = Try-Deluno -Path "/api/movies/$($m5.id)"
                $recLib = if ($rec.Data) { [string]$rec.Data.libraryId } else { '' }
                Add-Check 'Moving a film to a library on another drive either moves the folder or explains in plain words that a folder moves only within one drive' (($mv.Status -eq 200) -and ($mv.Data.filesMoved -ge 1 -or ($reasons.Count -ge 1 -and ($reasons -join ' ') -match 'another drive'))) "filesMoved $($mv.Data.filesMoved); $($reasons -join ' | ')"
                $stillAtOld = [bool]$afterFile; $nowAtNew = [bool]$movedFile
                Add-Check 'The film is in exactly one place and its record agrees with where it is' (($stillAtOld -xor $nowAtNew) -and (($nowAtNew -and $recLib -eq $libV.id) -or ($stillAtOld -and $recLib -eq $libC.id))) "old folder: $stillAtOld, new drive: $nowAtNew, record in library $recLib (old $($libC.id), new $($libV.id))"
                Add-Check 'The reassigned film''s file is whole wherever it is' ((@($afterFile, $movedFile) | Where-Object { $_ } | Select-Object -First 1).Length -eq (Get-Item -LiteralPath $script:Fx.Base['small']).Length) ''

                # Between two libraries on the same drive, where a folder can move in one step: a file held open by another program,
                # and a folder Deluno is not allowed to move. Either way the film is in one place and the record agrees.
                $libC2 = New-VolumeLibrary -Name "System drive two $token" -Root "C:\Media\VolTwo$token"
                foreach ($case in @('locked', 'denied')) {
                    $t6 = "Vol $case $token"
                    $s6 = New-VolumeSource -Folder $srcRoot -Title $t6
                    $m6 = New-VolumeMovie -Title $t6 -Library $libC
                    $movies += $m6
                    $r6 = Invoke-VolumeImport -Source $s6 -Library $libC -Title $t6 -Mode 'copy' -Fallback $true
                    $f6 = Find-VolumeFilm -Root "C:\Media\Vol$token" -Title $t6
                    if (-not $f6) { Add-Check "The $case film was imported first" $false $r6.Message; continue }
                    $folder6 = Split-Path $f6.FullName -Parent
                    if ($case -eq 'locked') { $lock = [System.IO.File]::Open($f6.FullName, 'Open', 'Read', 'None') }
                    else { & icacls.exe $folder6 /deny 'Everyone:(DE,DC)' | Out-Null; & icacls.exe "C:\Media\Vol$token" /deny 'Everyone:(DC)' | Out-Null }
                    try { $mv6 = Try-Deluno -Method POST -Path '/api/movies/bulk/reassign-library' -Body @{ movieIds = @($m6.id); fromLibraryId = $libC.id; toLibraryId = $libC2.id; moveFiles = $true } -TimeoutSec 300 }
                    finally {
                        if ($lock) { $lock.Dispose(); $lock = $null }
                        if ($case -eq 'denied') { & icacls.exe $folder6 /remove:d Everyone | Out-Null; & icacls.exe "C:\Media\Vol$token" /remove:d Everyone | Out-Null }
                    }
                    Save-Evidence -Name "volumes-second-drive-$case.json" -Text $mv6.Raw
                    $old6 = Find-VolumeFilm -Root "C:\Media\Vol$token" -Title $t6
                    $new6 = Find-VolumeFilm -Root "C:\Media\VolTwo$token" -Title $t6
                    $rec6 = Try-Deluno -Path "/api/movies/$($m6.id)"
                    $recLib6 = if ($rec6.Data) { [string]$rec6.Data.libraryId } else { '' }
                    Add-Check "A film that cannot be moved ($case) is never lost: it is in one place and the record agrees" (($mv6.Status -eq 200) -and ([bool]$old6 -xor [bool]$new6) -and (($new6 -and $recLib6 -eq $libC2.id) -or ($old6 -and $recLib6 -eq $libC.id))) "old: $([bool]$old6), new: $([bool]$new6), record library $recLib6; answer: $($mv6.Raw)"
                    $why6 = @($mv6.Data.moveFailureReasons) -join ' | '
                    $expect = if ($case -eq 'locked') { 'could not be moved|being used|another process' } else { 'not allowed to move|is denied' }
                    Add-Check "It says in plain words why the folder did not move ($case)" ($mv6.Data.filesMoved -ge 1 -or $why6 -match $expect) "moved $($mv6.Data.filesMoved); $why6"
                }
                Remove-VolumeLibrary -Library $libC2
            }

            # ---------------------------------------------- a drive Deluno cannot read any more is left out of disk space, not listed as zero
            $libVid = $libV.id
            Remove-TestVolume -Name 'seconddrive'
            $gone = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { -not (Test-Path -LiteralPath 'V:\') }
            $ds3 = Try-Deluno -Path '/api/diskspace'
            $stale = @(@($ds3.Data) | Where-Object { $_.path -like 'V:*' })
            Add-Check 'With the drive gone, disk space leaves it out (it does not list it with zero) and still answers' ($gone.Ok -and $ds3.Status -eq 200 -and $stale.Count -eq 0 -and @($ds3.Data).Count -ge 1) "HTTP $($ds3.Status); entries for V: $($stale.Count)"
        }
        finally {
            if ($lock) { $lock.Dispose() }
            if ($null -ne $minBefore) { try { Set-MinimumFreeSpaceMb -Mb $minBefore } catch { } }
            foreach ($m in $movies) { Remove-VolumeMovie -Movie $m }
            Remove-VolumeLibrary -Library $libC
            Remove-VolumeLibrary -Library $libV
            Remove-TestVolume -Name 'seconddrive'
            Remove-Item -LiteralPath $srcRoot, "C:\Media\Vol$token", "C:\Media\VolTwo$token" -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ------------------------------------------------------------------------------------------------ exFAT: no hard links at all
    Invoke-Scenario -Id 'volumes.exfat-volume' -Title 'A library on an exFAT volume mounted in a folder of the system drive: the roots look alike but the OS refuses the hard link' -Body {
        $token = New-FilmToken
        $srcRoot = (Join-Path $script:VolDir "src_Exfat$token")
        $mount = Join-Path $script:VolDir 'mnt-exfat'
        $lib = $null; $movies = @(); $minBefore = $null
        try {
            $root = New-TestVolume -Name 'exfat' -SizeMb 320 -Fs exfat -MountFolder $mount
            $lib = New-VolumeLibrary -Name "ExFAT $token" -Root (Join-Path $mount 'Movies')
            $minBefore = Get-MinimumFreeSpaceMb
            Set-MinimumFreeSpaceMb -Mb 20
            $t1 = "Exfat Fallback $token"
            $s1 = New-VolumeSource -Folder $srcRoot -Title $t1
            $size1 = (Get-Item -LiteralPath $s1).Length
            $movies += New-VolumeMovie -Title $t1 -Library $lib
            $r1 = Invoke-VolumeImport -Source $s1 -Library $lib -Title $t1 -Mode 'hardlink' -Fallback $true
            Save-Evidence -Name 'volumes-exfat-fallback.json' -Text ($r1 | Select-Object Status, Succeeded, Message, Response | ConvertTo-Json -Depth 6)
            Add-Check 'The preview thinks a hard link is possible (both folders are under C:\), which only the OS can refute' ($r1.Preview -and $r1.Preview.hardlinkAvailable -eq $true) ''
            Add-Check 'The OS refuses the hard link on exFAT and, with the fallback allowed, the file is copied (UsedFallback true, mode copy)' ($r1.Succeeded -and $r1.Response.usedFallback -eq $true -and $r1.Response.transferModeUsed -eq 'copy') "used fallback: $($r1.Response.usedFallback), mode: $($r1.Response.transferModeUsed); $($r1.Message)"
            $f1 = Find-VolumeFilm -Root $mount -Title $t1
            Add-Check 'The film is on the exFAT volume, whole, and the original was cleared once the copy was verified' ($f1 -and $f1.Length -eq $size1 -and -not (Test-Path -LiteralPath $s1)) $(if ($f1) { $f1.FullName } else { 'not found' })
            $extra = @(Get-VolumeFiles -Path $mount | Where-Object { $_.FullName -match [regex]::Escape($t1) -and $_.Extension -notin '.mkv', '.mp4', '.avi' })
            Add-Check 'No staging file or restart mark is left beside it' ($extra.Count -eq 0) (($extra | ForEach-Object { $_.Name }) -join ', ')

            $t2 = "Exfat Refused $token"
            $s2 = New-VolumeSource -Folder $srcRoot -Title $t2
            $movies += New-VolumeMovie -Title $t2 -Library $lib
            $r2 = Invoke-VolumeImport -Source $s2 -Library $lib -Title $t2 -Mode 'hardlink' -Fallback $false
            Save-Evidence -Name 'volumes-exfat-refused.json' -Text ($r2 | Select-Object Status, Succeeded, Message | ConvertTo-Json -Depth 6)
            Add-Check 'With the fallback off the OS''s refusal is carried to the person in the import''s answer' (-not $r2.Succeeded -and $r2.Message.Length -gt 0) $r2.Message
            Add-Check 'The source is untouched and nothing of it is on the volume (staging removed, nothing lost)' ((Test-Path -LiteralPath $s2) -and @(Get-VolumeFiles -Path $mount | Where-Object { $_.FullName -match [regex]::Escape($t2) }).Count -eq 0) ''
        }
        finally {
            if ($null -ne $minBefore) { try { Set-MinimumFreeSpaceMb -Mb $minBefore } catch { } }
            foreach ($m in $movies) { Remove-VolumeMovie -Movie $m }
            Remove-VolumeLibrary -Library $lib
            Remove-TestVolume -Name 'exfat'
            Remove-MountFolder -Path $mount
            Remove-Item -LiteralPath $srcRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ------------------------------------------------------------------------------------------------ an SMB share
    Invoke-Scenario -Id 'volumes.smb-share' -Title 'A library on a network share (loopback SMB): imports land whole, nothing existing is touched, removal and restore work, a read-only share refuses cleanly, a share that goes offline marks nothing missing' -Body {
        $token = New-FilmToken
        $shareName = "goldmedia$token".ToLower()
        $roShareName = "goldro$token".ToLower()
        $shareDir = Join-Path $script:VolDir "smb-$token"
        $roDir = Join-Path $script:VolDir "smbro-$token"
        $srcRoot = (Join-Path $script:VolDir "src_Smb$token")
        $unc = "\\localhost\$shareName"
        $roUnc = "\\localhost\$roShareName"
        $lib = $null; $roLib = $null; $movies = @(); $minBefore = $null; $serverStopped = $false
        try {
            New-Item -ItemType Directory -Force -Path $shareDir, $roDir | Out-Null
            New-SmbShare -Name $shareName -Path $shareDir -FullAccess 'Everyone' | Out-Null
            New-SmbShare -Name $roShareName -Path $roDir -ReadAccess 'Everyone' | Out-Null
            $up = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { (Test-Path -LiteralPath $unc) -and (Test-Path -LiteralPath $roUnc) }
            Add-Check 'The two loopback shares answer (one writable, one read-only)' $up.Ok "$unc and $roUnc"
            if (-not $up.Ok) { return }
            $lib = New-VolumeLibrary -Name "Share $token" -Root "$unc\Movies"
            $minBefore = Get-MinimumFreeSpaceMb
            Set-MinimumFreeSpaceMb -Mb 20

            # ---------------------------------------------- one film onto the share, by copy
            $t1 = "Smb Copy $token"
            $s1 = New-VolumeSource -Folder $srcRoot -Title $t1
            $size1 = (Get-Item -LiteralPath $s1).Length
            $movies += New-VolumeMovie -Title $t1 -Library $lib
            $r1 = Invoke-VolumeImport -Source $s1 -Library $lib -Title $t1 -Mode 'copy' -Fallback $true
            Save-Evidence -Name 'volumes-smb-copy.json' -Text ($r1 | Select-Object Status, Succeeded, Message, Response | ConvertTo-Json -Depth 6)
            Add-Check 'A film copied onto the share is imported' $r1.Succeeded $r1.Message
            $f1 = Find-VolumeFilm -Root "$unc\Movies" -Title $t1
            Add-Check 'It lands on the share intact (same size as the source)' ($f1 -and $f1.Length -eq $size1) $(if ($f1) { "$($f1.FullName) $($f1.Length) bytes" } else { 'not found' })
            $extra = @(Get-VolumeFiles -Path $shareDir | Where-Object { $_.Extension -notin '.mkv', '.mp4', '.avi' })
            Add-Check 'The staging name is gone: nothing but the film is on the share' ($extra.Count -eq 0) (($extra | ForEach-Object { $_.FullName }) -join ', ')
            Add-Check 'The share holds the file where the share''s folder says it does (the share and its folder agree)' ($null -ne (Find-VolumeFilm -Root $shareDir -Title $t1)) ''

            # ---------------------------------------------- a move onto the share
            $t2 = "Smb Move $token"
            $s2 = New-VolumeSource -Folder $srcRoot -Title $t2
            $size2 = (Get-Item -LiteralPath $s2).Length
            $movies += New-VolumeMovie -Title $t2 -Library $lib
            $r2 = Invoke-VolumeImport -Source $s2 -Library $lib -Title $t2 -Mode 'move' -Fallback $true
            $f2 = Find-VolumeFilm -Root "$unc\Movies" -Title $t2
            Add-Check 'A move onto the share leaves the film on the share, whole, and gone from the source' ($r2.Succeeded -and $f2 -and $f2.Length -eq $size2 -and -not (Test-Path -LiteralPath $s2)) $(if ($f2) { "$($f2.FullName) $($f2.Length) of $size2 bytes; $($r2.Message)" } else { $r2.Message })

            # ---------------------------------------------- the share is listed once, with its own space
            $ds = Try-Deluno -Path '/api/diskspace'
            $mine = @(@($ds.Data) | Where-Object { $_.path -like '\\*' })
            Add-Check 'Disk space lists the share once, with a total and free space' ($ds.Status -eq 200 -and $mine.Count -eq 1 -and [long]$mine[0].totalSpace -gt 0 -and [long]$mine[0].freeSpace -gt 0) $(if ($mine.Count) { "$($mine[0].path) total $($mine[0].totalSpace), free $($mine[0].freeSpace)" } else { 'no entry for the share' })

            # ---------------------------------------------- Review existing and Import existing change nothing
            $t3 = "Smb Existing $token"
            $existingDir = Join-Path "$unc\Movies" "$t3 (2008)"
            New-Item -ItemType Directory -Force -Path $existingDir | Out-Null
            $existing = Join-Path $existingDir "$t3 (2008).mkv"
            Copy-Item -LiteralPath $script:Fx.Base['small'] -Destination $existing -Force
            $before = @(Get-FileSnapshot -Path $shareDir)
            $pv = Try-Deluno -Path "/api/libraries/$($lib.id)/import-existing/preview" -TimeoutSec 300
            Save-Evidence -Name 'volumes-smb-import-existing-preview.json' -Text $pv.Raw
            $go = Try-Deluno -Method POST -Path "/api/libraries/$($lib.id)/import-existing" -TimeoutSec 120
            $done = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition {
                $p = Try-Deluno -Path "/api/libraries/$($lib.id)/import-existing"
                if ($p.Status -eq 200 -and $p.Data -and $p.Data.run -and "$($p.Data.run.status)" -match '(?i)^(completed|complete|done|failed|idle|finished|succeeded|cancelled|paused)$') { $p } else { $null }
            }
            Save-Evidence -Name 'volumes-smb-import-existing-run.json' -Text $(if ($done.Ok) { $done.Value.Raw } else { $go.Raw })
            $after = @(Get-FileSnapshot -Path $shareDir)
            Add-Check 'Review existing and Import existing ran against the share' ($pv.Status -eq 200 -and $go.Status -in 200, 202 -and $done.Ok) "preview $($pv.Status), import $($go.Status); ended: $($done.Ok)"
            Add-Check 'Nothing on the share was moved, renamed, rewritten or deleted (names, sizes and times are the same)' ((($before -join "`n") -eq ($after -join "`n")) -and (Test-Path -LiteralPath $existing)) "before $($before.Count) files, after $($after.Count)"
            $runEnd = if ($done.Ok) { $done.Value.Data.run } else { $null }
            Add-Check 'The run read all three films on the share and took in only the one that was not yet known (the two Deluno had put there itself are skipped)' ($runEnd -and $runEnd.processedCount -eq 3 -and $runEnd.importedCount -eq 1 -and $runEnd.skippedCount -eq 2) "processed $($runEnd.processedCount), imported $($runEnd.importedCount), skipped $($runEnd.skippedCount)"

            # ---------------------------------------------- removal with its files, and restore, across the share
            $m1 = $movies[0]
            $film1 = $f1.FullName
            $rm = Try-Deluno -Method DELETE -Path "/api/movies/$($m1.id)?deleteFiles=true" -TimeoutSec 300
            Add-Check 'Removing a title with its files from the share is accepted' ($rm.Status -in 200, 204) "HTTP $($rm.Status) $($rm.Raw)"
            $gone = -not (Test-Path -LiteralPath $film1)
            $holdingFiles = @(Get-VolumeFiles -Path $shareDir | Where-Object { $_.FullName -notlike "$shareDir\Movies\*" -and $_.Extension -in '.mkv', '.mp4', '.avi' -and $_.FullName -match [regex]::Escape($t1) })
            Add-Check 'The film left the library folder and sits in the holding folder beside the library root, on the same share' ($gone -and $holdingFiles.Count -eq 1) "gone from the library: $gone; in the holding folder: $($holdingFiles.Count) ($(($holdingFiles | Select-Object -First 1).FullName))"
            $bin = @(Invoke-Deluno -Path '/api/recycle-bin')
            $binRows = if ($bin.Count -eq 1 -and $bin[0].PSObject.Properties.Name -contains 'items') { @($bin[0].items) } else { $bin }
            $item = @($binRows | Where-Object { ($_ | ConvertTo-Json -Depth 4) -match [regex]::Escape($t1) }) | Select-Object -First 1
            Add-Check 'The recycle bin lists the removed film' ($null -ne $item) ''
            if ($item) {
                $rs = Try-Deluno -Method POST -Path "/api/recycle-bin/$($item.id)/restore" -TimeoutSec 300
                Add-Check 'Restoring it puts the film back where it was on the share, whole' ($rs.Status -in 200, 204 -and (Test-Path -LiteralPath $film1) -and (Get-Item -LiteralPath $film1).Length -eq $f1.Length) "HTTP $($rs.Status) $($rs.Raw)"
            }

            # ---------------------------------------------- a read-only share: a rename fails cleanly
            $roLib = New-VolumeLibrary -Name "Read-only share $token" -Root $roUnc
            $roFilmDir = Join-Path $roDir 'Ro Film (2008)'
            # The folder is made through the local path (the share is read-only, the folder is not), then the share is asked to move it.
            New-Item -ItemType Directory -Force -Path $roFilmDir | Out-Null
            Copy-Item -LiteralPath $script:Fx.Base['small'] -Destination (Join-Path $roFilmDir 'Ro Film (2008).mkv') -Force
            $tRo = 'Ro Film'
            $mRo = New-VolumeMovie -Title $tRo -Library $roLib
            $movies += $mRo
            $before2 = @(Get-VolumeFiles -Path $roDir | ForEach-Object { '{0}|{1}' -f $_.FullName, $_.Length })
            $rn = Try-Deluno -Method POST -Path '/api/movies/bulk/rename-apply' -Body @{ movieIds = @($mRo.id) } -TimeoutSec 120
            Save-Evidence -Name 'volumes-smb-readonly-rename.json' -Text $rn.Raw
            $after2 = @(Get-VolumeFiles -Path $roDir | ForEach-Object { '{0}|{1}' -f $_.FullName, $_.Length })
            Add-Check 'A rename on the read-only share answers (not a crash) and leaves every file where it was, whole' ($rn.Status -lt 500 -and (($before2 -join "`n") -eq ($after2 -join "`n"))) "HTTP $($rn.Status); before $($before2.Count) files, after $($after2.Count)"

            # ---------------------------------------------- the share goes offline mid-check, and comes back
            $mv = Find-VolumeFilm -Root "$unc\Movies" -Title $t2
            Stop-Service -Name LanmanServer -Force
            $serverStopped = $true
            $shareGone = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { -not (Test-Path -LiteralPath $unc) }
            Add-Check 'The share is offline' $shareGone.Ok ''
            $fc = Try-Deluno -Method POST -Path '/api/filesystem/file-check' -Body @{ libraryId = $lib.id } -TimeoutSec 180
            Save-Evidence -Name 'volumes-smb-offline-filecheck.json' -Text $fc.Raw
            $alerts = Wait-Until -TimeoutSec 60 -IntervalSec 3 -Condition {
                $a = Try-Deluno -Path '/api/monitoring/alerts'
                if (@($a.Data.alerts | Where-Object { $_.code -eq 'library.unreachable' -and $_.summary -match [regex]::Escape($lib.name) }).Count -gt 0) { $a } else { $null }
            }
            Add-Check 'With the share offline Deluno says the library is not reachable and changes nothing about its titles' $alerts.Ok $(if ($alerts.Ok) { 'alert library.unreachable raised' } else { 'no unreachable alert' })
            $miss = Try-Deluno -Path "/api/movies/$($movies[1].id)"
            Add-Check 'Nothing is marked missing while the share is away' ($miss.Status -eq 200 -and ($miss.Raw -notmatch '"(fileMissing|isMissing)"\s*:\s*true')) $(if ($miss.Raw.Length -gt 300) { $miss.Raw.Substring(0, 300) } else { $miss.Raw })
            Start-Service -Name LanmanServer
            $serverStopped = $false
            $back = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { (Test-Path -LiteralPath $unc) -and (Test-Path -LiteralPath $film1) }
            Add-Check 'The share comes back with its film' $back.Ok ''
            $cleared = Wait-Until -TimeoutSec 120 -IntervalSec 5 -Condition {
                $a = Try-Deluno -Path '/api/monitoring/alerts'
                if (@($a.Data.alerts | Where-Object { $_.code -eq 'library.unreachable' -and $_.summary -match [regex]::Escape($lib.name) }).Count -eq 0) { $a } else { $null }
            }
            Add-Check 'The not-reachable alert clears by itself once the share is back' $cleared.Ok ''
        }
        finally {
            if ($serverStopped -or (Get-Service -Name LanmanServer).Status -ne 'Running') { try { Start-Service -Name LanmanServer } catch { } }
            if ($null -ne $minBefore) { try { Set-MinimumFreeSpaceMb -Mb $minBefore } catch { } }
            foreach ($m in $movies) { Remove-VolumeMovie -Movie $m }
            Remove-VolumeLibrary -Library $roLib
            Remove-VolumeLibrary -Library $lib
            Remove-SmbShare -Name $shareName, $roShareName -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $shareDir, $roDir, $srcRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ------------------------------------------------------------------------------------------------ a share that stops answering
    Invoke-Scenario -Id 'volumes.dead-share' -Title 'A library on a share that stops answering is reported not reachable within the 5 seconds the check gives, the page that reads it is not held up, and it recovers' -Body {
        $token = New-FilmToken
        $shareName = "goldstall$token".ToLower()
        $shareDir = Join-Path $script:VolDir "stall-$token"
        $port = 44555
        $lib = $null; $mapped = $false; $proxy = $null
        try {
            if (-not ([System.Management.Automation.PSTypeName]'StallProxy').Type) {
                Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Net; using System.Net.Sockets; using System.Threading;
public class StallProxy {
    TcpListener l; public volatile bool Stalled; List<TcpClient> all = new List<TcpClient>();
    public void Start(int port, int target) {
        l = new TcpListener(IPAddress.Loopback, port); l.Start();
        var t = new Thread(() => {
            while (true) {
                TcpClient c; try { c = l.AcceptTcpClient(); } catch { return; }
                var s = new TcpClient();
                try { s.Connect(IPAddress.Loopback, target); } catch { c.Close(); continue; }
                lock (all) { all.Add(c); all.Add(s); }
                Pump(c, s); Pump(s, c);
            }
        }); t.IsBackground = true; t.Start();
    }
    void Pump(TcpClient a, TcpClient b) {
        var t = new Thread(() => {
            var buf = new byte[65536];
            try { var sa = a.GetStream(); var sb = b.GetStream();
                while (true) { int n = sa.Read(buf, 0, buf.Length); if (n <= 0) break; while (Stalled) Thread.Sleep(50); sb.Write(buf, 0, n); }
            } catch { }
        }); t.IsBackground = true; t.Start();
    }
    public void Stop() { try { l.Stop(); } catch { } lock (all) { foreach (var c in all) { try { c.Close(); } catch { } } all.Clear(); } }
}
'@
            }
            New-Item -ItemType Directory -Force -Path (Join-Path $shareDir 'Movies') | Out-Null
            New-SmbShare -Name $shareName -Path $shareDir -FullAccess 'Everyone' | Out-Null
            # A proxy in front of the share that can be told to stop passing anything on: a NAS that has stopped answering.
            $proxy = New-Object StallProxy
            $proxy.Start($port, 445)
            $out = & net.exe use Z: "\\127.0.0.1\$shareName" "/TCPPORT:$port" /persistent:no 2>&1 | Out-String
            $mapped = Test-Path -LiteralPath 'Z:\Movies'
            Add-Check 'A drive is mapped to the share through a proxy that can be made to stall' $mapped $out
            if (-not $mapped) { return }
            $made = Try-Deluno -Method POST -Path '/api/libraries' -TimeoutSec 120 -Body @{
                name = "Stalling share $token"; mediaType = 'movies'; purpose = 'Main library'; rootPath = 'Z:\Movies'
                qualityProfileId = (Get-Library -MediaType 'movies').qualityProfileId; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
                removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
            }
            Save-Evidence -Name 'volumes-dead-share-create.json' -Text $made.Raw
            Add-Check 'Deluno makes a library on the share while it answers' ($made.Status -ge 200 -and $made.Status -lt 300) "HTTP $($made.Status) $($made.Raw)"
            if ($made.Status -lt 200 -or $made.Status -ge 300) { return }
            $lib = $made.Data
            # (Test-LibraryUnreachable asks the alerts page and says whether it names the library)
            $healthy = Wait-Until -TimeoutSec 20 -IntervalSec 2 -Condition { -not (Test-LibraryUnreachable -LibraryName $lib.name) }
            Add-Check 'While the share answers the library is reachable (no alert)' $healthy.Ok ''

            # The share stops answering. Windows keeps a folder's details for ten seconds, so the stall shows from then on.
            $proxy.Stalled = $true
            $win = [Diagnostics.Stopwatch]::StartNew()
            $script:deadTimes = @()
            $verdict = Wait-Until -TimeoutSec 150 -IntervalSec 1 -Condition {
                $one = [Diagnostics.Stopwatch]::StartNew()
                $r = Test-LibraryUnreachable -LibraryName $lib.name
                $script:deadTimes += $one.Elapsed.TotalSeconds
                $r
            }
            Add-Check 'The library is reported not reachable while its share is stalled' $verdict.Ok ("after {0} s of stalling" -f $verdict.Seconds)
            $slowest = if ($script:deadTimes.Count) { ($script:deadTimes | Measure-Object -Maximum).Maximum } else { 0 }
            Add-Check 'No read of the alerts page was held up by the stalled share (the slowest answered in under 10 seconds; Windows takes over 20 to give up on a stalled share)' ($script:deadTimes.Count -gt 0 -and $slowest -lt 10) ("{0} reads, slowest {1:N2} s" -f $script:deadTimes.Count, $slowest)
            $hw = [Diagnostics.Stopwatch]::StartNew()
            $h = Try-Deluno -Path '/api/system/health' -TimeoutSec 60
            Add-Check 'Another page (System health) still answers promptly while the share is stalled' ($h.Status -eq 200 -and $hw.Elapsed.TotalSeconds -lt 5) ("{0:N1} s" -f $hw.Elapsed.TotalSeconds)
            $dw = [Diagnostics.Stopwatch]::StartNew()
            $disk = Try-Deluno -Path '/api/monitoring/dashboard' -TimeoutSec 60
            Add-Check 'The monitoring dashboard answers promptly too' ($disk.Status -eq 200 -and $dw.Elapsed.TotalSeconds -lt 5) ("{0:N1} s" -f $dw.Elapsed.TotalSeconds)

            # It passes the share's answers on again: the alert clears by itself.
            $proxy.Stalled = $false
            $cleared = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { -not (Test-LibraryUnreachable -LibraryName $lib.name) }
            Add-Check 'Once the share answers again the not-reachable alert clears by itself' $cleared.Ok ("after {0} s" -f $cleared.Seconds)
        }
        finally {
            if ($proxy) { $proxy.Stalled = $false }
            Remove-VolumeLibrary -Library $lib
            if ($mapped -or (Test-Path -LiteralPath 'Z:\')) { & net.exe use Z: /delete /y 2>&1 | Out-Null }
            if ($proxy) { $proxy.Stop() }
            Remove-SmbShare -Name $shareName -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $shareDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
