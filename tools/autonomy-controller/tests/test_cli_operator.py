"""PR 4 acceptance, part 2: the operator commands through the real command line (`controller.py` as a
subprocess, the fake adapter): `metrics`, `report`, `backup`, `restore`, `evidence archive|list`, and
the controls `pause`/`resume`/`provider disable|enable`/`run cancel` taking effect at the next tick."""
import hashlib
import json
import os
import sqlite3
import tarfile
import unittest
from pathlib import Path

from .test_cli import CommandLine


def sha256_of(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class OperatorCommands(CommandLine):
    def seed(self):
        """A ledger with one completed run (fake adapter) and one queued task."""
        self.cli("init", "--controller-id", "ctl-op")
        self.cli("task", "add", "--task-id", "t1", "--card", "772", "--workstream", "budgeted-autonomy",
                 "--provider", "fake", "--title", "PR 4")
        out, _ = self.cli("tick")
        self.assertEqual(out["action"], "launched")
        out, _ = self.cli("tick")
        self.assertEqual(out["action"], "settled")
        self.cli("task", "add", "--task-id", "t2", "--workstream", "budgeted-autonomy", "--provider", "fake", "--title", "next")
        return out["settled"][0]["run_id"]

    def test_metrics_are_counts_from_the_ledger(self):
        self.seed()
        out, _ = self.cli("metrics")
        self.assertEqual(out["runs"]["total"], 1)
        self.assertEqual(out["runs"]["by_stop_reason"], {"completed": 1})
        self.assertEqual(out["tasks"]["by_state"], {"done": 1, "queued": 1})
        self.assertEqual(out["runs"]["consumed_total"], 3.0)
        self.assertEqual(out["prs"]["total"], 0)
        self.assertEqual(out["controls"]["paused"], False)

    def test_report_is_markdown_from_status_and_metrics(self):
        self.seed()
        text, _ = self.cli("report")
        # plain text: the --json flag is always on in these tests, so parse the json shape instead
        out = text if isinstance(text, dict) else json.loads(text)
        self.assertIn("## Tasks", out["markdown"])
        self.assertIn("t1", out["markdown"])
        self.assertIn("completed", out["markdown"])
        self.assertEqual(out["metrics"]["runs"]["total"], 1)
        self.assertEqual(out["status"]["tasks"][0]["task_id"], "t1")

    def test_backup_then_restore_round_trip_and_the_restored_ledger_accepts_work(self):
        self.seed()
        dest = str(self.tmp / "backups" / "ledger.db")
        out, _ = self.cli("backup", dest)
        self.assertEqual(out["path"], dest)
        self.assertEqual(out["sha256"], sha256_of(dest))
        self.assertEqual(out["rows"]["tasks"], 2)
        # a second backup to the same path is refused and the file is untouched
        _, err = self.cli("backup", dest, expect=3)
        self.assertEqual(out["sha256"], sha256_of(dest))
        # restore without --replace refuses the live state and leaves it alone
        before = self.digest()
        _, err = self.cli("restore", dest, expect=3)
        self.assertIn("replace", err)
        self.assertEqual(self.digest(), before)
        # change the live ledger, then restore over it
        self.cli("task", "add", "--task-id", "t3", "--workstream", "w", "--provider", "fake")
        out, _ = self.cli("restore", dest, "--replace")
        self.assertTrue(out["replaced"])
        self.assertEqual(out["schema_version_after"], "2")
        tasks, _ = self.cli("task", "list")
        self.assertEqual(sorted(t["task_id"] for t in tasks), ["t1", "t2"])
        # the restored ledger refuses nothing it should accept: a tick launches the queued task
        out, _ = self.cli("tick")
        self.assertEqual((out["action"], out["task_id"]), ("launched", "t2"))
        st, _ = self.cli("status")
        self.assertEqual(st["controller_id"], "ctl-op")

    def test_restore_refuses_an_unknown_schema_and_writes_nothing(self):
        self.cli("init")
        src = str(self.tmp / "unknown.db")
        self.cli("backup", src)
        conn = sqlite3.connect(src)
        conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'")
        conn.commit(); conn.close()
        fresh = str(self.tmp / "state2" / "controller.db")
        _, err = self.cli("restore", src, state=fresh, expect=3)
        self.assertIn("99", err)
        self.assertFalse(Path(fresh).exists())

    def test_evidence_archive_and_list(self):
        run_id = self.seed()
        state_dir = Path(self.state_path).parent
        run_dir = state_dir / "runs" / run_id
        run_dir.mkdir(parents=True)
        (run_dir / "manifest.json").write_text(json.dumps({"run_id": run_id, "session_id": "s1"}), encoding="utf-8")
        (run_dir / "stdout.json").write_text('{"type":"result"}\n', encoding="utf-8")
        (run_dir / "stderr.log").write_text("", encoding="utf-8")
        out, _ = self.cli("evidence", "archive", run_id)
        path = out["path"]
        self.assertEqual(Path(path).parent, state_dir / "evidence")
        self.assertEqual(out["sha256"], sha256_of(path))
        with tarfile.open(path) as tar:
            names = set(tar.getnames())
            ledger = json.loads(tar.extractfile("ledger.json").read().decode("utf-8"))
        for member in ("run/manifest.json", "run/stdout.json", "run/stderr.log", "ledger.json", "MANIFEST.txt"):
            self.assertIn(member, names)
        self.assertEqual(ledger["run"]["run_id"], run_id)
        self.assertEqual(ledger["task"]["task_id"], "t1")
        self.assertFalse(out["transcript_included"])
        # never overwritten
        _, err = self.cli("evidence", "archive", run_id, expect=3)
        self.assertEqual(out["sha256"], sha256_of(path))
        listed, _ = self.cli("evidence", "list")
        self.assertEqual([a["run_id"] for a in listed], [run_id])
        # status names the archive now
        st, _ = self.cli("status")
        self.assertEqual(st["tasks"][0]["evidence"]["archive"], path)
        self.assertEqual(st["tasks"][0]["evidence"]["manifest"], str(run_dir / "manifest.json"))

    def test_controls_take_effect_at_the_next_tick_through_the_cli(self):
        self.cli("init")
        self.cli("task", "add", "--task-id", "t1", "--workstream", "w", "--provider", "fake")
        self.cli("provider", "disable", "fake")
        out, _ = self.cli("tick")
        self.assertEqual(out["action"], "idle")
        self.cli("provider", "enable", "fake")
        self.cli("pause", "--reason", "hold")
        out, _ = self.cli("tick")
        self.assertEqual(out["action"], "paused")
        self.cli("resume")
        out, _ = self.cli("tick")
        self.assertEqual(out["action"], "launched")
        # the fake adapter's default poll says completed; cancel of a finished run is a no-op, cancel of a
        # running one is covered in test_operator_controls (needs a scripted adapter)
        runs, _ = self.cli("run", "list")
        self.assertEqual(len(runs), 1)


if __name__ == "__main__":
    unittest.main()
