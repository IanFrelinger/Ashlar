#!/usr/bin/env bash
# Demo receipt script: start API with mock, post canned task, print plain receipt + URL.
# Fails fast if .NET SDK is missing — does NOT silently install .NET.
set -euo pipefail

# Check for .NET SDK
if ! command -v dotnet &> /dev/null; then
  echo "Error: .NET SDK not found. Install .NET SDK 10.x first."
  echo "Download from: https://dotnet.microsoft.com/download"
  exit 1
fi

# Check SDK version
SDK_VERSION=$(dotnet --version 2>/dev/null || echo "")
if [[ ! "$SDK_VERSION" =~ ^10\. ]]; then
  echo "Error: .NET SDK 10.x required. Found: $SDK_VERSION"
  echo "See global.json for the required version."
  exit 1
fi

echo "=== Ashlar Demo Receipt ==="
echo "Starting API with mock provider..."
echo

# Find repo root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

# Start API in background
export ASHLAR_ALLOW_MOCK=1
export ASPNETCORE_URLS=http://localhost:5678
API_PROJECT="application/src/Ashlar.API/Ashlar.API.csproj"

# Check if project exists
if [[ ! -f "$API_PROJECT" ]]; then
  echo "Error: API project not found at $API_PROJECT"
  exit 1
fi

echo "Building API..."
dotnet build "$API_PROJECT" --configuration Release --verbosity quiet || {
  echo "Error: Build failed. Run 'dotnet build $API_PROJECT' to see details."
  exit 1
}

echo "Starting API on http://localhost:5678 (mock provider)..."
dotnet run --project "$API_PROJECT" --no-build --configuration Release &> /tmp/ashlar-demo-api.log &
API_PID=$!

# Cleanup on exit
cleanup() {
  echo
  echo "Stopping API..."
  kill "$API_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  rm -f /tmp/ashlar-demo-api.log
}
trap cleanup EXIT INT TERM

# Wait for API to be ready
echo "Waiting for API to start..."
MAX_WAIT=30
ELAPSED=0
until curl -sf http://localhost:5678/health > /dev/null 2>&1; do
  if [[ $ELAPSED -ge $MAX_WAIT ]]; then
    echo "Error: API did not start within ${MAX_WAIT}s"
    echo "Check logs at /tmp/ashlar-demo-api.log"
    exit 1
  fi
  sleep 1
  ((ELAPSED++))
done

echo "API ready!"
echo

# Post canned task
echo "Submitting canned task: 'Summarize what this repository does'"
TASK_JSON='{"task": "Summarize what this repository does", "auditCount": 5}'

RESPONSE=$(curl -sf -X POST http://localhost:5678/api/copilot/task \
  -H "Content-Type: application/json" \
  -d "$TASK_JSON" 2>&1) || {
  echo "Error: Task submission failed"
  echo "Response: $RESPONSE"
  exit 1
}

# Parse response
TASK_ID=$(echo "$RESPONSE" | grep -o '"taskId":"[^"]*"' | cut -d'"' -f4)
SUCCESS=$(echo "$RESPONSE" | grep -o '"success":[^,}]*' | cut -d':' -f2)
TENANT_ID=$(echo "$RESPONSE" | grep -o '"tenantId":"[^"]*"' | cut -d'"' -f4)

echo
echo "=== RECEIPT ==="
echo
echo "Task:           Summarize what this repository does"
echo "Outcome:        $([ "$SUCCESS" = "true" ] && echo "Succeeded" || echo "Failed")"
echo "Record ID:      $TASK_ID"
echo "Tenant:         $TENANT_ID"
echo "Provider:       mock (smoke-only plumbing — not an admission certificate)"
echo
echo "What was recorded (post-execution audit only, not admit-before-run):"
echo "  - Task submitted at $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
echo "  - Orchestration completed"
echo "  - Audit trail updated (CopilotTask entry)"
echo
echo "View full details:"
echo "  Task record:    http://localhost:5678/api/copilot/tasks/$TASK_ID"
echo "  Audit trail:    http://localhost:5678/api/trust/dashboard"
echo "  Portal:         http://localhost:5678"
echo
echo "=== END RECEIPT ==="
echo

read -p "Press Enter to stop the API and exit..."
