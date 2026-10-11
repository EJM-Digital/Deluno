<#
    Removal: "Remove this copy" on a finished download, as a folder and as a single file; removing twice reads as a failure and
    writes no second Activity row. Each scenario sends its own film (a folder release on every client; on the torrent clients
    also a single file), so the films of G02-Films.ps1 stay in place for Weir's scenarios.
#>
function Test-RemoveThisCopy {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] [string] $What)
    $before = @(Get-FilmLeftovers -Film $Film)
    Add-Check "$What : the download is on disk before it is removed" ($before.Count -gt 0) "$($before.Count) items"
    $lib = Find-Imported -Title $Film.Title
    $libBytes = if ($lib) { $lib.Length } else { -1 }
    $rowsBefore = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Removed from the download queue' }).Count

    $first = Remove-FilmDownload -Film $Film -Reason 'reclaim'
    Add-Check "$What : the first removal succeeds" ($first.Status -eq 200 -and $first.Data.succeeded) "HTTP $($first.Status): $($first.Data.message)"
    $gone = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { @(Get-FilmLeftovers -Film $Film).Count -eq 0 }
    $left = @(Get-FilmLeftovers -Film $Film)
    Add-Check "$What : no folder, file or extra of it is left" $gone.Ok ("gone after {0} s; left: {1}" -f $gone.Seconds, (($left | Select-Object -First 5 | ForEach-Object { $_.FullName }) -join '; '))
    $lib2 = Find-Imported -Title $Film.Title
    Add-Check "$What : the library file was not touched" ($lib2 -and $lib2.Length -eq $libBytes) ''

    $second = Remove-FilmDownload -Film $Film -Reason 'reclaim'
    $reads = ($second.Status -ge 400 -and (($second.Data -and $second.Data.succeeded -eq $false) -or $second.Status -ge 400))
    Add-Check "$What : the second removal reads as a failure" $reads ("HTTP {0}: {1}" -f $second.Status, $(if ($second.Data) { $second.Data.message } else { $second.Raw }))
    Start-Sleep -Seconds 2
    $rowsAfter = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Removed from the download queue' }).Count
    Add-Check "$What : the second removal writes no second Activity row" ($rowsAfter -eq $rowsBefore + 1) "rows before the first removal $rowsBefore, after the second $rowsAfter"
}

function Invoke-GroupRemoval {
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "remove-copy.$kind.folder" -Title "Remove this copy on a finished $kind download (a folder), and removing twice" -Body {
            # Its own film: the films of the films group stay in place, because Weir's scenarios follow them.
            $film = New-FilmFor -Kind $kind -Layout 'folder' -Label 'removal'
            Send-Film -Film $film -Kind $kind | Out-Null
            $r = Wait-FilmImported -Film $film
            Add-Check 'The film was imported first' $r.Imported ''
            Test-RemoveThisCopy -Film $film -What "$kind folder"
        }
    }
    foreach ($kind in $script:Kinds.torrent) {
        Invoke-Scenario -Id "remove-copy.$kind.file" -Title "Remove this copy on a finished $kind download (a single file), and removing twice" -Body {
            $film = New-FilmFor -Kind $kind -Layout 'file' -Label 'single'
            Send-Film -Film $film -Kind $kind | Out-Null
            $r = Wait-FilmImported -Film $film
            Add-Check 'The single-file film was imported first' $r.Imported ''
            Test-RemoveThisCopy -Film $film -What "$kind single file"
        }
    }
}
