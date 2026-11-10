#!/usr/bin/env bash
# Run through scripts/test-in-container.sh. Child probes inherit each actual target runtime.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
RESULTS="$(mktemp -d)"
trap 'rm -rf "$RESULTS"' EXIT
for framework in net8.0 net10.0; do
  dotnet test src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj \
    --framework "$framework" --blame-hang-timeout 120s --blame-hang-dump-type none \
    --logger "trx;LogFileName=kernel-${framework}.trx" --results-directory "$RESULTS" \
    --nologo -v minimal
  bash scripts/ci/zero-test-guard.sh "$RESULTS/kernel-${framework}.trx"
done
python3 - "$RESULTS" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
for path in Path(sys.argv[1]).glob('kernel-*.trx'):
    results = ET.parse(path).getroot().findall('./{*}Results/{*}UnitTestResult')
    assert results and all(r.get('outcome') == 'Passed' for r in results), path
    for suite, count in (('YamlDepthGuardTests', 13), ('YamlDepthProcessTests', 6), ('FileLockSafetyTests', 14)):
        cases = [r for r in results if suite in r.get('testName', '')]
        assert len(cases) == count, (path, suite, len(cases), count)
    print(f'PASS {path.name}: {len(results)} executed cases, no skips; all input-safety cases present')
PY
