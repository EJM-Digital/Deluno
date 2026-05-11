# Deluno Services Health Check
# Run with: .\health-check.ps1

param(
    [int]$BackendPort = 5099,
    [int]$FrontendPort = 5173
)

$ErrorActionPreference = "SilentlyContinue"

Write-Host "🏥 Deluno Health Check" -ForegroundColor Cyan
Write-Host ""

# Check Backend
Write-Host "📦 Backend (localhost:$BackendPort)" -ForegroundColor Cyan
$backendConn = Get-NetTCPConnection -LocalPort $BackendPort -ErrorAction SilentlyContinue
if ($backendConn) {
    Write-Host "   ✅ Listening: Yes (PID: $($backendConn.OwningProcess))" -ForegroundColor Green

    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$BackendPort/api/jobs/queue" -Method Get -TimeoutSec 5 -ErrorAction Stop
        Write-Host "   ✅ HTTP Response: 200 OK (auth required)" -ForegroundColor Green
    } catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 401) {
            Write-Host "   ✅ HTTP Response: 401 Unauthorized (expected, auth required)" -ForegroundColor Green
        } else {
            Write-Host "   ❌ HTTP Response: $($_.Exception.Response.StatusCode.Value__)" -ForegroundColor Red
        }
    }
} else {
    Write-Host "   ❌ Listening: No" -ForegroundColor Red
    Write-Host "   💡 Run: .\start-dev-services.ps1" -ForegroundColor Yellow
}

Write-Host ""

# Check Frontend
Write-Host "⚛️  Frontend (localhost:$FrontendPort)" -ForegroundColor Cyan
$frontendConn = Get-NetTCPConnection -LocalPort $FrontendPort -ErrorAction SilentlyContinue
if ($frontendConn) {
    Write-Host "   ✅ Listening: Yes (PID: $($frontendConn.OwningProcess))" -ForegroundColor Green

    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$FrontendPort/" -Method Get -TimeoutSec 5 -ErrorAction Stop
        if ($response.StatusCode -eq 200) {
            Write-Host "   ✅ HTTP Response: 200 OK" -ForegroundColor Green
        }
    } catch {
        Write-Host "   ❌ HTTP Response: $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "   ❌ Listening: No" -ForegroundColor Red
    Write-Host "   💡 Run: .\start-dev-services.ps1" -ForegroundColor Yellow
}

Write-Host ""

# Check Logs
Write-Host "📋 Logs" -ForegroundColor Cyan
if (Test-Path "deluno-backend.log") {
    $size = (Get-Item "deluno-backend.log").Length
    Write-Host "   ✅ deluno-backend.log ($($size) bytes)" -ForegroundColor Green
} else {
    Write-Host "   ⚠️  deluno-backend.log (not found)" -ForegroundColor Yellow
}

if (Test-Path "deluno-frontend.log") {
    $size = (Get-Item "deluno-frontend.log").Length
    Write-Host "   ✅ deluno-frontend.log ($($size) bytes)" -ForegroundColor Green
} else {
    Write-Host "   ⚠️  deluno-frontend.log (not found)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Access Deluno at: http://localhost:$FrontendPort" -ForegroundColor Gray
