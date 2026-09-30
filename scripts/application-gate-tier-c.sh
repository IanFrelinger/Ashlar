#!/usr/bin/env bash
# Application Tier C: in-process Ashlar.API (WebApplicationFactory) HTTP contract tests.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

INFRA="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
TRX_DIR="test-results/application-gate-tier-c"
rm -rf "$TRX_DIR"

echo "== Application Tier C: in-process API (DI + HTTP demos) =="
# net10.0, not net8.0: both classes link Ashlar.API, which ships on net10.0 only, and the csproj
# compiles Tests/API and Tests/VirtualProduction out of the net8.0 leg. At -f net8.0 this lane
# selected ZERO tests and printed PASS - FrameworkVirtualProdDemosTests from the day it was written
# (VirtualProduction was already net10.0-only), ApiDevelopmentHostDiTests since e6b5579f
# (2026-08-17). Measured in the dev container at 876cc527: net8.0 selects 0, net10.0 selects 4.
# 720s because ApiDevelopmentHostDiTests carries Timeout = TestTimeouts.HostTouching (480s).
dotnet build "$INFRA" -f net10.0 -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA" -f net10.0 --no-build \
  --filter "FullyQualifiedName~ApiDevelopmentHostDiTests|FullyQualifiedName~FrameworkVirtualProdDemosTests" \
  --logger "trx;LogFileName=in-process-api.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 720s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/in-process-api.trx"

echo ""
echo "application-gate-tier-c: PASS"
