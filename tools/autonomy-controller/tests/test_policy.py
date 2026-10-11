import json
import os
import unittest
from pathlib import Path

from ashlar_controller import budget
from ashlar_controller.models import PolicyError
from ashlar_controller.policy import Policy, load_policy, parse_policy

from .helpers import ControllerCase, example_policy_dict


class PolicyParsing(ControllerCase):
    def test_example_policy_is_valid_and_conservative(self):
        p = self.load()
        self.assertEqual(p.version, "2026-10-11.1")
        self.assertEqual(p.limits["active_writers"], 1)
        self.assertEqual(p.limits["same_failure_retries"], 1)
        self.assertEqual(p.limits["repair_rounds"], 2)
        self.assertEqual(p.limits["invocation_turn_ceiling"], 20)
        self.assertFalse(p.funding["paid_inference_enabled"])
        self.assertFalse(p.funding["new_paid_infrastructure"])
        self.assertAlmostEqual(p.funding["interactive_reserve_fraction"], 0.25)
        self.assertEqual(sorted(p.providers), ["claude", "codex", "cursor", "fake"])

    def test_policy_inside_a_checkout_is_refused(self):
        with self.assertRaises(PolicyError) as cm:
            self.load(roots=[self.operator_dir])
        self.assertIn("inside a checkout", str(cm.exception))

    def test_policy_inside_a_registered_worker_checkout_is_refused(self):
        worker = self.tmp / "worker-checkout"
        worker.mkdir()
        copy = worker / "policy.json"
        copy.write_text(Path(self.policy_path).read_text(encoding="utf-8"), encoding="utf-8")
        os.chmod(copy, 0o600)
        with self.assertRaises(PolicyError):
            load_policy(str(copy), roots=[worker.resolve()])

    def test_world_writable_policy_is_refused(self):
        os.chmod(self.policy_path, 0o666)
        with self.assertRaises(PolicyError) as cm:
            self.load()
        self.assertIn("world-writable", str(cm.exception))

    def test_missing_limit_is_refused(self):
        path = self.write_policy(lambda r: r["limits"].pop("same_failure_retries"), name="p2.json")
        with self.assertRaises(PolicyError) as cm:
            self.load(path)
        self.assertIn("same_failure_retries", str(cm.exception))

    def test_limit_outside_range_is_refused(self):
        path = self.write_policy(lambda r: r["limits"].__setitem__("active_writers", 2), name="p3.json")
        with self.assertRaises(PolicyError) as cm:
            self.load(path)
        self.assertIn("active_writers=2", str(cm.exception))

    def test_invocation_ceiling_above_task_ceiling_is_refused(self):
        path = self.write_policy(lambda r: r["limits"].__setitem__("invocation_turn_ceiling", 200) or
                                 r["limits"].__setitem__("task_turn_ceiling", 100), name="p4.json")
        with self.assertRaises(PolicyError):
            self.load(path)

    def test_paid_spend_provider_with_paid_inference_off_is_refused(self):
        def mutate(r):
            r["providers"]["fake"]["pool_kind"] = "paid_spend"
        path = self.write_policy(mutate, name="p5.json")
        with self.assertRaises(PolicyError) as cm:
            self.load(path)
        self.assertIn("paid_inference_enabled is false", str(cm.exception))

    def test_bad_unobservable_choice_is_refused(self):
        path = self.write_policy(lambda r: r["funding"].__setitem__("unobservable_allowance", "yolo"), name="p6.json")
        with self.assertRaises(PolicyError):
            self.load(path)

    def test_not_json_is_refused(self):
        p = self.operator_dir / "bad.json"
        p.write_text("{not json", encoding="utf-8")
        os.chmod(p, 0o600)
        with self.assertRaises(PolicyError):
            self.load(str(p))

    def test_hash_tracks_content(self):
        a = self.load()
        b = self.load(self.write_policy(name="same.json"))
        c = self.load(self.write_policy(lambda r: r.__setitem__("policy_version", "x"), name="diff.json"))
        self.assertEqual(a.sha256, b.sha256)
        self.assertNotEqual(a.sha256, c.sha256)


class WorkerCannotRaiseLimits(ControllerCase):
    def test_a_worker_side_policy_with_higher_limits_does_not_change_the_pool(self):
        ctl = self.make_controller()
        self.add_task()
        worker = self.tmp / "worker"
        worker.mkdir()
        self.store.register_worker_checkout(str(worker))
        raw = example_policy_dict()
        raw["limits"]["invocation_turn_ceiling"] = 200
        raw["limits"]["task_turn_ceiling"] = 5000
        raw["limits"]["max_attempts_per_task"] = 20
        edited = worker / "policy.json"
        edited.write_text(json.dumps(raw), encoding="utf-8")
        os.chmod(edited, 0o600)
        # 1. The edited file cannot even be loaded as the active policy.
        with self.assertRaises(PolicyError):
            load_policy(str(edited), worker_checkouts=self.store.worker_checkouts(), roots=[worker.resolve()])
        # 2. Even a Policy object carrying those limits cannot widen an existing pool: the pool's
        #    ceilings were frozen at creation and the reservation takes the minimum.
        inflated = parse_policy(json.dumps(raw), source="worker-edit")
        r = budget.reserve(self.store, run_id="r1", task_id="t1", pool_id=self.pool()["pool_id"], provider="fake",
                           policy=inflated, policy_version_id=None, holder="h", now=self.clock())
        self.assertEqual(r["reservation"], 20.0)
        self.assertEqual(r["limits"]["task_ceiling"], 80.0)
        self.assertEqual(r["limits"]["attempts_max"], 4)

    def test_a_tighter_policy_does_tighten(self):
        ctl = self.make_controller()
        self.add_task()
        raw = example_policy_dict()
        raw["limits"]["invocation_turn_ceiling"] = 5
        tighter = parse_policy(json.dumps(raw), source="operator-tightened")
        r = budget.reserve(self.store, run_id="r1", task_id="t1", pool_id=self.pool()["pool_id"], provider="fake",
                           policy=tighter, policy_version_id=None, holder="h", now=self.clock())
        self.assertEqual(r["reservation"], 5.0)


if __name__ == "__main__":
    unittest.main()
