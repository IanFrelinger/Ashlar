#!/usr/bin/env bash
# Run inside scripts/test-in-container.sh. These suites are not all selected by the general lanes.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
RESULTS="$(mktemp -d)"
trap 'rm -rf "$RESULTS"' EXIT

for framework in net8.0 net10.0; do
  dotnet test src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj \
    --framework "$framework" \
    --filter 'FullyQualifiedName~SanitizationAuditLogContractTests' \
    --blame-hang-timeout 120s --blame-hang-dump-type none \
    --logger "trx;LogFileName=audit-${framework}.trx" --results-directory "$RESULTS" \
    --nologo -v minimal
  bash scripts/ci/zero-test-guard.sh "$RESULTS/audit-${framework}.trx"
done

dotnet test samples/hello-brick/HelloBrick.Tests/HelloBrick.Tests.csproj \
  --framework net8.0 \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=hello-net8.0.trx' --results-directory "$RESULTS" \
  --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/hello-net8.0.trx"

# Select the actual xUnit bridge row, not the non-discoverable UnitTestBase type alone.
ASHLAR_ALLOW_MOCK=1 dotnet test application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj \
  --framework net10.0 \
  --filter 'FullyQualifiedName~UnitTestBridgeTests&DisplayName~RuntimeCommandTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=runtime-command-net10.0.trx' --results-directory "$RESULTS" \
  --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/runtime-command-net10.0.trx"

# The lane must execute every added case, not merely one test matching each filter.
python3 - "$RESULTS" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

results = Path(sys.argv[1])
expected = {"audit-net8.0.trx": 9, "audit-net10.0.trx": 9,
            "hello-net8.0.trx": 3, "runtime-command-net10.0.trx": 1}
for name, count in expected.items():
    counters = ET.parse(results / name).find(".//{*}Counters")
    assert counters is not None, name
    observed = {key: int(counters.get(key, "-1")) for key in ("total", "executed", "passed", "failed")}
    assert observed == {"total": count, "executed": count, "passed": count, "failed": 0}, (name, observed)
    print(f"PASS {name}: {count}/{count} executed and passed; no skipped cases")
PY
