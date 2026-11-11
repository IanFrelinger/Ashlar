#!/usr/bin/env bash
# Run through scripts/test-in-container.sh, never on the host.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
RESULTS="$(mktemp -d)"
trap 'rm -rf "$RESULTS"' EXIT
for framework in net8.0 net10.0; do
  dotnet test src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj \
    --framework "$framework" \
    --filter 'FullyQualifiedName~ManifestContractTests|FullyQualifiedName~AdmissionGateTests|FullyQualifiedName~GateStoreRaceTests|FullyQualifiedName~ProjectScaffoldTests|FullyQualifiedName~ProjectVerifierTests' \
    --blame-hang-timeout 120s --blame-hang-dump-type none \
    --logger "trx;LogFileName=budget-${framework}.trx" --results-directory "$RESULTS" --nologo -v minimal
  bash scripts/ci/zero-test-guard.sh "$RESULTS/budget-${framework}.trx"
done
dotnet test application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj --framework net10.0 \
  --filter 'FullyQualifiedName~PolicyCommandTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=budget-cli.trx' --results-directory "$RESULTS" --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/budget-cli.trx"
python3 - "$RESULTS" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
for path in Path(sys.argv[1]).glob('budget-*.trx'):
    results = ET.parse(path).getroot().findall('./{*}Results/{*}UnitTestResult')
    assert results and all(r.get('outcome') == 'Passed' for r in results), path
    expected = (('Invalid_budget_is_rejected_by_show_set_and_verify_without_writes', 9),) if path.stem == 'budget-cli' else (
        ('Invalid_budgets_are_rejected_in_every_mode', 54),
        ('Invalid_or_overflowing_windows_return_false_and_zero', 15),
        ('Largest_supported_windows_parse_exactly', 3),
        ('Invalid_programmatic_budgets_never_admit', 4),
        ('Windows_before_year_one_count_all_admissions_without_clock_underflow', 6),
    )
    for test, count in expected:
        cases = [r for r in results if test in r.get('testName', '')]
        assert len(cases) == count, (path, test, len(cases), count)
    print(f'PASS {path.name}: {len(results)} executed cases, no skips; budget cases present')
PY
