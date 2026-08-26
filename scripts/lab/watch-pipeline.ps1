$login = Invoke-RestMethod -Uri 'http://192.168.1.142:5099/api/auth/login' -Method Post -ContentType 'application/json' -Body (@{username='admin';password='***REMOVED***'} | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($login.accessToken)" }
$o = Invoke-RestMethod -Uri 'http://192.168.1.142:5099/api/download-clients/telemetry' -Headers $h
"summary : " + ($o.summary | ConvertTo-Json -Compress)
"queue   : " + (($o.clients.queue | ForEach-Object { "$($_.status)" }) -join ', ')
$hf = Invoke-RestMethod -Uri 'http://192.168.1.142:5099/api/integrations/processors/handoffs' -Headers $h
$rows = if ($hf.items) { $hf.items } else { $hf }
"handoffs: " + ($rows.Count)
$rows | Select-Object releaseName, status, outputPath, importJobId | Format-List
"jobs    :"
(Invoke-RestMethod -Uri 'http://192.168.1.142:5099/api/jobs' -Headers $h).items | Select-Object jobType, status, lastError | Format-Table -AutoSize
"activity:"
(Invoke-RestMethod -Uri 'http://192.168.1.142:5099/api/activity' -Headers $h).items | Where-Object { $_.category -match 'processing|import|dispatch' } | Select-Object -First 8 category, message | Format-Table -AutoSize -Wrap
