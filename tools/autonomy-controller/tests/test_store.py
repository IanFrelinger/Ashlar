import os
import sqlite3
import unittest
from pathlib import Path

from ashlar_controller.models import StateCorrupt, StateMissing
from ashlar_controller.store import Store

from .helpers import ControllerCase


class StateIntegrity(ControllerCase):
    def test_missing_state_stops_dispatch_and_creates_nothing(self):
        with self.assertRaises(StateMissing) as cm:
            Store.open(self.state_path)
        self.assertIn("recreating an empty budget", str(cm.exception))
        self.assertFalse(Path(self.state_path).exists())

    def test_garbage_file_is_corrupt(self):
        Path(self.state_path).parent.mkdir(parents=True)
        Path(self.state_path).write_bytes(b"this is not a database at all" * 100)
        with self.assertRaises(StateCorrupt):
            Store.open(self.state_path)

    def test_missing_table_is_corrupt(self):
        s = self.init_store()
        s.close()
        conn = sqlite3.connect(self.state_path)
        conn.execute("DROP TABLE budget_pools")
        conn.commit()
        conn.close()
        with self.assertRaises(StateCorrupt) as cm:
            Store.open(self.state_path)
        self.assertIn("budget_pools", str(cm.exception))

    def test_schema_version_mismatch_is_corrupt(self):
        s = self.init_store()
        s.close()
        conn = sqlite3.connect(self.state_path)
        conn.execute("UPDATE meta SET value='999' WHERE key='schema_version'")
        conn.commit()
        conn.close()
        with self.assertRaises(StateCorrupt):
            Store.open(self.state_path)

    def test_init_refuses_to_overwrite(self):
        self.init_store().close()
        with self.assertRaises(StateCorrupt):
            Store.init(self.state_path, "again")

    def test_read_only_store_refuses_writes(self):
        self.init_store().close()
        ro = Store.open(self.state_path, read_only=True)
        with self.assertRaises(StateCorrupt):
            with ro.tx():
                pass
        ro.close()


if __name__ == "__main__":
    unittest.main()
