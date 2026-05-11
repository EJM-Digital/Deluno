#!/bin/bash
# Deluno Services Health Check
# Run with: ./health-check.sh

BACKEND_PORT=${BACKEND_PORT:-5099}
FRONTEND_PORT=${FRONTEND_PORT:-5173}

echo "🏥 Deluno Health Check"
echo ""

# Check Backend
echo "📦 Backend (localhost:$BACKEND_PORT)"
if lsof -Pi :$BACKEND_PORT -sTCP:LISTEN -t >/dev/null 2>&1; then
    BACKEND_PID=$(lsof -Pi :$BACKEND_PORT -sTCP:LISTEN -t)
    echo "   ✅ Listening: Yes (PID: $BACKEND_PID)"

    HTTP_CODE=$(curl -s -w "%{http_code}" -o /dev/null "http://localhost:$BACKEND_PORT/api/jobs/queue" 2>/dev/null)
    if [ "$HTTP_CODE" = "401" ] || [ "$HTTP_CODE" = "200" ]; then
        echo "   ✅ HTTP Response: $HTTP_CODE (auth required)"
    else
        echo "   ❌ HTTP Response: $HTTP_CODE"
    fi
else
    echo "   ❌ Listening: No"
    echo "   💡 Run: ./start-dev-services.sh"
fi

echo ""

# Check Frontend
echo "⚛️  Frontend (localhost:$FRONTEND_PORT)"
if lsof -Pi :$FRONTEND_PORT -sTCP:LISTEN -t >/dev/null 2>&1; then
    FRONTEND_PID=$(lsof -Pi :$FRONTEND_PORT -sTCP:LISTEN -t)
    echo "   ✅ Listening: Yes (PID: $FRONTEND_PID)"

    HTTP_CODE=$(curl -s -w "%{http_code}" -o /dev/null "http://localhost:$FRONTEND_PORT/" 2>/dev/null)
    if [ "$HTTP_CODE" = "200" ]; then
        echo "   ✅ HTTP Response: 200 OK"
    else
        echo "   ❌ HTTP Response: $HTTP_CODE"
    fi
else
    echo "   ❌ Listening: No"
    echo "   💡 Run: ./start-dev-services.sh"
fi

echo ""

# Check Logs
echo "📋 Logs"
if [ -f "deluno-backend.log" ]; then
    SIZE=$(stat -f%z deluno-backend.log 2>/dev/null || stat -c%s deluno-backend.log 2>/dev/null)
    echo "   ✅ deluno-backend.log ($SIZE bytes)"
else
    echo "   ⚠️  deluno-backend.log (not found)"
fi

if [ -f "deluno-frontend.log" ]; then
    SIZE=$(stat -f%z deluno-frontend.log 2>/dev/null || stat -c%s deluno-frontend.log 2>/dev/null)
    echo "   ✅ deluno-frontend.log ($SIZE bytes)"
else
    echo "   ⚠️  deluno-frontend.log (not found)"
fi

echo ""
echo "Access Deluno at: http://localhost:$FRONTEND_PORT"
