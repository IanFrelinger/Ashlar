#!/usr/bin/env bash
set -euo pipefail

# Test script - run against real GitHub PRs
# Usage: ./test-real-pr.sh [PR_URL] [GITHUB_TOKEN]

PR_URL="${1:-https://github.com/microsoft/terminal/pull/17796}"
GITHUB_TOKEN="${2:-${GITHUB_TOKEN:-}}"

echo "Testing PR Blocker Assistant"
echo "============================="
echo ""
echo "Target: $PR_URL"
echo ""

if [ -n "$GITHUB_TOKEN" ]; then
    export GITHUB_TOKEN
    echo "Using GitHub token (improves rate limits)"
fi

dotnet run --project src/PrBlocker.CLI --configuration Release -- "$PR_URL"
