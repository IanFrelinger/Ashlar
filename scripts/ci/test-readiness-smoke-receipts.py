#!/usr/bin/env python3
"""Execute readiness smoke workflow shells with fake dotnet/Docker and the real TRX guard.

These are receipt/wiring tests, not evidence that .NET suites or platform containers passed.
The fake test runner models VSTest's successful exit on an empty selection and overwriting
LogFileName when a multi-target invocation does not pin its target framework.
"""
import fnmatch
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ".github/workflows/full-platform-readiness-gate.yml"
INFRA = "src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
BACKGROUND = "src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj"
EXPECTED = {
    "native-platform": [(INFRA, "net8.0"), (INFRA, "net10.0"),
                        (BACKGROUND, "net8.0"), (BACKGROUND, "net10.0")],
    "container-platform": [(INFRA, "net10.0"), (BACKGROUND, "net10.0")],
}


class ReadinessSmokeReceiptsTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="readiness receipts ")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.bin = self.directory / "bin"
        self.bin.mkdir()
        self.calls = self.directory / "calls.jsonl"
        self.env = {**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
                    "FIXTURE_CALLS": str(self.calls), "FIXTURE_MODE": "success",
                    "FIXTURE_TARGET": "", "FIXTURE_RESULTS": str(self.directory / "container-results")}
        self.workflow = yaml.safe_load((ROOT / WORKFLOW).read_text())
        scripts = self.directory / "scripts/ci"
        scripts.mkdir(parents=True)
        shutil.copyfile(ROOT / "scripts/ci/zero-test-guard.sh", scripts / "zero-test-guard.sh")
        self.stub("dotnet", '''
import json,os,sys
from pathlib import Path
args=sys.argv[1:]
if args[0]!='test':sys.exit(0)
def value(flag):return args[args.index(flag)+1]
project=args[1]
framework=value('-f') if '-f' in args else (value('--framework') if '--framework' in args else None)
name=next(arg.split('LogFileName=',1)[1] for arg in args if 'LogFileName=' in arg)
with open(os.environ['FIXTURE_CALLS'],'a') as f:
 f.write(json.dumps({'project':project,'framework':framework,'name':name,'args':args})+'\\n')
for tfm in ([framework] if framework else ['net8.0','net10.0']):
 target=project+'|'+tfm
 mode=os.environ['FIXTURE_MODE'] if os.environ['FIXTURE_TARGET']==target else 'success'
 if mode=='failure':sys.exit(23)
 if mode=='missing':continue
 p=Path(value('--results-directory'))/name
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():print('WARNING: Overwriting results file:',p)
 if mode=='unreadable':p.write_text('not a test receipt');continue
 executed=0 if mode in ('empty','skipped') else 2
 total=0 if mode=='empty' else 2
 p.write_text(f'<TestRun><ResultSummary><Counters total="{total}" executed="{executed}" /></ResultSummary></TestRun>')
''')
        # Execute the real nested shell, mapping only its mounted /results path. No Docker required.
        self.stub("docker", '''
import os,shlex,subprocess,sys
args=sys.argv[1:]
assert args[0]=='run' and args[-3:-1]==['bash','-c'],args
script=args[-1].replace('/results',shlex.quote(os.environ['FIXTURE_RESULTS']))
sys.exit(subprocess.run(['bash','-c',script]).returncode)
''')

    def stub(self, name, code):
        target = self.bin / name
        target.write_text(f"#!{sys.executable}\n" + code + "\n")
        target.chmod(0o755)

    def step(self, lane):
        name = ("Dry-run — build + run smoke tests" if lane == "native-platform"
                else "Discover + Dry-run in container")
        return next(step for step in self.workflow["jobs"][lane]["steps"] if step.get("name") == name)

    def run_lane(self, lane, target=None, mode="success"):
        self.calls.unlink(missing_ok=True)
        for path in (self.directory / "test-results", self.directory / "container-results"):
            if path.exists():
                shutil.rmtree(path)
        self.env["FIXTURE_TARGET"] = "|".join(target) if target else ""
        self.env["FIXTURE_MODE"] = mode
        run = self.step(lane)["run"].replace("${{ runner.temp }}", str(self.directory))
        run = run.replace("${{ matrix.label }}", "Fixture-Lane")
        result = subprocess.run(["bash", "-e", "-o", "pipefail", "-c", run], cwd=self.directory,
                                env=self.env, capture_output=True, text=True, timeout=10)
        calls = [json.loads(line) for line in self.calls.read_text().splitlines()] if self.calls.exists() else []
        return result, calls

    def test_every_project_framework_has_a_distinct_nonempty_receipt(self):
        for lane, expected in EXPECTED.items():
            with self.subTest(lane=lane):
                result, calls = self.run_lane(lane)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual([(c["project"], c["framework"]) for c in calls], expected)
                self.assertEqual(len({c["name"] for c in calls}), len(expected))
                receipts = list(self.directory.rglob("*.trx"))
                self.assertEqual({p.name for p in receipts}, {c["name"] for c in calls})
                self.assertEqual(result.stdout.count("zero-test-guard: ok "), len(expected))
                self.assertNotIn("Overwriting", result.stdout)
                for call in calls:
                    self.assertIn(call["framework"], call["name"])
                    self.assertIn("--no-build", call["args"])
                    self.assertIn("--blame-hang-timeout", call["args"])
                    self.assertEqual(call["args"][call["args"].index("--blame-hang-timeout") + 1],
                                     "450s" if call["project"] == INFRA else "60s")
                    self.assertEqual(call["args"][call["args"].index("--filter") + 1],
                                     "(FullyQualifiedName~BaseFrameworkSmokeTests|FullyQualifiedName~RuntimeStudioBlackBoxSmokeTests)"
                                     if call["project"] == INFRA else "FullyQualifiedName~RuntimeStudioWhiteBoxSmokeTests")
                for receipt in receipts:
                    self.assertEqual(ET.parse(receipt).find(".//Counters").attrib["executed"], "2")

    def assert_refuses(self, mode, reason):
        for lane, expected in EXPECTED.items():
            for index, target in enumerate(expected):
                with self.subTest(lane=lane, target=target, mode=mode):
                    result, calls = self.run_lane(lane, target, mode)
                    self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                    self.assertIn("zero-test-guard: FAIL ", result.stdout)
                    self.assertIn(reason, result.stdout)
                    self.assertEqual(len(calls), index + 1, "no later smoke may hide a failed receipt")

    def test_each_empty_selection_is_refused_despite_successful_test_exit(self):
        self.assert_refuses("empty", "the filter selected no test")

    def test_each_all_skipped_selection_is_refused(self):
        self.assert_refuses("skipped", "selected test(s) were skipped")

    def test_each_missing_receipt_is_refused(self):
        self.assert_refuses("missing", "no TRX was written")

    def test_each_unreadable_receipt_is_refused(self):
        self.assert_refuses("unreadable", "cannot read total/executed")

    def test_test_failures_keep_their_exit_status(self):
        for lane, expected in EXPECTED.items():
            for index, target in enumerate(expected):
                with self.subTest(lane=lane, target=target):
                    result, calls = self.run_lane(lane, target, "failure")
                    self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
                    self.assertEqual(len(calls), index + 1)

    def test_workflow_keeps_receipts_on_failure_and_uses_bash(self):
        for lane in EXPECTED:
            job = self.workflow["jobs"][lane]
            smoke = self.step(lane)
            self.assertFalse(job.get("continue-on-error", False))
            self.assertFalse(smoke.get("continue-on-error", False))
            self.assertFalse(job["strategy"]["fail-fast"])
            if lane == "native-platform":
                self.assertEqual(smoke["shell"], "bash")
            else:
                self.assertIn("set -euo pipefail", smoke["run"])
            uploads = [s for s in job["steps"] if s.get("uses", "").startswith("actions/upload-artifact@")]
            expected_path = "test-results/" if lane == "native-platform" else "container-results/"
            self.assertEqual(len(uploads), 1)
            self.assertEqual(uploads[0]["if"], "always()")
            self.assertIn(expected_path, uploads[0]["with"]["path"])

    def test_receipt_guard_changes_trigger_readiness(self):
        source = (ROOT / WORKFLOW).read_text()
        paths = self.workflow.get("on", self.workflow.get(True))["push"]["paths"]
        for path in ("scripts/ci/zero-test-guard.sh", "scripts/ci/test-readiness-smoke-receipts.py"):
            self.assertIn(path, paths)
            self.assertIn('            "' + path + '"', source)

    def test_readiness_smokes_are_required_by_the_repository_wiring_checker(self):
        spec = importlib.util.spec_from_file_location("wiring", ROOT / "scripts/ci/verify-zero-test-guard-wiring.py")
        checker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(checker)
        self.assertFalse(any(fnmatch.fnmatch(WORKFLOW, pattern) for pattern, _ in checker.EXEMPT_FILES))
        for name in ("smoke-net8.0-${{ matrix.label }}.trx", "smoke-whitebox-net10.0.trx"):
            self.assertIn((WORKFLOW, name), checker.REQUIRED_WIRED)


if __name__ == "__main__":
    unittest.main(verbosity=2)
