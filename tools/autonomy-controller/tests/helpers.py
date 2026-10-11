"""Fixtures for the controller tests. Everything is a temp directory OUTSIDE the repository, so the
policy location check passes for the operator path and fails for a simulated worker checkout."""
from __future__ import annotations

import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path
from typing import Any, Dict, Optional

from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.controller import Controller
from ashlar_controller.policy import Policy, load_policy, parse_policy
from ashlar_controller.store import Store

HERE = Path(__file__).resolve().parent
PACKAGE_ROOT = HERE.parent
EXAMPLE_POLICY = PACKAGE_ROOT / "policy.example.json"


class FakeClock:
    def __init__(self, start: float = 1_800_000_000.0):
        self.now = start

    def __call__(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds


def example_policy_dict() -> Dict[str, Any]:
    return json.loads(EXAMPLE_POLICY.read_text(encoding="utf-8"))


class ControllerCase(unittest.TestCase):
    """A temp dir with an operator policy, a fresh state file and a controller over a fake adapter."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="ashlar-ctl-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.operator_dir = self.tmp / "operator"
        self.operator_dir.mkdir()
        self.state_path = str(self.tmp / "state" / "controller.db")
        self.clock = FakeClock()
        self.policy_path = self.write_policy()

    def write_policy(self, mutate=None, name: str = "policy.json") -> str:
        raw = example_policy_dict()
        raw.pop("_comment", None)
        if mutate:
            mutate(raw)
        p = self.operator_dir / name
        p.write_text(json.dumps(raw, indent=2), encoding="utf-8")
        os.chmod(p, 0o600)
        return str(p)

    def load(self, path: Optional[str] = None, roots=None) -> Policy:
        return load_policy(path or self.policy_path, roots=roots if roots is not None else [])

    def init_store(self) -> Store:
        s = Store.init(self.state_path, "ctl-test", now=self.clock())
        self.addCleanup(lambda: s.close())
        return s

    def make_controller(self, store: Optional[Store] = None, policy: Optional[Policy] = None,
                        adapter: Optional[FakeAdapter] = None, holder: str = "holder-a", host: str = "host-a",
                        pid: int = 4242) -> Controller:
        store = store or (Store.open(self.state_path) if Path(self.state_path).exists() else self.init_store())
        self.store = store
        self.addCleanup(lambda: store.close())
        self.adapter = adapter or FakeAdapter()
        self.policy = policy or self.load()
        self.ctl = Controller(store, self.policy, {"fake": self.adapter}, holder_id=holder, host=host, pid=pid,
                              clock=self.clock)
        return self.ctl

    def add_task(self, task_id: str = "t1", provider: str = "fake", **kw) -> None:
        self.ctl.ensure_task(task_id, workstream="budgeted-autonomy", provider=provider, card_number=755,
                             title="PR 1", **kw)

    def pool(self, task_id: str = "t1"):
        t = self.store.row("SELECT pool_id FROM tasks WHERE task_id=?", (task_id,))
        return self.store.row("SELECT * FROM budget_pools WHERE pool_id=?", (t["pool_id"],))

    def task(self, task_id: str = "t1"):
        return self.store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))

    def run_row(self, run_id: str):
        return self.store.row("SELECT * FROM runs WHERE run_id=?", (run_id,))

    def adapter_calls(self) -> int:
        a = self.adapter
        return a.launch_count + a.poll_count + a.cancel_count + a.reconcile_count + a.usage_count
