#!/usr/bin/env python3
"""Execution twins for failing/stuck gate diagnostics, discovered by shell-lint."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "scripts/ci/run-reported-gates.py"
spec = importlib.util.spec_from_file_location("reported_gates", RUNNER)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class ReportedGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.reports = self.root / "reports"
        self.groups = self.root / "fixture-process-groups"
        self.addCleanup(self.cleanup_fixture_processes)

    def cleanup_fixture_processes(self):
        # Gate fixtures run in sessions separate from the runner. An outer watchdog
        # killing a broken runner must not leave its deliberately stuck children alive.
        if os.name == "posix" and self.groups.exists():
            for group in set(self.groups.read_text().splitlines()):
                group = int(group)
                if group != os.getpgrp():
                    try:
                        os.killpg(group, signal.SIGKILL)
                    except ProcessLookupError:
                        pass

    def gate(self, name, source):
        path = self.root / (name + ".py")
        record = (f"import os\nif hasattr(os, 'getpgrp'):\n"
                  f" with open({str(self.groups)!r}, 'a') as groups:\n  groups.write(str(os.getpgrp())+'\\n')\n")
        path.write_text(record + source)
        return str(path)

    def command(self, *gates, timeout=5, budget=20):
        return [sys.executable, "-B", str(RUNNER), "--reports", str(self.reports),
                "--timeout", str(timeout), "--budget", str(budget), *gates]

    def run_gates(self, *gates, **kwargs):
        process = subprocess.run(self.command(*gates, **kwargs), capture_output=True, text=True, timeout=30)
        return process, json.loads((self.reports / "report.json").read_text())

    def test_failure_is_nonzero_retains_log_and_later_checks_execute(self):
        before = self.gate("before", "print('first check ran')")
        failed = self.gate("failure", "import sys\nprint('test_named_regression: assertion failed')\nsys.exit(7)")
        after = self.gate("after", "print('later check ran')")
        process, report = self.run_gates(before, failed, after)
        self.assertEqual(process.returncode, 1)
        self.assertEqual(report["status"], "failed")
        self.assertEqual([r["status"] for r in report["checks"]], ["passed", "failed", "passed"])
        row = report["checks"][1]
        self.assertEqual(row["exit_code"], 7)
        self.assertEqual(row["command"][-1], failed)
        self.assertIn("test_named_regression", (self.reports / row["log"]).read_text())
        self.assertIn("::error::", process.stdout)
        self.assertIn("**failed**", (self.reports / "summary.md").read_text())

    def test_success_is_explicit_and_preserves_test_output(self):
        process, report = self.run_gates(self.gate("green", "print('Ran 3 tests; OK')"))
        self.assertEqual(process.returncode, 0)
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["checks"][0]["exit_code"], 0)
        self.assertIn("Ran 3 tests; OK", process.stdout)
        self.assertIn("all 1 gate(s) passed", process.stdout)

    def test_stall_is_timed_out_with_last_test_and_later_check_runs(self):
        hung = self.gate("hung", "import time\nprint('test_waiting_for_worker started', flush=True)\ntime.sleep(5)")
        after = self.gate("after", "print('after stall')")
        process, report = self.run_gates(hung, after, timeout=0.2)
        self.assertEqual(process.returncode, 1)
        self.assertEqual([r["status"] for r in report["checks"]], ["timed_out", "passed"])
        row = report["checks"][0]
        self.assertEqual(row["exit_code"], 124)
        self.assertLess(row["elapsed_seconds"], 4)
        self.assertIn("test_waiting_for_worker", (self.reports / row["log"]).read_text())
        self.assertIn("timed_out", process.stdout)

    @unittest.skipUnless(os.name == "posix", "POSIX process-group cleanup runs on Linux CI")
    def test_timeout_kills_descendants_even_when_leader_exits_on_term(self):
        marker = self.root / "orphan-ran"
        child = self.gate("child", f"import signal,time\nfrom pathlib import Path\nsignal.signal(signal.SIGTERM, signal.SIG_IGN)\ntime.sleep(1.5)\nPath({str(marker)!r}).write_text('leaked')")
        parent = self.gate("parent", f"import subprocess,sys,time\nsubprocess.Popen([sys.executable, {child!r}])\ntime.sleep(5)")
        process, report = self.run_gates(parent, timeout=0.4)
        self.assertEqual(process.returncode, 1)
        self.assertEqual(report["checks"][0]["status"], "timed_out")
        time.sleep(1.6)
        self.assertFalse(marker.exists(), "a stuck test descendant escaped cleanup")

    def test_budget_exhaustion_records_unexecuted_checks_not_passes(self):
        hung = self.gate("hung", "import time\ntime.sleep(5)")
        after = self.gate("after", "raise AssertionError('must not execute after budget')")
        process, report = self.run_gates(hung, after, timeout=5, budget=0.2)
        self.assertEqual(process.returncode, 1)
        self.assertEqual([r["status"] for r in report["checks"]], ["timed_out", "not_run"])
        self.assertIn("budget exhausted", report["checks"][1]["reason"])
        self.assertNotIn("exit_code", report["checks"][1])

    @unittest.skipUnless(os.name == "posix", "POSIX descendant cleanup runs on Linux CI")
    def test_exited_parent_cannot_leave_a_logging_descendant_past_deadline(self):
        marker = self.root / "escaped-writer"
        child = self.gate("writer", f"import os,time\nfrom pathlib import Path\nend=time.monotonic()+2\nwhile time.monotonic()<end:\n os.write(1, b'x'*65536)\nPath({str(marker)!r}).write_text('escaped')")
        parent = self.gate("exited", f"import subprocess,sys,time\nsubprocess.Popen([sys.executable, {child!r}])\ntime.sleep(.03)")
        started = time.monotonic()
        process, report = self.run_gates(parent, timeout=0.15, budget=0.2)
        self.assertLess(time.monotonic() - started, 1.5)
        self.assertIn(process.returncode, (0, 1))
        self.assertIn(report["checks"][0]["status"], ("passed", "timed_out"))
        time.sleep(2.1)
        self.assertFalse(marker.exists(), "the background log writer outlived its parent")

    @unittest.skipUnless(os.name == "posix", "TERM cancellation runs on Linux CI")
    def test_interruption_preserves_running_receipt_and_partial_log(self):
        gate = self.gate("interrupted", "import time\nprint('test_in_progress', flush=True)\ntime.sleep(10)")
        process = subprocess.Popen(self.command(gate), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.addCleanup(lambda: process.poll() is None and process.kill())
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            receipt = self.reports / "report.json"
            if receipt.exists():
                report = json.loads(receipt.read_text())
                row = report["checks"][0]
                log = self.reports / row["log"]
                if row["status"] == "running" and log.exists() and "test_in_progress" in log.read_text():
                    break
            time.sleep(0.01)
        else:
            self.fail("running receipt and partial output were not persisted")
        self.assertEqual(report["status"], "incomplete")
        process.send_signal(signal.SIGTERM)
        self.assertEqual(process.wait(timeout=5), 130)
        final = json.loads(receipt.read_text())
        self.assertEqual(final["status"], "failed")
        self.assertEqual(final["checks"][0]["status"], "interrupted")

    def test_launch_failure_is_actionable_and_not_a_pass(self):
        self.reports.mkdir()
        row = {"gate": "missing", "command": ["missing"], "log": "missing.log"}
        with patch.object(runner.subprocess, "Popen", side_effect=FileNotFoundError("fixture missing executable")):
            self.assertEqual(runner.run_one(row, self.reports, 1), 127)
        self.assertEqual(row["status"], "error")
        self.assertIn("fixture missing executable", row["reason"])

    def test_invalid_deadlines_are_rejected(self):
        for value in ("0", "-1", "nan", "inf"):
            with self.subTest(value=value):
                result = subprocess.run(self.command("unused.py", timeout=value), capture_output=True, timeout=5)
                self.assertEqual(result.returncode, 2)

    def test_required_workflow_runs_wrapper_and_retains_diagnostics_on_failure(self):
        import yaml
        workflow = yaml.safe_load((ROOT / ".github/workflows/shell-lint.yml").read_text())
        steps = workflow["jobs"]["shell-lint"]["steps"]
        gates = next(step for step in steps if step.get("name") == "Repository gates")
        self.assertIn("bash scripts/ci/run-repo-gates.sh", gates["run"])
        self.assertIn("ASHLAR_GATE_REPORTS", gates["env"])
        summary = next(step for step in steps if step.get("name") == "Publish repository gate failures and stalls")
        self.assertEqual(summary["if"], "always()")
        upload = next(step for step in steps if step.get("uses", "").startswith("actions/upload-artifact@"))
        self.assertEqual(upload["if"], "always()")
        self.assertEqual(upload["with"]["if-no-files-found"], "error")
        shell = (ROOT / "scripts/ci/run-repo-gates.sh").read_text()
        self.assertIn('exec "${PY_BIN}" scripts/ci/run-reported-gates.py', shell)
        self.assertIn('"${GATES[@]}"', shell)


if __name__ == "__main__":
    unittest.main(verbosity=2)
