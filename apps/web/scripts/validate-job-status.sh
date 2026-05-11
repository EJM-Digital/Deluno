#!/bin/bash

# Validation script to detect hardcoded job status strings in the frontend.
# This ensures all job status comparisons use the shared JOB_STATUS constants.

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
SRC_DIR="$SCRIPT_DIR/../src"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Search for hardcoded job status strings
# Exclude job-status-constants.ts and files using JOB_STATUS correctly
VIOLATIONS=$(
  find "$SRC_DIR" -type f \( -name "*.tsx" -o -name "*.ts" \) \
  ! -path "*/node_modules/*" \
  ! -path "*/dist/*" \
  ! -name "*job-status-constants*" \
  ! -name "*test*" \
  | xargs grep -l '\(=== *["'"'"']\(queued\|running\|completed\|failed\)["'"'"']\|== *["'"'"']\(queued\|running\|completed\|failed\)["'"'"']\)' \
  | xargs grep -L 'JOB_STATUS' \
  | xargs grep -L 'isJobActive\|isJobInProgress\|isJobDone' \
  | wc -l
)

if [ "$VIOLATIONS" -gt 0 ]; then
  echo -e "${RED}❌ Found ${VIOLATIONS} file(s) with hardcoded job status strings:${NC}\n"

  find "$SRC_DIR" -type f \( -name "*.tsx" -o -name "*.ts" \) \
  ! -path "*/node_modules/*" \
  ! -path "*/dist/*" \
  ! -name "*job-status-constants*" \
  ! -name "*test*" \
  | xargs grep -n '\(=== *["'"'"']\(queued\|running\|completed\|failed\)["'"'"']\|== *["'"'"']\(queued\|running\|completed\|failed\)["'"'"']\)' \
  | grep -v 'JOB_STATUS' \
  | grep -v 'isJobActive\|isJobInProgress\|isJobDone'

  echo -e "\n${BLUE}💡 Use JOB_STATUS constants instead:${NC}"
  echo "   import { JOB_STATUS, isJobActive } from '../lib/job-status-constants';"
  echo "   if (isJobActive(status)) { ... }"

  exit 1
else
  echo -e "${GREEN}✅ No hardcoded job status strings found!${NC}"
  exit 0
fi
