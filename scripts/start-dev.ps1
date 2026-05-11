# Deluno local dev launcher
# Starts backend on :5099 (dotnet watch) and frontend on :5173 (vite)
# Press Ctrl+C to stop both.

$Root = Split-Path $PSScriptRoot -Parent
$BackendDir = Join-Path $Root "src\Deluno.Host"
$FrontendDir = Join-Path $Root "apps\web"

# Kill anything already on the ports
foreach ($port in 5099, 5173) {
    $pid = (Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue).OwningProcess | Select-Object -First 1
    if ($pid) { Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue }
}

Write-Host "Starting backend  (http://127.0.0.1:5099) ..." -ForegroundColor Cyan
$backend = Start-Process powershell -ArgumentList "-NoProfile -Command `"cd '$BackendDir'; dotnet watch run`"" -PassThru -WindowStyle Minimized

Write-Host "Starting frontend (http://127.0.0.1:5173) ..." -ForegroundColor Cyan
$frontend = Start-Process powershell -ArgumentList "-NoProfile -Command `"cd '$FrontendDir'; npm run dev`"" -PassThru -WindowStyle Minimized

Write-Host ""
Write-Host "Both servers are running." -ForegroundColor Green
Write-Host "  Backend  -> http://127.0.0.1:5099" -ForegroundColor Gray
Write-Host "  Frontend -> http://127.0.0.1:5173" -ForegroundColor Gray
Write-Host ""
Write-Host "Press Ctrl+C to stop both servers." -ForegroundColor Yellow

try {
    while ($true) { Start-Sleep -Seconds 5 }
} finally {
    Write-Host "Stopping servers..." -ForegroundColor Yellow
    Stop-Process -Id $backend.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $frontend.Id -Force -ErrorAction SilentlyContinue
}
