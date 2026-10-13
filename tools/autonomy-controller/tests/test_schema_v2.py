"""Schema 2 (PR 3): the additive migration from a schema-1 ledger, and the policy's `pr` section."""
import sqlite3
import unittest
from pathlib import Path

from ashlar_controller.models import PolicyError, SCHEMA_VERSION, StateCorrupt
from ashlar_controller.policy import PR_DEFAULTS, parse_policy
from ashlar_controller.store import Store

from .helpers import ControllerCase, example_policy_dict
import json


class SchemaMigration(ControllerCase):
    def downgrade_to_v1(self):
        """Rebuild the file as a schema-1 ledger: drop the PR 3 columns and schema 3's `tasks.brief`, and
        write version 1."""
        conn = sqlite3.connect(self.state_path, isolation_level=None)
        for table, cols in (("tasks", ["branch", "brief"]), ("prs", ["branch", "url", "title"]), ("runs", ["pr_number"])):
            for col in cols:
                conn.execute(f"ALTER TABLE {table} DROP COLUMN {col}")
        conn.execute("UPDATE meta SET value='1' WHERE key='schema_version'")
        conn.close()

    def test_a_schema_1_ledger_is_migrated_in_place_and_keeps_its_rows(self):
        store = self.init_store()
        ctl = self.make_controller(store=store)
        self.add_task()
        ctl.tick(); ctl.tick()
        runs_before = store.row("SELECT COUNT(*) AS n FROM runs")["n"]
        store.close()
        self.downgrade_to_v1()
        reopened = Store.open(self.state_path)
        self.addCleanup(reopened.close)
        self.assertEqual(reopened.meta("schema_version"), str(SCHEMA_VERSION))
        cols = {r[1] for r in reopened.conn.execute("PRAGMA table_info(prs)")}
        self.assertTrue({"branch", "url", "title"} <= cols)
        self.assertIn("pr_number", {r[1] for r in reopened.conn.execute("PRAGMA table_info(runs)")})
        self.assertTrue({"branch", "brief"} <= {r[1] for r in reopened.conn.execute("PRAGMA table_info(tasks)")})
        self.assertEqual(reopened.row("SELECT COUNT(*) AS n FROM runs")["n"], runs_before)
        self.assertEqual(reopened.row("SELECT COUNT(*) AS n FROM tasks")["n"], 1)
        # One journalled step per schema: 1 to 2, then 2 to 3 (card #776).
        steps = [json.loads(r["detail"]) for r in reopened.rows("SELECT detail FROM journal WHERE kind='schema-migrated' "
                                                                "ORDER BY id")]
        self.assertEqual(steps, [{"from": 1, "to": 2}, {"from": 2, "to": 3}])

    def test_a_read_only_open_of_a_schema_1_ledger_refuses_rather_than_migrating(self):
        self.init_store().close()
        self.downgrade_to_v1()
        with self.assertRaises(StateCorrupt):
            Store.open(self.state_path, read_only=True)

    def test_an_unknown_future_schema_is_refused(self):
        s = self.init_store(); s.close()
        conn = sqlite3.connect(self.state_path, isolation_level=None)
        conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'"); conn.close()
        with self.assertRaises(StateCorrupt):
            Store.open(self.state_path)


class PrPolicySection(unittest.TestCase):
    def text(self, mutate=None):
        raw = example_policy_dict(); raw.pop("_comment", None)
        if mutate:
            mutate(raw)
        return json.dumps(raw)

    def test_defaults_apply_when_the_section_is_absent(self):
        p = parse_policy(self.text(lambda r: r.pop("pr")))
        self.assertEqual(p.pr["required_checks"], PR_DEFAULTS["required_checks"])
        self.assertEqual(p.pr["base_branch"], "master")
        self.assertEqual(p.to_json()["pr"]["branch_prefix"], "auto/")

    def test_the_example_names_the_five_required_checks(self):
        p = parse_policy(self.text())
        self.assertEqual(p.pr["required_checks"],
                         ["cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary"])

    def test_unknown_key_empty_checks_and_duplicates_are_refused(self):
        with self.assertRaises(PolicyError):
            parse_policy(self.text(lambda r: r["pr"].__setitem__("auto_merge", True)))
        with self.assertRaises(PolicyError):
            parse_policy(self.text(lambda r: r["pr"].__setitem__("required_checks", [])))
        with self.assertRaises(PolicyError):
            parse_policy(self.text(lambda r: r["pr"].__setitem__("required_checks", ["a", "a"])))
        with self.assertRaises(PolicyError):
            parse_policy(self.text(lambda r: r["pr"].__setitem__("base_branch", "")))


if __name__ == "__main__":
    unittest.main()
