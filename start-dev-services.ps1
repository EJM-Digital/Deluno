# Start Deluno Development Services (Backend + Frontend)
# Run with: .\start-dev-services.ps1

param(
    [switch]$SkipBackend,
    [switch]$SkipFrontend,
    [int]$BackendPort = 5099,
    [int]$FrontendPort = 5173
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "🚀 Starting Deluno Development Services" -ForegroundColor Cyan
Write-Host "   Backend port: $BackendPort" -ForegroundColor Gray
Write-Host "   Frontend port: $FrontendPort" -ForegroundColor Gray
Write-Host ""

# Cleanup function
function Cleanup-Port {
    param([int]$Port)
    $conn = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue
    if ($conn) {
        Write-Host "   Cleaning up port $Port (PID: $($conn.OwningProcess))..." -ForegroundColor Yellow
        Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}

# Start Backend
if (-not $SkipBackend) {
    Write-Host "📦 Starting ASP.NET Core Backend..." -ForegroundColor Green
    Cleanup-Port -Port $BackendPort

    $backendProj = Join-Path $scriptRoot "src\Deluno.Host\Deluno.Host.csproj"
    if (-not (Test-Path $backendProj)) {
        Write-Host "❌ Backend project not found: $backendProj" -ForegroundColor Red
        exit 1
    }

    $backendLog = Join-Path $scriptRoot "deluno-backend.log"
    Start-Process dotnet -ArgumentList "run", "--project", $backendProj -RedirectStandardOutput $backendLog -RedirectStandardError $backendLog -NoNewWindow
    Write-Host "   Backend process started, logging to: deluno-backend.log" -ForegroundColor Gray

    # Wait for backend to be ready
    $maxWait = 30
    $waited = 0
    while ($waited -lt $maxWait) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:$BackendPort/api/jobs/queue" -Method Get -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 401 -or $response.StatusCode -eq 200) {
                Write-Host "   ✅ Backend is ready on port $BackendPort" -ForegroundColor Green
                break
            }
        } catch {
            # Still starting
        }
        Start-Sleep -Seconds 1
        $waited++
    }

    if ($waited -ge $maxWait) {
        Write-Host "   ⚠️  Backend did not respond within $maxWait seconds (check deluno-backend.log)" -ForegroundColor Yellow
    }
}

# Start Frontend
if (-not $SkipFrontend) {
    Write-Host "⚛️  Starting React Frontend..." -ForegroundColor Green
    Cleanup-Port -Port $FrontendPort

    $packageJson = Join-Path $scriptRoot "app\package.json"
    if (-not (Test-Path $packageJson)) {
        Write-Host "❌ Frontend project not found: $packageJson" -ForegroundColor Red
        exit 1
    }

    $frontendLog = Join-Path $scriptRoot "deluno-frontend.log"
    Push-Location (Join-Path $scriptRoot "app")
    Start-Process npm -ArgumentList "run", "dev" -RedirectStandardOutput $frontendLog -RedirectStandardError $frontendLog -NoNewWindow
    Pop-Location
    Write-Host "   Frontend process started, logging to: deluno-frontend.log" -ForegroundColor Gray

    # Wait for frontend to be ready
    $maxWait = 20
    $waited = 0
    while ($waited -lt $maxWait) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:$FrontendPort/" -Method Get -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                Write-Host "   ✅ Frontend is ready on port $FrontendPort" -ForegroundColor Green
                break
            }
        } catch {
            # Still starting
        }
        Start-Sleep -Seconds 1
        $waited++
    }

    if ($waited -ge $maxWait) {
        Write-Host "   ⚠️  Frontend did not respond within $maxWait seconds (check deluno-frontend.log)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "✨ Deluno Development Services Started" -ForegroundColor Cyan
Write-Host "   🌐 Frontend: http://localhost:$FrontendPort" -ForegroundColor Green
Write-Host "   🔌 Backend:  http://localhost:$BackendPort" -ForegroundColor Green
Write-Host "   📋 Logs:     deluno-backend.log, deluno-frontend.log" -ForegroundColor Gray
Write-Host ""
Write-Host "To stop the services, use the system taskbar or 'taskkill' command." -ForegroundColor Gray
