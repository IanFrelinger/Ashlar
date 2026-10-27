#!/usr/bin/env bash
# WHY: a currency scan must reject each stale shape, not pass after scanning nothing.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PY_BIN=""
for candidate in python3 python py; do
  if "${candidate}" -c 'print(1)' >/dev/null 2>&1; then PY_BIN="${candidate}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then echo "FAIL: no working Python"; exit 1; fi
"${PY_BIN}" - "${ROOT}" <<'PY'
import json
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(sys.argv[1])
check = root / "scripts/ci/verify-mutation-manifests-current.py"
passed = failed = 0

def expect(label, code, fragment, result):
    global passed, failed
    if result.returncode == code and fragment in result.stdout:
        passed += 1
        print("ok - " + label)
    else:
        failed += 1
        print(f"FAIL - {label}: exit={result.returncode}, output={result.stdout}")

def run(path):
    return subprocess.run([sys.executable, str(check), "--root", str(path)],
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

with tempfile.TemporaryDirectory() as temp:
    tree = Path(temp)
    manifests = tree / "scripts/mutations"
    manifests.mkdir(parents=True)
    source = tree / "source.cs"
    source.write_text("unique original\n", encoding="utf-8")
    for index in range(7):
        cases = [{"id": f"case-{index}-{n}", "file": "source.cs", "old": "unique original",
                  "new": "mutated", "expected_failures": ["Named_test"]}
                 for n in range(41 if index < 5 else 40)]
        (manifests / f"spec007-{index}.json").write_text(json.dumps({"mutations": cases}), encoding="utf-8")
    expect("current fixture", 0, "7 manifest(s), 285 mutation(s), 0 problem(s)", run(tree))
    spec010 = manifests / "spec010-0.json"
    spec010.write_text(json.dumps({"mutations": [{"id": "case-s10", "file": "source.cs", "old": "gone",
                                                  "new": "mutated", "expected_failures": ["Named_test"]}]}),
                       encoding="utf-8")
    expect("spec010 manifest is scanned", 1, "spec010-0.json: case-s10 (source.cs): old occurs 0 times", run(tree))
    spec010.unlink()
    source.write_text("different\n", encoding="utf-8")
    expect("missing old names id and file", 1, "case-0-0 (source.cs): old occurs 0 times", run(tree))
    source.write_text("unique original unique original\n", encoding="utf-8")
    expect("duplicate old", 1, "case-0-0 (source.cs): old occurs 2 times", run(tree))
    source.unlink()
    expect("missing file", 1, "case-0-0 (source.cs): source file missing", run(tree))
    source.write_text("unique original\n", encoding="utf-8")
    first = manifests / "spec007-0.json"
    original = first.read_text(encoding="utf-8")
    data = json.loads(original)
    data["mutations"][0]["expected_failures"] = []
    first.write_text(json.dumps(data), encoding="utf-8")
    expect("empty expected failures", 1, "case-0-0 (source.cs): expected_failures must be a nonempty list", run(tree))
    first.write_text(original, encoding="utf-8")
    data = json.loads(original)
    data["mutations"].pop()
    first.write_text(json.dumps(data), encoding="utf-8")
    expect("mutation floor", 1, "found 284 mutations; expected at least 285", run(tree))
    first.unlink()
    expect("manifest floor", 1, "found 6 manifests; expected at least 7", run(tree))
    first.write_text("{", encoding="utf-8")
    expect("malformed manifest", 1, "spec007-0.json: invalid manifest", run(tree))

expect("current repository", 0, "0 problem(s)", run(root))
print(f"passed: {passed}   failed: {failed}")
assert passed + failed == 10, "partial fixture execution"
sys.exit(1 if failed else 0)
PY
