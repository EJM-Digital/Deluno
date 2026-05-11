#!/usr/bin/env bash
# Mirrors the GitHub Actions CI pipeline. Run before every push.
# Usage: bash scripts/ci-check.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PASS=0; WARN=0; FAIL=0

ok()   { echo "  ✓  $*"; PASS=$((PASS+1)); }
warn() { echo "  ⚠  $*"; WARN=$((WARN+1)); }
fail() { echo "  ✗  $*"; FAIL=$((FAIL+1)); }

# Detect whether a dotnet build failure is caused by the local dev server locking
# output files — an environment issue, not a code error. Captures stdout+stderr.
is_env_lock() {
  grep -qE "MSB3027|MSB3021|MSB3492|being used by another process|locked by:" "$1"
}

# ── Backend ──────────────────────────────────────────────────────────────────
echo ""
echo "▶  Backend"

restore_log=$(mktemp)
if dotnet restore Deluno.slnx -q >"$restore_log" 2>&1; then
  ok "restore"
else
  cat "$restore_log"; fail "restore failed"
fi
rm -f "$restore_log"

build_log=$(mktemp)
if dotnet build Deluno.slnx --configuration Release --no-restore -q >"$build_log" 2>&1; then
  ok "build"
elif is_env_lock "$build_log"; then
  warn "Release outputs locked by running server — CI will catch real errors"
else
  cat "$build_log"; fail "build failed"
fi
rm -f "$build_log"

# ── Tray — Linux CI simulation ────────────────────────────────────────────────
echo ""
echo "▶  Tray (Linux CI simulation)"

tray_log=$(mktemp)
if dotnet build apps/windows-tray/Deluno.Tray.csproj \
     -p:SimulateLinuxCI=true --configuration Release --no-restore -q >"$tray_log" 2>&1; then
  ok "tray builds as empty Library"
elif is_env_lock "$tray_log"; then
  warn "Tray build locked by running server — CI will catch real errors"
else
  cat "$tray_log"; fail "tray would fail on Linux CI"
fi
rm -f "$tray_log"

# ── Frontend ──────────────────────────────────────────────────────────────────
echo ""
echo "▶  Frontend"

# Only run npm ci if node_modules is missing or stale; avoid destroying the
# running Vite dev server's module graph unnecessarily.
if [ ! -d node_modules ] || [ package-lock.json -nt node_modules/.package-lock.json ]; then
  echo "   npm ci (node_modules out of date)..."
  npm_log=$(mktemp)
  if npm ci --silent >"$npm_log" 2>&1; then
    ok "npm ci"
  else
    cat "$npm_log"; fail "npm ci failed"
  fi
  rm -f "$npm_log"
else
  ok "npm ci (node_modules current, skipped)"
fi

web_log=$(mktemp)
if npm run build:web --silent >"$web_log" 2>&1; then
  ok "build:web"
else
  cat "$web_log"; fail "build:web failed"
fi
rm -f "$web_log"

# ── Agent readiness ───────────────────────────────────────────────────────────
echo ""
echo "▶  Agent readiness"

if command -v pwsh &>/dev/null; then PS_CMD=pwsh; else PS_CMD=powershell; fi
agents_log=$(mktemp)
if "$PS_CMD" -NoProfile -ExecutionPolicy Bypass \
     -File scripts/validate-agent-readiness.ps1 >"$agents_log" 2>&1; then
  ok "agent readiness"
else
  cat "$agents_log"; fail "agent readiness"
fi
rm -f "$agents_log"

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo "──────────────────────────────────────"
echo "  Passed: $PASS  Warned: $WARN  Failed: $FAIL"
echo "──────────────────────────────────────"

if [ "$FAIL" -gt 0 ]; then
  echo "  ✗  Fix the failures above before pushing."
  exit 1
elif [ "$WARN" -gt 0 ]; then
  echo "  ⚠  Warnings present (env locks from running server) — safe to push."
else
  echo "  ✓  All checks passed — safe to push."
fi
