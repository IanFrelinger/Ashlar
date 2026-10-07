#!/usr/bin/env bash
# Composition + mesh Tier B: CLI pipeline + open mesh command surfaces.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLI="application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj"
TRX_DIR="test-results/composition-mesh-gate-tier-b"
rm -rf "$TRX_DIR"

echo "== Composition Tier B: open mesh CLI unit suites =="
dotnet build "$CLI" -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$CLI" -f net10.0 --no-build \
  --filter "FullyQualifiedName~UnitTestBridgeTests&(DisplayName~PipelineCommand|DisplayName~MeshCommand|DisplayName~OptimizeAgentCluster)" \
  --logger "trx;LogFileName=mesh-cli.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 180s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/mesh-cli.trx"

echo ""
echo "composition-mesh-gate-tier-b: PASS"
