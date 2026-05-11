#!/usr/bin/env bash
# Mirrors the GitHub Actions CI pipeline.
# Run before every push: bash scripts/ci-check.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "▶  Restoring backend..."
dotnet restore Deluno.slnx -q

echo "▶  Building backend (Release)..."
dotnet build Deluno.slnx --configuration Release --no-restore -q

echo "▶  Installing frontend dependencies..."
npm ci --silent

echo "▶  Building frontend..."
npm run build:web --silent

echo "▶  Agent readiness..."
if command -v pwsh &>/dev/null; then
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/validate-agent-readiness.ps1
else
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate-agent-readiness.ps1
fi

echo ""
echo "✓  All checks passed — safe to push."
