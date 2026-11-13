#!/usr/bin/env bash
# Run through scripts/test-in-container.sh; CLI supports net10.0 only.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "$ROOT" != /repo || ! -e /.dockerenv || ! -d /src-mirror/.git ]]; then
  echo 'Run through scripts/test-in-container.sh in its Linux devtest clone.' >&2
  exit 2
fi
cd "$ROOT"
RESULTS="$(mktemp -d)"
trap 'rm -rf "$RESULTS"' EXIT
# Literal project/framework/TRX pairs let the repository wiring guard prove that
# each pinned test invocation has its own fail-closed zero-test guard.
dotnet test src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj \
  --framework net8.0 \
  --filter 'FullyQualifiedName~GateDecisionIdentityTests|FullyQualifiedName~GateDecisionNormalizationProcessTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=kernel-net8.0.trx' --results-directory "$RESULTS" \
  --logger 'console;verbosity=normal' --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/kernel-net8.0.trx"

dotnet test src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj \
  --framework net10.0 \
  --filter 'FullyQualifiedName~GateDecisionIdentityTests|FullyQualifiedName~GateDecisionNormalizationProcessTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=kernel-net10.0.trx' --results-directory "$RESULTS" \
  --logger 'console;verbosity=normal' --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/kernel-net10.0.trx"

dotnet test application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj \
  --framework net10.0 \
  --filter 'FullyQualifiedName~GateDecisionDisplaySecurityTests|FullyQualifiedName~KeysGateDisplaySecurityTests|FullyQualifiedName~ReportBackgroundAgentCommandTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=cli-net10.0.trx' --results-directory "$RESULTS" \
  --logger 'console;verbosity=normal' --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/cli-net10.0.trx"

# dotnet test returns success for zero matches and skips. Require every selected
# class to execute and every result to pass, without freezing a case-count baseline.
python3 - "$RESULTS" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

root = Path(sys.argv[1])
selections = {
    'kernel-net8.0.trx': ('GateDecisionIdentityTests', 'GateDecisionNormalizationProcessTests'),
    'kernel-net10.0.trx': ('GateDecisionIdentityTests', 'GateDecisionNormalizationProcessTests'),
    'cli-net10.0.trx': (
        'GateDecisionDisplaySecurityTests',
        'KeysGateDisplaySecurityTests',
        'ReportBackgroundAgentCommandTests',
    ),
}
for name, classes in selections.items():
    document = ET.parse(root / name).getroot()
    rows = document.findall('./{*}Results/{*}UnitTestResult')
    counters = document.findall('./{*}ResultSummary/{*}Counters')
    if len(counters) != 1:
        raise SystemExit(f'{name}: expected one TRX counter record')
    counts = {key: int(counters[0].get(key, '-1')) for key in ('total', 'executed', 'passed', 'failed')}
    if not rows or any(row.get('outcome') != 'Passed' for row in rows):
        raise SystemExit(f'{name}: zero tests, failure, or skipped result')
    if any(counts[key] != len(rows) for key in ('total', 'executed', 'passed')) or counts['failed'] != 0:
        raise SystemExit(f'{name}: inconsistent TRX rows/counters: {counts}')
    for class_name in classes:
        selected = [row for row in rows if f'.{class_name}.' in row.get('testName', '')]
        if not selected:
            raise SystemExit(f'{name}: no executed cases from {class_name}')
        print(f'PASS {name}: {class_name} executed={len(selected)}; all passed, no skips')
    print(f'PASS {name}: total={len(rows)}; all passed, no skips')
PY
