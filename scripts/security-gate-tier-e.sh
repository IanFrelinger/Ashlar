#!/usr/bin/env bash
# Security Tier E: air-gapped no-network smoke (kernel/profile + safety probes).
# Validates that core flows do not require network egress.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

INFRA="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
TRX_DIR="test-results/security-gate-tier-e"
rm -rf "$TRX_DIR"

echo "== Security Tier E: air-gapped + safety in-process tests =="
dotnet build "$INFRA" -f net8.0 -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA" -f net8.0 --no-build \
  --filter "FullyQualifiedName~AirGapped|FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Safety" \
  --logger "trx;LogFileName=air-gapped-safety.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 180s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/air-gapped-safety.trx"

if [ "${SECURITY_GATE_AIRGAPPED_CONTAINER:-0}" = "1" ]; then
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    echo "== Security Tier E: --network none container suite =="
    dotnet run --project application/src/Ashlar.CLI/Ashlar.CLI.csproj -- \
      test multi-env --suite framework --env ubuntu-8.0 --no-network
  else
    echo "::warning::SECURITY_GATE_AIRGAPPED_CONTAINER=1 but Docker not available — skipping"
  fi
fi

echo ""
echo "security-gate-tier-e: PASS"
