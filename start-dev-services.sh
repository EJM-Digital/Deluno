#!/bin/bash
# Start Deluno Development Services (Backend + Frontend)
# Run with: ./start-dev-services.sh [--skip-backend] [--skip-frontend]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_PORT=${BACKEND_PORT:-5099}
FRONTEND_PORT=${FRONTEND_PORT:-5173}
SKIP_BACKEND=false
SKIP_FRONTEND=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --skip-backend)
            SKIP_BACKEND=true
            shift
            ;;
        --skip-frontend)
            SKIP_FRONTEND=true
            shift
            ;;
        *)
            shift
            ;;
    esac
done

echo "🚀 Starting Deluno Development Services"
echo "   Backend port: $BACKEND_PORT"
echo "   Frontend port: $FRONTEND_PORT"
echo ""

# Cleanup function
cleanup_port() {
    local port=$1
    if lsof -Pi :$port -sTCP:LISTEN -t >/dev/null 2>&1; then
        echo "   Cleaning up port $port..."
        lsof -Pi :$port -sTCP:LISTEN -t | xargs kill -9 2>/dev/null || true
        sleep 1
    fi
}

# Start Backend
if [ "$SKIP_BACKEND" = false ]; then
    echo "📦 Starting ASP.NET Core Backend..."
    cleanup_port $BACKEND_PORT

    BACKEND_PROJ="$SCRIPT_DIR/src/Deluno.Host/Deluno.Host.csproj"
    if [ ! -f "$BACKEND_PROJ" ]; then
        echo "❌ Backend project not found: $BACKEND_PROJ"
        exit 1
    fi

    BACKEND_LOG="$SCRIPT_DIR/deluno-backend.log"
    cd "$SCRIPT_DIR"
    nohup dotnet run --project "$BACKEND_PROJ" > "$BACKEND_LOG" 2>&1 &
    BACKEND_PID=$!
    echo "   Backend process started (PID: $BACKEND_PID), logging to: deluno-backend.log"

    # Wait for backend to be ready
    for i in {1..30}; do
        if curl -s "http://localhost:$BACKEND_PORT/api/jobs/queue" -w "%{http_code}" -o /dev/null 2>/dev/null | grep -q "401\|200"; then
            echo "   ✅ Backend is ready on port $BACKEND_PORT"
            break
        fi
        if [ $i -eq 30 ]; then
            echo "   ⚠️  Backend did not respond within 30 seconds (check deluno-backend.log)"
        fi
        sleep 1
    done
fi

# Start Frontend
if [ "$SKIP_FRONTEND" = false ]; then
    echo "⚛️  Starting React Frontend..."
    cleanup_port $FRONTEND_PORT

    APP_DIR="$SCRIPT_DIR/app"
    if [ ! -f "$APP_DIR/package.json" ]; then
        echo "❌ Frontend project not found: $APP_DIR/package.json"
        exit 1
    fi

    FRONTEND_LOG="$SCRIPT_DIR/deluno-frontend.log"
    cd "$APP_DIR"
    nohup npm run dev > "$FRONTEND_LOG" 2>&1 &
    FRONTEND_PID=$!
    echo "   Frontend process started (PID: $FRONTEND_PID), logging to: deluno-frontend.log"

    # Wait for frontend to be ready
    for i in {1..20}; do
        if curl -s "http://localhost:$FRONTEND_PORT/" -w "%{http_code}" -o /dev/null 2>/dev/null | grep -q "200"; then
            echo "   ✅ Frontend is ready on port $FRONTEND_PORT"
            break
        fi
        if [ $i -eq 20 ]; then
            echo "   ⚠️  Frontend did not respond within 20 seconds (check deluno-frontend.log)"
        fi
        sleep 1
    done
fi

echo ""
echo "✨ Deluno Development Services Started"
echo "   🌐 Frontend: http://localhost:$FRONTEND_PORT"
echo "   🔌 Backend:  http://localhost:$BACKEND_PORT"
echo "   📋 Logs:     deluno-backend.log, deluno-frontend.log"
echo ""
echo "To stop the services, press Ctrl+C or run: pkill -f 'dotnet run' && pkill -f 'npm run dev'"
