# Records Weir's two deliberate server errors on its intake routes (Weir 1.0.0-rc.18, Weir #964): a retryable
# "503 database_busy" with a Retry-After header, and a JSON "500 internal_error". Runs INSIDE the VM after Record.Lib.ps1.
#
# How Weir's own contract tests make them (apps/server/tests/Weir.Contract.Tests/MediaManagers/DatabaseBusyTests.cs):
#   503  another program holds the database's write lock for longer than WEIR_SQLITE_BUSY_TIMEOUT_SECONDS (1 to 120, 30 unless set)
#   500  a table the route reads is missing from the database
# Neither can be done to the Weir the VM is using, so this starts a SECOND Weir server from the installed one on a free port, with its
# own empty data folder, the shortest busy timeout (1 second) and a webhook secret of its own, does both to that server, and removes it.
# The lock is held with winsqlite3.dll (the SQLite Windows ships), so nothing needs installing. The real Weir is never touched.
#
# Records, as app "weir":
#   database-busy-outcome        an outcome report while the database is locked: 503, Retry-After, {"detail", "code": "database_busy"}
#   database-busy-handoff        a hand-off while the database is locked (named by what it answered; see its note)
#   database-busy-retried        the same outcome report after the lock is gone: Weir answers it (404, no record), so the retry works
#   internal-error-outcome       an outcome report with the hand-offs table missing: 500, {"detail", "code": "internal_error"}

