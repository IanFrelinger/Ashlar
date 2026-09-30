#!/usr/bin/env bash
# Perf Tier A: micro-benchmark / performance-scoped unit tests.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REPORT_DIR=".ashlar/perf"
mkdir -p "$REPORT_DIR"
# A stale TRX from an earlier local run would satisfy the guard below without this run writing one.
rm -f "$REPORT_DIR/perf-orchestration.trx" "$REPORT_DIR/perf-background-agents.trx"

ORCH="src/Ashlar.Tests.Orchestration/Ashlar.Tests.Orchestration.csproj"
BG="src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj"

echo "== Perf Tier A: orchestration performance tests =="
dotnet build "$ORCH" -v minimal
dotnet test "$ORCH" -f net8.0 --no-build \
  --filter "FullyQualifiedName~Ashlar.Tests.Orchestration.Performance" \
  --logger "trx;LogFileName=perf-orchestration.trx" \
  --results-directory "$REPORT_DIR" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$REPORT_DIR/perf-orchestration.trx"

echo "== Perf Tier A: background agent performance tests =="
dotnet build "$BG" -v minimal
dotnet test "$BG" -f net8.0 --no-build \
  --filter "FullyQualifiedName~Ashlar.Tests.BackgroundAgents.Performance" \
  --logger "trx;LogFileName=perf-background-agents.trx" \
  --results-directory "$REPORT_DIR" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$REPORT_DIR/perf-background-agents.trx"

echo ""
echo "perf-gate-tier-a: PASS"
