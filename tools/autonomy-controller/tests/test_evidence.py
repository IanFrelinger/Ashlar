"""PR 4 lane B: backup, restore and per-run archives (`ashlar_controller.evidence`).

Every test names, in the comment above it, the one-line mutation of evidence.py that makes it fail. Ledgers
are built through the Controller over the fake adapter; the only hand-made rows are the deliberately
malformed copies a refusal test needs. Nothing here calls an adapter: `adapter_calls()` is asserted
unchanged across every operation that has an adapter in reach.
"""
import hashlib
import json
import os
import shutil
import sqlite3
import tarfile
import unittest

from ashlar_controller import evidence
from ashlar_controller.models import PolicyError, SCHEMA_VERSION, StateCorrupt
from ashlar_controller.store import REQUIRED_TABLES, Store

from .helpers import ControllerCase
from .test_pr_lifecycle import LifecycleCase


def sha256_of(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def set_schema_version(path, value):
    conn = sqlite3.connect(path, isolation_level=None)
    conn.execute("UPDATE meta SET value=? WHERE key='schema_version'", (value,))
    conn.close()


class EvidenceCase(ControllerCase):
    """A ledger with one task, one settled run and the surrounding journal, built through the Controller."""

    def setUp(self):
        super().setUp()
        self.ctl = self.make_controller()
        self.add_task()
        self.assertEqual(self.ctl.tick().action, "launched")
        self.assertEqual(self.ctl.tick().action, "settled")
        self.run_id = self.store.row("SELECT run_id FROM runs")["run_id"]
        self.bak = str(self.tmp / "backups" / "ledger.bak")

    def counts(self, conn):
        return {t: conn.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0] for t in REQUIRED_TABLES}


class Backup(EvidenceCase):
    # Kills: deleting backup's `if os.path.exists(dest_path): raise StateCorrupt(...)` guard.
    def test_backup_refuses_an_existing_destination(self):
        evidence.backup(self.store, self.bak, now=self.clock())
        before = read_bytes(self.bak)
        with self.assertRaises(StateCorrupt) as cm:
            evidence.backup(self.store, self.bak, now=self.clock())
        self.assertIn("already exists", str(cm.exception))
        self.assertEqual(read_bytes(self.bak), before)

    # Kills: replacing `store.conn.backup(dest)` with `dest.executescript(SCHEMA)` (an empty schema-only copy).
    def test_backup_row_counts_equal_the_source_and_the_copy_opens_with_the_same_controller_id(self):
        calls = self.adapter_calls()
        result = evidence.backup(self.store, self.bak, now=self.clock())
        self.assertEqual(self.adapter_calls(), calls)
        source_counts = self.counts(self.store.conn)
        # The source gained its own `backup` journal row after the copy; the copy was counted before it.
        source_counts["journal"] -= 1
        self.assertEqual(result["rows"], source_counts)
        self.assertGreaterEqual(result["rows"]["runs"], 1)
        self.assertEqual(result["schema_version"], str(SCHEMA_VERSION))
        copy = Store.open(self.bak)
        self.addCleanup(copy.close)
        self.assertEqual(copy.meta("controller_id"), self.store.meta("controller_id"))
        self.assertEqual(copy.row("SELECT COUNT(*) AS n FROM runs")["n"], result["rows"]["runs"])
        self.assertEqual(sorted(copy.row("SELECT run_id FROM runs")), sorted(self.store.row("SELECT run_id FROM runs")))

    # Kills: moving backup's `store.journal("backup", ...)` above the copy (the copy would then carry its own row).
    def test_backup_journals_in_the_source_after_the_copy_so_the_copy_carries_no_backup_row(self):
        evidence.backup(self.store, self.bak, now=self.clock())
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM journal WHERE kind='backup'")["n"], 1)
        copy = Store.open(self.bak, read_only=True)
        self.addCleanup(copy.close)
        self.assertEqual(copy.row("SELECT COUNT(*) AS n FROM journal WHERE kind='backup'")["n"], 0)

    # Kills: journalling `hashlib.sha256(dest_path.encode()).hexdigest()` (a hash of the path, not the file).
    def test_backup_journal_sha256_is_the_files(self):
        result = evidence.backup(self.store, self.bak, now=self.clock())
        detail = json.loads(self.store.row("SELECT detail FROM journal WHERE kind='backup'")["detail"])
        self.assertEqual(detail["sha256"], sha256_of(self.bak))
        self.assertEqual(result["sha256"], sha256_of(self.bak))
        self.assertEqual(os.path.abspath(detail["path"]), os.path.abspath(self.bak))


