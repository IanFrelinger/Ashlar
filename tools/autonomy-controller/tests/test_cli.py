import hashlib
import json
import os
import sqlite3
import subprocess
import sys
import unittest
from pathlib import Path

from .helpers import ControllerCase, PACKAGE_ROOT

CLI = PACKAGE_ROOT / "controller.py"


class CommandLine(ControllerCase):
    def cli(self, *args, state=None, policy=None, expect=0):
        cmd = [sys.executable, str(CLI), "--state", state or self.state_path, "--policy", policy or self.policy_path,
               "--holder", "cli-test", "--json", *args]
        env = dict(os.environ, PYTHONWARNINGS="ignore")
        p = subprocess.run(cmd, capture_output=True, text=True, env=env, timeout=120)
        self.assertEqual(p.returncode, expect, f"{' '.join(args)}\nstdout: {p.stdout}\nstderr: {p.stderr}")
        return json.loads(p.stdout) if p.stdout.strip().startswith(("{", "[")) else p.stdout, p.stderr

    def digest(self):
        h = hashlib.sha256(Path(self.state_path).read_bytes())
        conn = sqlite3.connect(f"file:{self.state_path}?mode=ro", uri=True)
        for line in conn.iterdump():
            h.update(line.encode("utf-8"))
        conn.close()
        return h.hexdigest()

    def test_missing_state_exits_3_and_creates_nothing(self):
        out, err = self.cli("tick", expect=3)
        self.assertIn("missing", err)
        self.assertFalse(Path(self.state_path).exists())

    def test_in_checkout_policy_exits_4(self):
        self.cli("init")
        out, err = self.cli("status", policy=str(PACKAGE_ROOT / "policy.example.json"), expect=4)
        self.assertIn("inside a checkout", err)

    def test_init_status_pause_resume_and_dry_run(self):
        out, _ = self.cli("init")
        self.assertIn("controller_id", out)
        out, _ = self.cli("task", "add", "--task-id", "t1", "--card", "755", "--workstream", "budgeted-autonomy",
                          "--provider", "fake", "--title", "PR 1")
        self.assertTrue(out["created"])
        before = self.digest()
        out, _ = self.cli("tick", "--dry-run")
        self.assertEqual(out["action"], "would-launch")
        self.assertEqual(self.digest(), before)
        self.cli("pause", "--reason", "owner review")
        out, _ = self.cli("tick")
        self.assertEqual(out["action"], "paused")
        self.cli("resume")
        out, _ = self.cli("status")
        self.assertIsNone(out["paused"])
        self.assertEqual(out["tasks"][0]["pool"]["attempts_remaining"], 4)
        out, _ = self.cli("event", "ingest", "--event-id", "e1", "--kind", "card-ready", "--task-id", "t1")
        self.assertTrue(out["new"])
        out, _ = self.cli("event", "ingest", "--event-id", "e1", "--kind", "card-ready", "--task-id", "t1")
        self.assertTrue(out["duplicate"])

    def test_set_state_does_not_clobber_the_state_path(self):
        # The positional was once named "state", so argparse overwrote --state with the new task state
        # and every `task set-state` failed with "controller state queued is missing".
        self.cli("init")
        self.cli("task", "add", "--task-id", "t1", "--workstream", "w", "--provider", "fake")
        self.cli("task", "set-state", "t1", "blocked-auth", "--reason", "auth-missing")
        out, _ = self.cli("task", "set-state", "t1", "queued", "--reason", "operator-requeue")
        self.assertEqual(out["state"], "queued")
        out, _ = self.cli("task", "show", "t1")
        self.assertEqual((out["state"], out["stop_reason"]), ("queued", "operator-requeue"))

    # Kills: `--brief` parsed but not passed to ensure_task, or not written by its INSERT (card #776).
    def test_task_add_brief_round_trips_through_task_show(self):
        self.cli("init")
        title = "Add --brief to task add: a short title for the PR title and the commit subject of controller PRs"
        out, _ = self.cli("task", "add", "--task-id", "t1", "--card", "776", "--workstream", "budgeted-autonomy",
                          "--provider", "fake", "--title", title, "--brief", "task add --brief")
        self.assertTrue(out["created"])
        out, _ = self.cli("task", "show", "t1")
        self.assertEqual((out["brief"], out["title"]), ("task add --brief", title))
        self.cli("task", "add", "--task-id", "t2", "--workstream", "w", "--provider", "fake", "--title", "no brief")
        out, _ = self.cli("task", "show", "t2")
        self.assertEqual((out["brief"], out["title"]), (None, "no brief"))

    # Kills: the length check deleted from `_brief`, or `>` made `>=` (a brief of exactly 72 is accepted). The
    # refusal is a usage error, exit 2, before the state is opened: the ledger is byte-identical afterwards.
    def test_a_brief_over_72_characters_exits_2_and_writes_nothing(self):
        self.cli("init")
        before = self.digest()
        out, err = self.cli("task", "add", "--task-id", "t1", "--workstream", "w", "--provider", "fake",
                            "--brief", "b" * 73, expect=2)
        self.assertIn("argument --brief: 73 characters; a brief is at most 72 (it is the PR title and the commit "
                      "subject)", err)
        self.assertEqual(self.digest(), before)
        self.cli("task", "show", "t1", expect=1)
        out, _ = self.cli("task", "add", "--task-id", "t1", "--workstream", "w", "--provider", "fake", "--brief", "b" * 72)
        self.assertTrue(out["created"])
        out, _ = self.cli("task", "show", "t1")
        self.assertEqual(out["brief"], "b" * 72)

    def test_lease_break_requires_confirmation(self):
        self.cli("init")
        out, err = self.cli("lease", "break", "--holder", "x", expect=1)
        self.assertIn("--confirm-stopped", err)

    def test_pr_records_list_show_and_observe_without_gh(self):
        self.cli("init")
        out, _ = self.cli("pr", "list")
        self.assertEqual(out, [])
        out, err = self.cli("pr", "show", "7", expect=1)
        self.assertIn("no PR record 7", err)
        out, err = self.cli("pr", "observe", "7", expect=1)
        self.assertIn("no PR record 7", err)

    def test_event_ingest_without_gh_records_once_and_acts_on_nothing(self):
        self.cli("init")
        self.cli("task", "add", "--task-id", "t1", "--workstream", "w", "--provider", "fake")
        out, _ = self.cli("event", "ingest", "--event-id", "pr-1", "--kind", "checks-green", "--task-id", "t1")
        self.assertEqual((out["new"], out["duplicate"], out["action"]), (True, False, None))
        out, _ = self.cli("event", "ingest", "--event-id", "pr-1", "--kind", "checks-green", "--task-id", "t1")
        self.assertEqual((out["new"], out["duplicate"]), (False, True))
        out, _ = self.cli("status")
        self.assertIsNone(out["tasks"][0]["pr_record"])

    def test_corrupt_state_exits_3(self):
        Path(self.state_path).parent.mkdir(parents=True)
        Path(self.state_path).write_bytes(b"garbage" * 200)
        out, err = self.cli("status", expect=3)
        self.assertIn("not a database", err)


if __name__ == "__main__":
    unittest.main()