function Invoke-RecordWeirErrors($p) {
    Assert-FreeSpace 2 'the Weir errors'
    $exe = $null
    $running = Get-Process -Name 'WeirServer' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running) { try { $exe = $running.Path } catch { } }
    if (-not $exe) { $exe = (Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Weir') -Recurse -Filter 'WeirServer.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
    if (-not $exe) { Add-Missing 'weir errors: WeirServer.exe was not found in the VM'; return }
    if (-not ('RecordWinSqlite' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RecordWinSqlite {
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPStr)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_close(IntPtr db);
}
'@
    }
    function Invoke-Sql([IntPtr] $Db, [string] $Sql) {
        $err = [IntPtr]::Zero
        $rc = [RecordWinSqlite]::sqlite3_exec($Db, $Sql, [IntPtr]::Zero, [IntPtr]::Zero, [ref]$err)
        if ($rc -ne 0) { throw "SQLite answered $rc to: $Sql" }
    }
    function Open-Sql([string] $File) {
        $db = [IntPtr]::Zero
        $rc = [RecordWinSqlite]::sqlite3_open_v2($File, [ref]$db, 2, [IntPtr]::Zero)   # SQLITE_OPEN_READWRITE
        if ($rc -ne 0) { throw "SQLite could not open $File ($rc)" }
        $db
    }

    $home1 = Join-Path $env:TEMP ('weir_errors_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $null = New-Item -ItemType Directory -Path $home1
    $secretValue = 'recorded-errors-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    Register-Secret $secretValue
    $port = [int](Get-Random -Minimum 20000 -Maximum 29000)
    $process = $null
    function Start-Throwaway {
        $psi = New-Object Diagnostics.ProcessStartInfo($exe)
        $psi.Arguments = "--host 127.0.0.1 --port $port"
        $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
        $psi.WorkingDirectory = Split-Path $exe
        foreach ($pair in @{
                WEIR_HOME = $home1; ASPNETCORE_URLS = "http://127.0.0.1:$port"; WEIR_SESSION_SECRET = 'recorded-errors-session-secret-at-least-32-chars'
                WEIR_SQLITE_BUSY_TIMEOUT_SECONDS = '1'; WEIR_MEDIA_MANAGER_WEBHOOK_SECRET = $secretValue
                WEIR_PROCESSING_WORKER_COUNT = '0'; WEIR_PROCESSING_WATCHER_ENABLED = '0'; WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED = '0'
                WEIR_ARTWORK_GATEWAY_URL = 'off'
            }.GetEnumerator()) { $psi.EnvironmentVariables[$pair.Key] = $pair.Value }
        $started = [Diagnostics.Process]::Start($psi)
        $ready = Wait-Until -Seconds 90 -EverySeconds 1 {
            try { $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/ready" -UseBasicParsing -TimeoutSec 3; if ($r.StatusCode -eq 200 -and ($r.Content | ConvertFrom-Json).ready) { $true } } catch { }
        }
        if (-not $ready) { try { $started.Kill() } catch { }; throw 'The second Weir server did not become ready in 90 s.' }
        $started
    }

    try {
        $process = Start-Throwaway
        $client = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -KeepHeaders @('content-type', 'retry-after')
        $headers = @{ 'X-Webhook-Secret' = $secretValue }
        $caps = Send-Recorded -Client $client -Path '/api/v1/intake/capabilities' -Headers $headers
        $ver = if ((Get-ReplyJson $caps).version) { [string](Get-ReplyJson $caps).version } else { $p.WeirVersion }
        $database = Join-Path $home1 'data\weir.sqlite3'
        if (-not (Test-Path $database)) { Add-Missing "weir errors: the second Weir has no database at $database"; return }
        $outcomePath = '/api/v1/intake/handoffs/deluno/recorded-no-such-handoff/outcome'
        $outcomeBody = [ordered]@{ outcome = 'imported'; occurredUtc = (Get-Date).ToUniversalTime().ToString('o') }
        $handoffBody = [ordered]@{ eventType = 'deluno.processor-handoff'; handoffId = 'recorded-busy-handoff'; libraryId = 'recorded-library'; mediaType = 'movie'; sourcePath = 'C:\golden\recorded\nowhere\film.mkv'; releaseName = 'Recorded busy'; queueItemId = 'q1'; callbackPath = '/api/integrations/processors/events' }

        # ---- 503: the database is locked by something outside Weir
        $db = Open-Sql $database
        try {
            Invoke-Sql $db 'BEGIN IMMEDIATE'
            $busy = Send-Recorded -Client $client -Method POST -Path $outcomePath -Headers $headers -Json $outcomeBody
            $busyHandoff = Send-Recorded -Client $client -Method POST -Path '/api/v1/intake/webhook/deluno' -Headers $headers -Json $handoffBody
        } finally {
            try { Invoke-Sql $db 'ROLLBACK' } catch { }
            $null = [RecordWinSqlite]::sqlite3_close($db)
        }
        if ($busy.reply.status -eq 503) { Save-State weir $ver 'database-busy-outcome' 'An outcome report while another program holds the database write lock for longer than the busy timeout (set to 1 s on a second Weir server made for this): 503 with Retry-After and the code database_busy.' @($busy) }
        else { Add-Missing "weir database-busy-outcome: the outcome report under a held lock was HTTP $($busy.reply.status), not 503" }
        if ($busyHandoff.reply.status -eq 503) { Save-State weir $ver 'database-busy-handoff' 'A hand-off while the database write lock is held elsewhere: 503 with Retry-After and the code database_busy.' @($busyHandoff) }
        else { Add-Missing "weir database-busy-handoff: a hand-off under a held lock was HTTP $($busyHandoff.reply.status) (it is refused before it writes, as there is no such library on the second server); the outcome report is the recorded 503" }
        $retried = Send-Recorded -Client $client -Method POST -Path $outcomePath -Headers $headers -Json $outcomeBody
        Save-State weir $ver 'database-busy-retried' "The same outcome report sent again once the lock was gone: HTTP $($retried.reply.status), Weir's ordinary answer for a hand-off it has no record of, so a 503 can be retried." @($retried)

        # ---- 500: a table the route reads is missing
        $process.Kill(); $process.WaitForExit(); $process = $null
        Start-Sleep -Seconds 2
        $db = Open-Sql $database
        try { Invoke-Sql $db 'PRAGMA foreign_keys=OFF'; Invoke-Sql $db 'DROP TABLE media_manager_handoffs' }
        finally { $null = [RecordWinSqlite]::sqlite3_close($db) }
        $process = Start-Throwaway
        $broken = Send-Recorded -Client $client -Method POST -Path $outcomePath -Headers $headers -Json $outcomeBody
        if ($broken.reply.status -eq 500) { Save-State weir $ver 'internal-error-outcome' 'An outcome report to a second Weir server whose hand-offs table had been dropped: 500 with a JSON body {"detail", "code": "internal_error"} and nothing of the failure in it.' @($broken) }
        else { Add-Missing "weir internal-error-outcome: the outcome report with the hand-offs table missing was HTTP $($broken.reply.status), not 500" }
    } finally {
        if ($process) { try { $process.Kill(); $process.WaitForExit() } catch { } }
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $home1 -Recurse -Force -ErrorAction SilentlyContinue
    }
}
