"""Schema 3 (card #776): `tasks.brief`, the short title of a controller-opened PR. The additive migration from a
schema-2 ledger, the read-only refusal before it, and the length rule the ledger keeps."""
import json
import sqlite3
import unittest

from ashlar_controller.models import ControllerError, StateCorrupt
from ashlar_controller.store import Store

from .helpers import ControllerCase, downgrade_to_v2


def columns(conn, table):
    return {r[1] for r in conn.execute(f"PRAGMA table_info({table})")}


class SchemaMigration2To3(ControllerCase):
    # Kills: deleting the `version == "2"` step from check_integrity (a schema-2 ledger would be refused), the
    # `ALTER TABLE tasks ADD COLUMN brief` in _migrate_2_to_3, or its `schema-migrated` journal row.
    def test_a_schema_2_ledger_is_migrated_in_place_and_keeps_its_rows(self):
        store = self.init_store()
        ctl = self.make_controller(store=store)
        self.add_task()
        ctl.tick(); ctl.tick()
        runs_before = store.row("SELECT COUNT(*) AS n FROM runs")["n"]
        journal_before = store.row("SELECT COUNT(*) AS n FROM journal")["n"]
        store.close()
        downgrade_to_v2(self.state_path)
        reopened = Store.open(self.state_path)
        self.addCleanup(reopened.close)
        self.assertEqual(reopened.meta("schema_version"), "3")
        self.assertIn("brief", columns(reopened.conn, "tasks"))
        self.assertEqual([(r["task_id"], r["title"], r["brief"]) for r in reopened.rows("SELECT * FROM tasks")],
                         [("t1", "PR 1", None)])
        self.assertEqual(reopened.row("SELECT COUNT(*) AS n FROM runs")["n"], runs_before)
        migrated = reopened.rows("SELECT detail FROM journal WHERE kind='schema-migrated'")
        self.assertEqual([json.loads(r["detail"]) for r in migrated], [{"from": 2, "to": 3}])
        self.assertEqual(reopened.row("SELECT COUNT(*) AS n FROM journal")["n"], journal_before + 1)
        # The migrated ledger takes a brief.
        ctl = self.make_controller(store=reopened)
        self.assertTrue(ctl.ensure_task("t2", workstream="w", provider="fake", title="the instruction", brief="short"))
        self.assertEqual(reopened.row("SELECT brief FROM tasks WHERE task_id='t2'")["brief"], "short")

    # Kills: `if version == "2" and not self.read_only` -> `if version == "2"` (the read-only open would try to
    # migrate and fail on the write instead of naming the schema), the way a schema-1 ledger is refused read-only.
    def test_a_read_only_open_of_a_schema_2_ledger_refuses_rather_than_migrating(self):
        self.init_store().close()
        downgrade_to_v2(self.state_path)
        with self.assertRaises(StateCorrupt) as cm:
            Store.open(self.state_path, read_only=True)
        self.assertIn("schema_version '2' != 3", str(cm.exception))
        conn = sqlite3.connect(f"file:{self.state_path}?mode=ro", uri=True)
        self.addCleanup(conn.close)
        self.assertEqual(conn.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()[0], "2")
        self.assertNotIn("brief", columns(conn, "tasks"))
        self.assertEqual(conn.execute("SELECT COUNT(*) FROM journal WHERE kind='schema-migrated'").fetchone()[0], 0)


class BriefLength(ControllerCase):
    # Kills: deleting the length check from Controller.ensure_task (a caller other than the CLI could store a
    # brief that cannot be a commit subject), or moving it below `budget.create_pool` (a refused task would
    # leave its pool behind).
    def test_ensure_task_refuses_a_brief_over_72_characters_and_writes_nothing(self):
        ctl = self.make_controller()
        with self.assertRaises(ControllerError) as cm:
            ctl.ensure_task("t1", workstream="w", provider="fake", title="the instruction", brief="b" * 73)
        self.assertEqual(str(cm.exception), "the brief of task t1 is 73 characters; a brief is at most 72 (it is the "
                                            "PR title and the commit subject)")
        for table in ("tasks", "budget_pools"):
            self.assertEqual(self.store.row(f"SELECT COUNT(*) AS n FROM {table}")["n"], 0)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM journal WHERE kind='task-created'")["n"], 0)
        self.assertTrue(ctl.ensure_task("t1", workstream="w", provider="fake", title="the instruction", brief="b" * 72))


if __name__ == "__main__":
    unittest.main()