class Restore(EvidenceCase):
    def setUp(self):
        super().setUp()
        evidence.backup(self.store, self.bak, now=self.clock())
        self.dest = str(self.tmp / "restored" / "controller.db")

    # Kills: `KNOWN_SCHEMA_VERSIONS = ("1", "2", "99")`, or deleting the `version not in KNOWN_SCHEMA_VERSIONS` check.
    def test_restore_refuses_an_unknown_schema_and_writes_nothing(self):
        set_schema_version(self.bak, "99")
        with self.assertRaises(StateCorrupt) as cm:
            evidence.restore(self.bak, self.dest, now=self.clock())
        self.assertIn("schema_version '99' is not one this controller knows", str(cm.exception))
        self.assertFalse(os.path.exists(self.dest))
        self.assertFalse(os.path.exists(os.path.dirname(self.dest)))

    # Kills: deleting the `tables missing` check (the file would be copied in place and only then rejected).
    def test_restore_refuses_a_source_missing_a_required_table_and_writes_nothing(self):
        conn = sqlite3.connect(self.bak, isolation_level=None)
        conn.execute("DROP TABLE outbox")
        conn.close()
        with self.assertRaises(StateCorrupt) as cm:
            evidence.restore(self.bak, self.dest, now=self.clock())
        self.assertIn("outbox", str(cm.exception))
        self.assertFalse(os.path.exists(self.dest))

    # Kills: deleting the `os.path.isfile(src_path)` check (sqlite's own error would name no missing file).
    def test_restore_refuses_a_missing_source(self):
        missing = str(self.tmp / "nowhere.bak")
        with self.assertRaises(StateCorrupt) as cm:
            evidence.restore(missing, self.dest, now=self.clock())
        self.assertIn("does not exist", str(cm.exception))
        self.assertFalse(os.path.exists(self.dest))

    # Kills: `if dest_exists and not replace` -> `if False` (or a `replace=True` default).
    def test_restore_refuses_an_existing_destination_without_replace_and_leaves_it_byte_identical(self):
        Store.init(self.dest, "ctl-live", now=self.clock()).close()
        before = read_bytes(self.dest)
        with self.assertRaises(StateCorrupt) as cm:
            evidence.restore(self.bak, self.dest, now=self.clock())
        self.assertIn("pass replace", str(cm.exception))
        self.assertEqual(read_bytes(self.dest), before)
        live = Store.open(self.dest)
        self.addCleanup(live.close)
        self.assertEqual(live.meta("controller_id"), "ctl-live")

    # Kills: deleting `os.replace(tmp_path, dest_path)` (the copy would stay beside the live file).
    def test_restore_with_replace_swaps_the_file_and_the_restored_store_dispatches(self):
        Store.init(self.dest, "ctl-live", now=self.clock()).close()
        result = evidence.restore(self.bak, self.dest, replace=True, now=self.clock())
        self.assertTrue(result["replaced"])
        self.assertEqual(result["schema_version_after"], str(SCHEMA_VERSION))
        restored = Store.open(self.dest)
        self.assertEqual(restored.meta("controller_id"), "ctl-test")
        self.assertEqual(restored.row("SELECT COUNT(*) AS n FROM runs")["n"], 1)
        ctl = self.make_controller(store=restored)
        self.assertTrue(ctl.ensure_task("t2", workstream="budgeted-autonomy", provider="fake", title="after restore"))
        self.assertEqual(ctl.tick().action, "launched")
        self.assertEqual(ctl.tick().action, "settled")
        self.assertEqual(restored.row("SELECT COUNT(*) AS n FROM runs")["n"], 2)
        leftovers = [n for n in os.listdir(os.path.dirname(self.dest)) if n.startswith(".restore-")]
        self.assertEqual(leftovers, [])

    # Kills: `KNOWN_SCHEMA_VERSIONS = ("2",)` (a schema-1 backup would be refused instead of migrated).
    def test_restore_of_a_schema_1_file_opens_and_is_migrated(self):
        set_schema_version(self.bak, "1")
        result = evidence.restore(self.bak, self.dest, now=self.clock())
        self.assertEqual((result["schema_version_before"], result["schema_version_after"]), ("1", str(SCHEMA_VERSION)))
        restored = Store.open(self.dest)
        self.addCleanup(restored.close)
        self.assertEqual(restored.meta("schema_version"), str(SCHEMA_VERSION))
        kinds = [r["kind"] for r in restored.rows("SELECT kind FROM journal ORDER BY id")]
        self.assertEqual(kinds.count("schema-migrated"), 1)
        self.assertEqual(kinds.count("restored"), 1)
        # The source file itself was not touched.
        source = sqlite3.connect(f"file:{self.bak}?mode=ro", uri=True)
        self.assertEqual(source.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()[0], "1")
        source.close()

    # Kills: deleting `_remove_sidecars(dest_path)` (a stale WAL would be replayed over the restored pages).
    def test_restore_removes_stale_wal_and_shm_sidecars(self):
        Store.init(self.dest, "ctl-live", now=self.clock()).close()
        # A valid WAL from a page-identical sibling of the backup, holding one extra meta row. Left beside the
        # destination it would be replayed onto the restored file; the restore must remove it first.
        sibling = str(self.tmp / "sibling.db")
        shutil.copyfile(self.bak, sibling)
        conn = sqlite3.connect(sibling, isolation_level=None)
        conn.execute("PRAGMA wal_autocheckpoint=0")
        conn.execute("INSERT INTO meta(key, value) VALUES ('stale', 'yes')")
        stale_wal = read_bytes(sibling + "-wal")
        conn.close()
        self.assertGreater(len(stale_wal), 32)
        with open(self.dest + "-wal", "wb") as f:
            f.write(stale_wal)
        with open(self.dest + "-shm", "wb") as f:
            f.write(b"stale shared memory")
        evidence.restore(self.bak, self.dest, replace=True, now=self.clock())
        self.assertFalse(os.path.exists(self.dest + "-wal"))
        self.assertFalse(os.path.exists(self.dest + "-shm"))
        restored = Store.open(self.dest)
        self.addCleanup(restored.close)
        self.assertIsNone(restored.meta("stale"))
        self.assertEqual(restored.meta("controller_id"), "ctl-test")

    # Kills: journalling `"replaced": False` unconditionally (or dropping `source` / `sha256` from the detail).
    def test_restore_journals_restored_with_source_sha256_and_replaced(self):
        Store.init(self.dest, "ctl-live", now=self.clock()).close()
        src_sha = sha256_of(self.bak)
        result = evidence.restore(self.bak, self.dest, replace=True, now=self.clock())
        restored = Store.open(self.dest, read_only=True)
        self.addCleanup(restored.close)
        rows = restored.rows("SELECT detail FROM journal WHERE kind='restored'")
        self.assertEqual(len(rows), 1)
        detail = json.loads(rows[0]["detail"])
        self.assertEqual(detail, {"source": os.path.abspath(self.bak), "sha256": src_sha, "replaced": True})
        self.assertEqual(result["sha256"], src_sha)
        self.assertEqual(result["source"], self.bak)
        fresh = evidence.restore(self.bak, str(self.tmp / "fresh" / "controller.db"), now=self.clock())
        self.assertFalse(fresh["replaced"])


