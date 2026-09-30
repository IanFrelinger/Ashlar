#!/usr/bin/env bash
# DR Tier B: user knowledge log store durability tests.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

INFRA="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
TRX_DIR="test-results/dr-gate-tier-b"
rm -rf "$TRX_DIR"

echo "== DR Tier B: LiteDB user knowledge log store =="
dotnet build "$INFRA" -v minimal
dotnet test "$INFRA" -f net8.0 --no-build \
  --filter "FullyQualifiedName~LiteDbUserKnowledgeLogStoreTests" \
  --logger "trx;LogFileName=knowledge-log-store.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 60s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/knowledge-log-store.trx"

REPORT_DIR=".ashlar/dr-gate"
mkdir -p "$REPORT_DIR"
echo '{"ok": true, "component": "user-knowledge-log", "timestamp": "'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"}' \
  >"$REPORT_DIR/knowledge-store.json"

echo ""
echo "dr-gate-tier-b: PASS"
