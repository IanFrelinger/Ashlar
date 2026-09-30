#!/usr/bin/env bash
# Security Tier B: API auth + mesh security middleware + open-internet readiness.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

INFRA="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
TRX_DIR="test-results/security-gate-tier-b"
rm -rf "$TRX_DIR"

echo "== Security Tier B: API security middleware =="
# This lane selected ZERO tests and printed PASS from 2026-08-17 until this change. It ran -f net8.0,
# and e6b5579f made Tests/API compile on net10.0 only (Ashlar.API ships on net10.0 alone), so the four
# API classes below stopped existing in the assembly it tested. Measured in the dev container at
# 876cc527: net8.0 selects 0, net10.0 selects 44 (16 + 5 + 18 + 5).
dotnet build "$INFRA" -f net10.0 -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA" -f net10.0 --no-build \
  --filter "FullyQualifiedName~AshlarApiKeyAuthMiddlewareTests|FullyQualifiedName~MeshSecurityMiddlewareTests|FullyQualifiedName~AshlarApiOpenInternetReadinessTests|FullyQualifiedName~SecurityAdvisoryEndpointTests" \
  --logger "trx;LogFileName=api-security.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 180s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/api-security.trx"

echo "== Security Tier B: security analysis rule (UnitTestBase suite) =="
# The filter above used to end in |FullyQualifiedName~SecurityAnalysisRuleTests, which was never
# selectable: SecurityAnalysisRuleTests is a `UnitTestBase` suite, not an xUnit class, so VSTest has no
# test by that name on either framework. The only runner that reaches it is the UnitTestBridgeTests
# theory, one row per suite, selected by DisplayName - the pattern security-gate-tier-c.sh uses.
#
# It is a SEPARATE invocation with its own TRX on purpose. Folded into the filter above, this one row
# exists on net8.0 too, so reverting the framework left the lane at 1 executed test and the guard
# green while all 44 API tests vanished again - measured by mutation. A zero guard can only protect a
# selection it sees on its own.
#
# 720s because the bridge row carries Timeout = TestTimeouts.HostTouching (480s), and a window under
# 1.5x the widest per-test deadline turns a stall into a killed host that names nothing
# (docs/Testing.md).
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA" -f net10.0 --no-build \
  --filter "FullyQualifiedName~UnitTestBridgeTests&DisplayName~SecurityAnalysisRuleTests" \
  --logger "trx;LogFileName=security-analysis-rule.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 720s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/security-analysis-rule.trx"

echo ""
echo "security-gate-tier-b: PASS"