class Archive(LifecycleCase):
    """A ledger with one task, one settled implementation run and an open PR (through the lifecycle fixtures)."""

    def setUp(self):
        super().setUp()
        self.make()
        self.pr = self.open_pr()
        self.run_id = self.store.row("SELECT run_id FROM runs")["run_id"]
        self.runs_dir = str(self.tmp / "runs")
        self.out_dir = str(self.tmp / "archives")

    def make_run_dir(self, run_id=None, transcript=True):
        """A run directory the way the Claude adapter lays it out: manifest.json, stdout.json, stderr.log."""
        run_id = run_id or self.run_id
        d = self.tmp / "runs" / run_id
        d.mkdir(parents=True)
        transcript_path = None
        if transcript:
            projects = self.tmp / "projects" / "slug"
            projects.mkdir(parents=True)
            transcript_path = projects / "fake-session-1.jsonl"
            transcript_path.write_text('{"type":"user","text":"TRANSCRIPT-BODY"}\n', encoding="utf-8")
        manifest = {"run_id": run_id, "task_id": "t1", "session_id": "fake-session-1",
                    "transcript_path": str(transcript_path) if transcript else str(self.tmp / "gone.jsonl"),
                    "projects_dir": None}
        (d / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        (d / "stdout.json").write_bytes(b'{"type":"result","result":"done"}\n')
        (d / "stderr.log").write_bytes(b"no errors\n")
        (d / "workspace").mkdir()                       # a directory: not a member
        return str(d)

    def members_of(self, path):
        with tarfile.open(path, "r:gz") as tar:
            return {m.name: tar.extractfile(m).read() for m in tar.getmembers() if m.isfile()}

    # Kills: deleting archive_run's `if os.path.exists(out_path): raise StateCorrupt(...)` guard.
    def test_archive_refuses_an_existing_path(self):
        self.make_run_dir()
        first = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        before = read_bytes(first["path"])
        with self.assertRaises(StateCorrupt):
            evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        self.assertEqual(read_bytes(first["path"]), before)
        self.assertEqual(self.journal_count("evidence-archived"), 1)

    # Kills: `arcname = name` (run files added without the `run/` prefix), or skipping the transcript member.
    def test_archive_members_include_the_run_files_the_transcript_the_ledger_and_the_manifest(self):
        self.make_run_dir(transcript=True)
        result = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        self.assertEqual(result["path"], os.path.join(self.out_dir, f"{self.run_id}.tar.gz"))
        self.assertEqual(result["members"], ["run/manifest.json", "run/stderr.log", "run/stdout.json",
                                             "transcript.jsonl", "ledger.json", "MANIFEST.txt"])
        self.assertTrue(result["transcript_included"])
        members = self.members_of(result["path"])
        self.assertEqual(set(members), set(result["members"]))
        self.assertIn(b"TRANSCRIPT-BODY", members["transcript.jsonl"])
        self.assertIn(b'"result":"done"', members["run/stdout.json"])
        self.assertEqual(members["run/stderr.log"], b"no errors\n")
        self.assertNotIn("run/workspace", members)

    # Kills: `"transcript_included": True` unconditionally, or `transcript = manifest.get("transcript_path")`.
    def test_archive_without_a_transcript_omits_the_member_and_says_so(self):
        self.make_run_dir(transcript=False)
        result = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        self.assertFalse(result["transcript_included"])
        self.assertNotIn("transcript.jsonl", result["members"])
        self.assertNotIn("transcript.jsonl", self.members_of(result["path"]))
        self.assertIn("run/manifest.json", result["members"])

    # Kills: deleting the `prs WHERE task_id=? ORDER BY rowid DESC` fallback (the implementation run's
    # `runs.pr_number` is NULL, so `pr` would be None).
    def test_ledger_json_carries_the_run_the_task_the_pr_and_journal_rows_with_the_run_id(self):
        self.make_run_dir()
        result = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        ledger = json.loads(self.members_of(result["path"])["ledger.json"].decode("utf-8"))
        self.assertEqual(ledger["run"]["run_id"], self.run_id)
        self.assertIsNone(ledger["run"]["pr_number"])
        self.assertEqual(ledger["task"]["task_id"], "t1")
        self.assertEqual(ledger["pool"]["pool_id"], ledger["run"]["pool_id"])
        self.assertEqual(ledger["pr"]["pr_number"], self.pr["pr_number"])
        self.assertEqual(result["pr_number"], self.pr["pr_number"])
        with_run = [j for j in ledger["journal"] if j["run_id"] == self.run_id]
        self.assertGreaterEqual(len(with_run), 1)
        self.assertIn("launched", {j["kind"] for j in with_run})
        self.assertTrue(all(j["run_id"] == self.run_id or j["task_id"] == "t1" for j in ledger["journal"]))
        self.assertEqual(ledger["policy_version"]["id"], ledger["run"]["policy_version_id"])
        self.assertEqual(ledger["archived_at"], evidence.utcnow_iso(self.clock()))
        self.assertIsInstance(ledger["usage"], list)

    # Kills: raising when `<runs_dir>/<run_id>` is missing (`if not os.path.isdir(run_dir): raise`).
    def test_archive_of_a_run_without_a_run_dir_holds_the_ledger_only(self):
        self.assertFalse(os.path.isdir(os.path.join(self.runs_dir, self.run_id)))
        result = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        self.assertEqual(result["members"], ["ledger.json", "MANIFEST.txt"])
        self.assertFalse(result["transcript_included"])
        members = self.members_of(result["path"])
        self.assertEqual(set(members), {"ledger.json", "MANIFEST.txt"})
        self.assertEqual(json.loads(members["ledger.json"])["run"]["run_id"], self.run_id)

    # Kills: replacing `raise PolicyError(...)` for an unknown run with `return {}`.
    def test_an_unknown_run_id_raises_and_writes_nothing(self):
        with self.assertRaises(PolicyError) as cm:
            evidence.archive_run(self.store, "run-does-not-exist", self.out_dir, self.runs_dir, now=self.clock())
        self.assertIn("no run run-does-not-exist", str(cm.exception))
        self.assertEqual(evidence.list_archives(self.out_dir), [])
        self.assertEqual(self.journal_count("evidence-archived"), 0)

    # Kills: computing `digest` from `ledger_bytes` (or before the tar is closed) instead of the written file.
    def test_the_archives_sha256_in_the_journal_equals_the_files(self):
        self.make_run_dir()
        result = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        row = self.store.row("SELECT * FROM journal WHERE kind='evidence-archived'")
        detail = json.loads(row["detail"])
        self.assertEqual(detail["sha256"], sha256_of(result["path"]))
        self.assertEqual(result["sha256"], sha256_of(result["path"]))
        self.assertEqual((row["task_id"], row["run_id"]), ("t1", self.run_id))
        self.assertEqual(detail["members"], result["members"])
        self.assertEqual(os.path.abspath(detail["path"]), os.path.abspath(result["path"]))

    # Kills: dropping the size from MANIFEST.txt lines (`lines += [name for name, size in sizes]`).
    def test_manifest_txt_names_every_member_with_its_byte_size(self):
        self.make_run_dir()
        result = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        members = self.members_of(result["path"])
        text = members["MANIFEST.txt"].decode("utf-8")
        self.assertIn(f"run_id {self.run_id}", text)
        self.assertIn(f"archived_at {evidence.utcnow_iso(self.clock())}", text)
        for name, body in members.items():
            if name == "MANIFEST.txt":
                self.assertIn("MANIFEST.txt", text)
            else:
                self.assertIn(f"{len(body)} {name}", text)

    # Kills: `return found` without the sort by run_id (or `"bytes": 0`).
    def test_list_archives_lists_what_was_written_sorted_by_run_id(self):
        self.assertEqual(evidence.list_archives(self.out_dir), [])
        self.make_run_dir()
        b = evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        # A second run for the same task: a repair round after a red check on the open PR.
        from .test_pr_lifecycle import red
        self.gh.set_checks("head-1", red("head-1"))
        self.assertEqual(self.ctl.tick().action, "launched")
        second = self.store.row("SELECT run_id FROM runs WHERE run_id != ?", (self.run_id,))["run_id"]
        a = evidence.archive_run(self.store, second, self.out_dir, self.runs_dir, now=self.clock())
        (self.tmp / "archives" / "notes.txt").write_text("not an archive", encoding="utf-8")
        listed = evidence.list_archives(self.out_dir)
        self.assertEqual([x["run_id"] for x in listed], sorted([self.run_id, second]))
        by_id = {x["run_id"]: x for x in listed}
        for res in (a, b):
            self.assertEqual(by_id[res["run_id"]]["path"], res["path"])
            self.assertEqual(by_id[res["run_id"]]["bytes"], os.path.getsize(res["path"]))
            self.assertRegex(by_id[res["run_id"]]["modified_at"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
        self.assertEqual(evidence.list_archives(str(self.tmp / "never-made")), [])

    # Kills: an `archive_run` that polls the adapter for the run (e.g. `adapter.poll(run["native_run_handle"])`);
    # the LLM-free rule: evidence is read from the ledger and the disk, never from a provider.
    def test_archiving_and_listing_make_zero_adapter_calls(self):
        self.make_run_dir()
        calls = self.adapter_calls()
        evidence.archive_run(self.store, self.run_id, self.out_dir, self.runs_dir, now=self.clock())
        evidence.list_archives(self.out_dir)
        evidence.backup(self.store, str(self.tmp / "b.bak"), now=self.clock())
        self.assertEqual(self.adapter_calls(), calls)
        self.assertEqual(self.adapter.launch_count, 1)


if __name__ == "__main__":
    unittest.main()
