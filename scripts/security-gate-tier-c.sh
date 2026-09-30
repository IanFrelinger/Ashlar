#!/usr/bin/env bash
# Security Tier C: trust CLI surfaces (boundary + dashboard + audit JSON).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLI="application/src/Ashlar.CLI/Ashlar.CLI.csproj"
CLI_TESTS="application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj"
TRX_DIR="test-results/security-gate-tier-c"
rm -rf "$TRX_DIR"

echo "== Security Tier C: package-admission and untrusted-output suites =="
dotnet build "$CLI_TESTS" -v minimal
# A filter here once named TrustCommandTests on its own. That is a `UnitTestBase` suite and not an
# xUnit one, so VSTest matched ZERO tests and exited 0: the lane asserted nothing. The only runner
# that reaches it is the UnitTestBridgeTests theory, and ONE row of that theory
# (testType: AgentCommandTests) never completes — so the whole theory cannot be named here. A single
# row of it can be, and is, by narrowing on DisplayName, the pattern
# scripts/composition-mesh-gate-tier-b.sh already uses. Measured in the dev container: the
# TrustCommandTests row alone runs 1 test in 453 ms, and the whole filter below is 61 tests in ~4s
# wall clock — it terminates, so the trust CLI suite has an automatic runner rather than a comment
# saying it cannot have one.
#
# The rest of the filter is where the CLI's attacker-reachable surface lives: SafePackageRead (the
# bounded, non-blocking .ashpkg read — a planted FIFO, device link or oversized file must be a
# refusal, never a hang or an OutOfMemoryException, and the mesh serve path goes through the same
# primitive), PkgCommand (a refused row must not deny the packages behind it, --from must not be
# coerced into a mangled path, and a refusal must not be able to quote sender-chosen text raw), and
# UntrustedText (a sender-chosen filename, symlink target or FORMAT VERSION must not be able to
# repaint a row into a counterfeit admission). The trust boundary/dashboard smoke below still
# exercises the trust CLI end to end on top of that.
#
# This step runs on every pull_request: security-gate.yml's Tier C `if:` is the `!=` form, and the
# workflow's `paths:` select the CLI directories these suites cover.
ASHLAR_ALLOW_MOCK=1 dotnet test "$CLI_TESTS" -f net10.0 --no-build \
  --filter "FullyQualifiedName~SafePackageReadTests|FullyQualifiedName~PkgCommandTests|FullyQualifiedName~UntrustedTextTests|FullyQualifiedName~MeshLanPartyTests|(FullyQualifiedName~UnitTestBridgeTests&DisplayName~TrustCommandTests)" \
  --logger "trx;LogFileName=trust-cli.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/trust-cli.trx"

echo "== Security Tier C: trust boundary + dashboard JSON smoke =="
dotnet build "$CLI" -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- trust boundary --format-json >/dev/null
ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- trust dashboard --format-json >/dev/null

echo ""
echo "security-gate-tier-c: PASS"
