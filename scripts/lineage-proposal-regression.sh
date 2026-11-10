#!/usr/bin/env bash
# Run only through scripts/test-in-container.sh; assert executed cases and abrupt process recovery.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
RESULTS="$(mktemp -d)"
trap 'rm -rf "$RESULTS"' EXIT
for framework in net8.0 net10.0; do
  dotnet test src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj \
    --framework "$framework" \
    --filter 'FullyQualifiedName~ChangeProposalStoreTests|FullyQualifiedName~ChangeProposalDurabilityTests|FullyQualifiedName~SelfExtendInvariantDRecursionCeilingTests' \
    --blame-hang-timeout 120s --blame-hang-dump-type none \
    --logger "trx;LogFileName=focused-${framework}.trx" --results-directory "$RESULTS" \
    --nologo -v minimal
  bash scripts/ci/zero-test-guard.sh "$RESULTS/focused-${framework}.trx"
  python3 scripts/proposal-store-crash-probe.py --framework "$framework"
done
python3 - "$RESULTS" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
for path in Path(sys.argv[1]).glob('focused-*.trx'):
    root = ET.parse(path).getroot()
    results = root.findall('./{*}Results/{*}UnitTestResult')
    assert len(results) == 71, (path, len(results))
    assert all(r.get('outcome') == 'Passed' for r in results), path
    for suite in ('ChangeProposalStoreTests', 'ChangeProposalDurabilityTests', 'SelfExtendInvariantDRecursionCeilingTests'):
        assert any(suite in r.get('testName', '') for r in results), (path, suite)
    print(f'PASS {path.name}: {len(results)} executed cases, no skips')
PY
