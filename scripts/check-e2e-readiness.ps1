# Is Deluno ready for the comprehensive end-to-end test?
#
# Ready means the core loop completes on the lab rig for at least one title:
# search -> grab -> download -> refine -> import -> a file in the library.
#
# Until that works there is nothing to run a 94-step plan against, because
# every phase after 8 assumes a library with something in it.
#
# Prints one line per stage and a final READY / NOT READY.

$ErrorActionPreference = "Stop"
$b = "http://192.168.1.142:5099"

$auth = (Invoke-RestMethod -Uri "$b/api/auth/login" -Method Post `
    -Body (@{ username = "admin"; password = "***REMOVED***" } | ConvertTo-Json) `
    -ContentType "application/json").accessToken
$h = @{ Authorization = "Bearer $auth" }

$movie = (Invoke-RestMethod -Uri "$b/api/movies/page?pageSize=5" -Headers $h).items |
    Where-Object { $_.title -eq "Big Buck Bunny" } | Select-Object -First 1
$dispatch = (Invoke-RestMethod -Uri "$b/api/v1/download-dispatches?pageSize=5" -Headers $h).items |
    Select-Object -First 1
$queue = (Invoke-RestMethod -Uri "$b/api/download-clients/telemetry" -Headers $h).clients |
    Where-Object { $_.clientName -eq "qBittorrent" } | Select-Object -ExpandProperty queue

$pw = ConvertTo-SecureString "***REMOVED***" -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential("Administrator", $pw)
$folders = Invoke-Command -ComputerName 192.168.1.142 -Credential $cred -ScriptBlock {
    [pscustomobject]@{
        Downloads = (Get-ChildItem 'C:\Deluno\Downloads-Complete\Movies' -ErrorAction SilentlyContinue).Count
        Refined   = (Get-ChildItem 'C:\Deluno\Refined\Movies' -ErrorAction SilentlyContinue).Count
        Library   = (Get-ChildItem 'C:\Deluno\Library\Movies' -ErrorAction SilentlyContinue).Count
    }
}

"grab          : dispatch=$($dispatch.status) queueItem='$($dispatch.torrentHashOrItemId)'"
"client        : $($queue.Count) item(s), status=$($queue.status -join ',')"
"downloaded    : $($folders.Downloads) in Downloads-Complete\Movies"
"refined       : $($folders.Refined) in Refined\Movies"
"library       : $($folders.Library) in Library\Movies"
"catalogue     : hasFile=$($movie.hasFile) wanted=$($movie.wantedStatus)"
"import status : '$($dispatch.importStatus)'"

if ($movie.hasFile -and $folders.Library -gt 0) {
    ""
    "READY - the core loop completes. The comprehensive end-to-end test can start."
    exit 0
}

$stalledAt = if ($folders.Refined -gt 0) { "refine (nothing imports)" }
    elseif ($folders.Downloads -gt 0) { "download (nothing refines)" }
    elseif ($dispatch.status -eq "sent") { "grab (nothing downloads)" }
    else { "search/grab" }
""
"NOT READY - the loop stops after: $stalledAt"
exit 1
