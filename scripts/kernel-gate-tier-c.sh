#!/usr/bin/env bash
# Tier C kernel gate: extended ProdStyle (Infrastructure), workflow executor, optional mesh lab.
# See docs/production-readiness/KernelHardeningPlan-v1.md
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

INFRA="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
TRANSPORT="src/Ashlar.Tests.Transport/Ashlar.Tests.Transport.csproj"
TRX_DIR="test-results/kernel-gate-tier-c"
rm -rf "$TRX_DIR"

echo "== Tier C: ProdStyle Infrastructure (net8, FluentAssertions-safe filter) =="
make test-prod-style

echo "== Tier C: workflow executor integration =="
dotnet build "$INFRA" -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA" -f net8.0 --no-build \
  --filter "FullyQualifiedName~WorkflowExecutorIntegrationTests" \
  --logger "trx;LogFileName=workflow-executor.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/workflow-executor.trx"

echo "== Tier C: gRPC transport ProdStyle =="
if [ -f "$TRANSPORT" ]; then
  dotnet build "$TRANSPORT" -v minimal
  dotnet test "$TRANSPORT" -f net8.0 --no-build \
    --filter "Category=ProdStyle" \
    --logger "trx;LogFileName=transport-prodstyle.trx" --results-directory "$TRX_DIR" \
    --blame-hang-timeout 120s --blame-hang-dump-type none
  bash scripts/ci/zero-test-guard.sh "$TRX_DIR/transport-prodstyle.trx"
else
  echo "Skip: $TRANSPORT not found"
fi

echo "== Tier C: air-gapped profile smoke (in-process) =="
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA" -f net8.0 --no-build \
  --filter "FullyQualifiedName~AirGapped" \
  --logger "trx;LogFileName=air-gapped-profile.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 180s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/air-gapped-profile.trx"

if [ "${KERNEL_GATE_MESH_E2E:-0}" = "1" ] && [ -f ".env.mesh-lab" ]; then
  echo "== Tier C: mesh virtual lab E2E (compose up + verify + down) =="
  bash scripts/run-mesh-lab-e2e.sh .env.mesh-lab
elif [ -f ".env.mesh-lab" ]; then
  echo "== Tier C: mesh lab skipped (set KERNEL_GATE_MESH_E2E=1 for full E2E; or run: make mesh-lab-e2e) =="
else
  echo "== Tier C: mesh lab skipped (run: make bootstrap-mesh-lab-env) =="
fi

echo ""
echo "kernel-gate-tier-c: PASS"
