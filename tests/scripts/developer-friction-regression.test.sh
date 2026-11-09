#!/usr/bin/env bash
# Hermetic wiring checks with a fake test process. These are not .NET test evidence.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin"
cat > "$WORK/bin/dotnet" <<'PY'
#!/usr/bin/env python3
import os
from pathlib import Path
import sys
args = sys.argv[1:]
project = args[1]
framework = args[args.index('--framework') + 1]
name = args[args.index('--logger') + 1].split('=', 1)[1]
folder = Path(args[args.index('--results-directory') + 1])
if 'BackgroundAgents' in project:
    assert framework in ('net8.0', 'net10.0')
    assert args[args.index('--filter') + 1] == 'FullyQualifiedName~SanitizationAuditLogContractTests'
    count = 9
elif 'HelloBrick' in project:
    assert framework == 'net8.0'
    count = 3
else:
    assert framework == 'net10.0'
    assert args[args.index('--filter') + 1] == 'FullyQualifiedName~UnitTestBridgeTests&DisplayName~RuntimeCommandTests'
    count = 1
mode = os.environ.get('FAKE_TEST_MODE', 'green')
executed = 0 if mode == 'zero' else count - 1 if mode == 'skipped' and count > 1 else count
folder.mkdir(parents=True, exist_ok=True)
(folder / name).write_text(f'<TestRun><ResultSummary><Counters total="{count}" executed="{executed}" passed="{executed}" failed="0" /></ResultSummary></TestRun>')
PY
chmod +x "$WORK/bin/dotnet"
PATH="$WORK/bin:$PATH" bash "$ROOT/scripts/developer-friction-regression.sh" > "$WORK/green.log"
[[ "$(grep -c '^PASS .*executed and passed' "$WORK/green.log")" -eq 4 ]]
echo 'PASS focused harness selects four expected invocations (stubbed .NET)'
for mode in zero skipped; do
  if PATH="$WORK/bin:$PATH" FAKE_TEST_MODE="$mode" bash "$ROOT/scripts/developer-friction-regression.sh" > "$WORK/$mode.log" 2>&1; then
    echo "FAIL focused harness accepted $mode cases" >&2
    exit 1
  fi
  if [[ "$mode" == zero ]]; then
    grep -q 'zero-test-guard: FAIL' "$WORK/$mode.log"
  else
    grep -q 'AssertionError' "$WORK/$mode.log"
  fi
  echo "PASS focused harness rejects $mode cases"
done
python3 - "$ROOT" <<'PY'
import json
from pathlib import Path
import sys
root = Path(sys.argv[1])
cases = json.loads((root / 'scripts/mutations/developer-friction.json').read_text())['mutations']
assert len(cases) == 4 and len({c['id'] for c in cases}) == 4
for case in cases:
    assert (root / case['file']).read_text().count(case['old']) == 1, case['id']
    assert case['old'] != case['new'] and case['expected_failures'], case['id']
    assert (root / case['project']).is_file(), case['id']
print('PASS four mutation targets are current; runtime kills are not claimed by this source check')
PY
