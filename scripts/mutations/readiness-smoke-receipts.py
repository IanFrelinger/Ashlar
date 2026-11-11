#!/usr/bin/env python3
"""Prove readiness receipt regressions fail assertions, restore clean, and rerun green.

Usage: python3 scripts/mutations/readiness-smoke-receipts.py --out /tmp/receipt-proof
Runs real Python unittest assertions in an isolated clone of HEAD, not .NET suites.
Every mutation must match once and cause an expected assertion failure (not a test error).
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

WORKFLOW = ".github/workflows/full-platform-readiness-gate.yml"
CHECKER = "scripts/ci/verify-zero-test-guard-wiring.py"
RECEIPTS = ["smoke-net8.0", "smoke-net10.0", "smoke-whitebox-net8.0", "smoke-whitebox-net10.0"]
DISTINCT = "test_every_project_framework_has_a_distinct_nonempty_receipt"
EMPTY = "test_each_empty_selection_is_refused_despite_successful_test_exit"
UPLOAD = "test_workflow_keeps_receipts_on_failure_and_uses_bash"
CHECK = "test_readiness_smokes_are_required_by_the_repository_wiring_checker"


def cases():
    result = []
    for name in RECEIPTS:
        line = '          bash scripts/ci/zero-test-guard.sh "${{ runner.temp }}/test-results/' + name + '-${{ matrix.label }}.trx"\n'
        result.append(("drop-native-guard-" + name, WORKFLOW, line, "", EMPTY))
    for name in ("smoke-net10.0", "smoke-whitebox-net10.0"):
        line = "              bash scripts/ci/zero-test-guard.sh /results/test-results/" + name + ".trx\n"
        result.append(("drop-container-guard-" + name, WORKFLOW, line, "", EMPTY))
    pin = "          dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \\\n            -f net8.0 \\\n"
    result.append(("restore-multitarget-overwrite", WORKFLOW, pin, pin.replace("            -f net8.0 \\\n", ""), DISTINCT))
    receipt = ('            --logger "trx;LogFileName=smoke-net10.0-${{ matrix.label }}.trx" \\\n'
               '            --results-directory "${{ runner.temp }}/test-results"\n'
               '          bash scripts/ci/zero-test-guard.sh "${{ runner.temp }}/test-results/smoke-net10.0-${{ matrix.label }}.trx"')
    result.append(("reuse-native-receipt", WORKFLOW, receipt, receipt.replace("smoke-net10.0", "smoke-net8.0"), DISTINCT))
    native = ('        shell: bash\n        run: |\n'
              '          rm -f src/Ashlar.Tests.Infrastructure/scripts/obj/project.assets.json')
    result.append(("native-guard-failures-ignored", WORKFLOW, native, native.replace("run: |\n", "run: |\n          set +e\n"), EMPTY))
    nested = "            bash -c '\n              set -euo pipefail"
    result.append(("container-guard-failures-ignored", WORKFLOW, nested, nested.replace("set -euo", "set -uo"), EMPTY))
    for kind in ("readiness", "container readiness"):
        line = "      - name: Upload " + kind + " artifacts\n        if: always()"
        result.append(("lose-" + kind.replace(" ", "-") + "-failure-artifacts", WORKFLOW, line, line.replace("always()", "success()"), UPLOAD))
    entry = "EXEMPT_FILES = [\n"
    result.append(("restore-readiness-wiring-exemption", CHECKER, entry,
                   entry + '    ("' + WORKFLOW + '", "mutated exemption"),\n', CHECK))
    pin = '    ("' + WORKFLOW + '", "smoke-whitebox-net10.0.trx"),\n'
    result.append(("drop-readiness-wiring-floor", CHECKER, pin, "", CHECK))
    return result


DRIVER = '''
import importlib.util,json,unittest,sys
spec=importlib.util.spec_from_file_location('receipts','scripts/ci/test-readiness-smoke-receipts.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
r=unittest.TextTestRunner(verbosity=2,stream=sys.stdout).run(unittest.defaultTestLoader.loadTestsFromModule(module))
print('RESULT_JSON:'+json.dumps({'tests':r.testsRun,'failures':len(r.failures),'errors':len(r.errors),'skipped':len(r.skipped),'failed_names':[t.id() for t,_ in r.failures]}))
sys.exit(not r.wasSuccessful())
'''


def run_tests(clone, output, name):
    result = subprocess.run([sys.executable, "-B", "-c", DRIVER], cwd=clone,
                            capture_output=True, text=True, timeout=60,
                            env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
    log = result.stdout + result.stderr
    (output / (name + ".log")).write_text(log)
    lines = [line.removeprefix("RESULT_JSON:") for line in result.stdout.splitlines()
             if line.startswith("RESULT_JSON:")]
    assert len(lines) == 1, (name, "missing unittest result", log)
    counts = json.loads(lines[0])
    assert counts["tests"] == 9 and counts["errors"] == 0 and counts["skipped"] == 0, (name, counts)
    assert result.returncode == (1 if counts["failures"] else 0), (name, result.returncode, counts)
    return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    repo, output = args.repo.resolve(), args.out.resolve()
    output.mkdir(parents=True, exist_ok=True)
    ref = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
    records = []
    with tempfile.TemporaryDirectory(prefix="readiness-receipt-proof-") as temp:
        clone = Path(temp) / "repo"
        subprocess.run(["git", "clone", "--quiet", "--no-hardlinks", str(repo), str(clone)], check=True)
        subprocess.run(["git", "checkout", "--quiet", "--detach", ref], cwd=clone, check=True)
        assert run_tests(clone, output, "baseline")["failures"] == 0
        for name, path, old, new, expected in cases():
            target = clone / path
            original = target.read_bytes()
            source = original.decode()
            assert source.count(old) == 1, (name, "mutation must match exactly once", source.count(old))
            target.write_text(source.replace(old, new))
            assert target.read_bytes() != original, (name, "mutation did not apply")
            (output / (name + ".patch")).write_bytes(subprocess.check_output(["git", "diff", "--", path], cwd=clone))
            try:
                red = run_tests(clone, output, name + "-red")
            finally:
                subprocess.run(["git", "restore", "--source=" + ref, "--", path], cwd=clone, check=True)
            assert target.read_bytes() == original, (name, "byte restoration failed")
            assert subprocess.check_output(["git", "status", "--porcelain"], cwd=clone) == b"", (name, "dirty restoration")
            assert red["failures"] and any(expected in test for test in red["failed_names"]), (name, "expected assertion survived", red)
            green = run_tests(clone, output, name + "-green")
            assert green["failures"] == 0, (name, "green did not pass", green)
            record = {"id": name, "ref": ref, "file": path, "restored_sha256": hashlib.sha256(original).hexdigest(),
                      "red": red, "green": green}
            records.append(record)
            (output / "summary.json").write_text(json.dumps(records, indent=2) + "\n")
            print(f"shell mutation {name}: KILLED red_assertion_failures={red['failures']} "
                  f"red_tests={red['tests']} green=passed:{green['tests']}/{green['tests']} ref={ref}", flush=True)
    print(f"PASS: {len(records)} assertion-level shell/Python mutations; source checkout unchanged", flush=True)


if __name__ == "__main__":
    main()
