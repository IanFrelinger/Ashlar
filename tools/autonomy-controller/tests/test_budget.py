import json
import sqlite3
import unittest

from ashlar_controller import budget
from ashlar_controller.models import (BudgetExhausted, PolicyError, RunState, StopReason, TaskState, UsageFreshness,
                                      UsageKind, UsageObservation)
from ashlar_controller.policy import Policy, parse_policy
from ashlar_controller.store import Store

from .helpers import ControllerCase, example_policy_dict


class Reservations(ControllerCase):
    def setUp(self):
        super().setUp()
        self.make_controller()
        self.add_task()
        self.pool_id = self.pool()["pool_id"]

    def reserve(self, run_id="r1", policy=None):
        return budget.reserve(self.store, run_id=run_id, task_id="t1", pool_id=self.pool_id, provider="fake",
                              policy=policy or self.policy, policy_version_id=None, holder="h", now=self.clock())

    def test_reserve_then_settle_accounts_cumulatively(self):
        r = self.reserve()
        p = self.pool()
        self.assertEqual((p["reserved"], p["consumed"], p["attempts_used"]), (20.0, 0.0, 1))
        self.assertEqual(budget.remaining(p), 60.0)
        budget.settle(self.store, "r1", state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed=7.0)
        p = self.pool()
        self.assertEqual((p["reserved"], p["consumed"], p["overshoot"]), (0.0, 7.0, 0.0))
        self.assertEqual(budget.remaining(p), 73.0)
        self.assertEqual(self.run_row("r1")["state"], RunState.FINISHED)
        self.assertEqual(json.loads(self.run_row("r1")["limits_json"])["policy_version"], self.policy.version)

    def test_restart_preserves_the_reservation(self):
        self.reserve()
        self.store.close()
        reopened = Store.open(self.state_path)
        self.addCleanup(reopened.close)
        self.store = reopened
        p = self.pool()
        self.assertEqual(p["reserved"], 20.0)
        self.assertEqual(self.run_row("r1")["state"], RunState.RESERVED)
        # A tight policy shows the reservation still counts: 80 - 20 reserved leaves 60, so a 61-turn
        # invocation cannot be reserved.
        raw = example_policy_dict()
        raw["limits"]["invocation_turn_ceiling"] = 61
        raw["limits"]["task_turn_ceiling"] = 80
        self.assertEqual(budget.remaining(p), 60.0)
        # and settling the preserved run releases exactly what was reserved
        budget.settle(reopened, "r1", state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed=2.0)
        self.assertEqual(self.pool()["reserved"], 0.0)
        self.assertEqual(self.pool()["consumed"], 2.0)

    def test_second_active_writer_is_refused_by_the_database(self):
        self.reserve("r1")
        with self.assertRaises(sqlite3.IntegrityError):
            with self.store.tx():
                self.store.conn.execute(
                    "INSERT INTO runs(run_id, task_id, pool_id, attempt_no, role, provider, model, effort, speed, "
                    "funding_pool, limits_json, state, reservation, reserved_at) VALUES "
                    "('r2','t1',?,2,'implementation','fake','m','e','s','p','{}','reserved',1,'x')", (self.pool_id,))

    def test_budget_exhaustion_refuses_a_reservation(self):
        for i in range(3):
            self.reserve(f"r{i}") if i == 0 else None
            budget.settle(self.store, "r0", state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed=25.0) if i == 0 else None
        # consumed 25; reserve twice more (20 each) -> consumed 25 + reserved 40 = 65; a fourth needs 20 > 15 left
        self.reserve("r1")
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state='finished' WHERE run_id='r1'")  # free the writer slot
        self.reserve("r2")
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state='finished' WHERE run_id='r2'")
        with self.assertRaises(BudgetExhausted) as cm:
            self.reserve("r3")
        self.assertIn(StopReason.TASK_BUDGET_EXHAUSTED, str(cm.exception))
        self.assertEqual(self.pool()["attempts_used"], 3)

    def test_attempts_exhaustion_refuses_a_reservation(self):
        for i in range(4):
            self.reserve(f"r{i}")
            budget.settle(self.store, f"r{i}", state=RunState.FAILED, stop_reason=StopReason.TASK_FAILED, consumed=1.0)
        with self.assertRaises(BudgetExhausted) as cm:
            self.reserve("r4")
        self.assertIn(StopReason.ATTEMPTS_EXHAUSTED, str(cm.exception))

    def test_unknown_consumption_is_charged_at_the_reservation(self):
        self.reserve()
        out = budget.settle(self.store, "r1", state=RunState.FAILED, stop_reason=StopReason.NO_FINAL_RESULT, consumed=None)
        self.assertEqual(out["charged"], 20.0)
        self.assertEqual(self.pool()["consumed"], 20.0)

    def test_overshoot_is_charged_in_full_and_recorded(self):
        self.reserve()
        out = budget.settle(self.store, "r1", state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed=27.0)
        self.assertEqual((out["charged"], out["overshoot"]), (27.0, 7.0))
        p = self.pool()
        self.assertEqual((p["consumed"], p["overshoot"], p["reserved"]), (27.0, 7.0, 0.0))

    def test_settling_twice_is_a_no_op(self):
        self.reserve()
        budget.settle(self.store, "r1", state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed=5.0)
        out = budget.settle(self.store, "r1", state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed=5.0)
        self.assertTrue(out["already_settled"])
        self.assertEqual(self.pool()["consumed"], 5.0)

    def test_disabled_provider_is_refused(self):
        raw = example_policy_dict()
        raw["providers"]["fake"]["enabled"] = False
        with self.assertRaises(PolicyError) as cm:
            self.reserve(policy=parse_policy(json.dumps(raw)))
        self.assertIn(StopReason.PROVIDER_DISABLED, str(cm.exception))

    def test_paid_route_is_refused_even_if_a_policy_object_slips_past_validation(self):
        raw = example_policy_dict()
        base = parse_policy(json.dumps(raw))
        providers = {k: dict(v) for k, v in base.providers.items()}
        providers["fake"]["pool_kind"] = UsageKind.PAID_SPEND
        paid = Policy(version=base.version, limits=base.limits, funding=base.funding, providers=providers,
                      idle=base.idle, sha256="x", source_path="x")
        with self.assertRaises(PolicyError) as cm:
            self.reserve(policy=paid)
        self.assertIn(StopReason.PAID_ROUTE_DISABLED, str(cm.exception))
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs")["n"], 0)


class SplitTasks(ControllerCase):
    def test_a_child_shares_and_cannot_reset_an_exhausted_parent_pool(self):
        ctl = self.make_controller()
        self.add_task("parent")
        pool_id = self.pool("parent")["pool_id"]
        with self.store.tx():
            self.store.conn.execute("UPDATE budget_pools SET consumed=79 WHERE pool_id=?", (pool_id,))
        self.add_task("child", parent_task_id="parent")
        self.assertEqual(self.pool("child")["pool_id"], pool_id)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM budget_pools")["n"], 1)
        with self.assertRaises(BudgetExhausted):
            budget.reserve(self.store, run_id="r1", task_id="child", pool_id=pool_id, provider="fake",
                           policy=self.policy, policy_version_id=None, holder="h")
        ctl.set_task_state("parent", TaskState.DONE)
        report = ctl.tick()
        self.assertEqual(report.action, "blocked")
        self.assertEqual(report.reason, StopReason.TASK_BUDGET_EXHAUSTED)
        self.assertEqual(self.task("child")["state"], TaskState.PARKED_BUDGET)
        self.assertEqual(self.adapter.launch_count, 0)


class UsageAccounting(ControllerCase):
    def setUp(self):
        super().setUp()
        self.make_controller()

    def obs(self, value, session="s1", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, freshness=UsageFreshness.FRESH, total=None):
        return UsageObservation(provider="fake", kind=kind, unit="turns", freshness=freshness, value=value, total=total,
                                native_session_id=session)

    def test_deltas_against_the_same_session_not_the_cumulative_figure(self):
        self.assertEqual(budget.record_usage(self.store, self.obs(10), None), 10.0)
        self.assertEqual(budget.record_usage(self.store, self.obs(13), None), 3.0)
        self.assertEqual(budget.record_usage(self.store, self.obs(2), None), 0.0)     # a reset is not negative
        self.assertEqual(budget.record_usage(self.store, self.obs(5, session="s2"), None), 5.0)
        self.assertEqual(budget.record_usage(self.store, self.obs(100, kind=UsageKind.ESTIMATED_API_VALUE), None), 100.0)

    def test_unavailable_observation_records_no_delta(self):
        self.assertIsNone(budget.record_usage(self.store, self.obs(None, freshness=UsageFreshness.UNAVAILABLE), None))
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM usage_observations")["n"], 1)

    def test_kinds_are_never_summed(self):
        budget.record_usage(self.store, self.obs(10), None)
        budget.record_usage(self.store, self.obs(4, kind=UsageKind.PAID_SPEND), None)
        rows = self.store.rows("SELECT kind, SUM(delta) AS d FROM usage_observations GROUP BY kind ORDER BY kind")
        self.assertEqual([(r["kind"], r["d"]) for r in rows], [(UsageKind.PAID_SPEND, 4.0), (UsageKind.SUBSCRIPTION_ALLOWANCE, 10.0)])


class AllowanceDecision(ControllerCase):
    def setUp(self):
        super().setUp()
        self.make_controller()

    def latest(self, used, total, freshness=UsageFreshness.FRESH):
        budget.record_usage(self.store, UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                                         freshness=freshness, value=used, total=total), None)
        return budget.latest_allowance(self.store, "fake")

    def test_fresh_allowance_below_the_interactive_reserve_refuses(self):
        # total 100, reserve 25 -> 75 usable; used 60 leaves 15 usable, below a 20-turn invocation
        ok, why = budget.allowance_decision(self.policy, self.latest(60, 100), 20)
        self.assertFalse(ok)
        self.assertEqual(why, StopReason.ALLOWANCE_RESERVE)

    def test_fresh_allowance_above_the_reserve_permits(self):
        ok, why = budget.allowance_decision(self.policy, self.latest(50, 100), 20)
        self.assertTrue(ok)

    def test_unavailable_allowance_follows_policy(self):
        ok, why = budget.allowance_decision(self.policy, None, 20)
        self.assertTrue(ok)
        self.assertIn("task-ceiling-only", why)
        raw = example_policy_dict()
        raw["funding"]["unobservable_allowance"] = "wait"
        waiting = parse_policy(json.dumps(raw))
        ok, why = budget.allowance_decision(waiting, None, 20)
        self.assertFalse(ok)
        self.assertEqual(why, StopReason.ALLOWANCE_UNOBSERVABLE)

    def test_stale_allowance_is_not_trusted_as_fresh(self):
        raw = example_policy_dict()
        raw["funding"]["unobservable_allowance"] = "wait"
        waiting = parse_policy(json.dumps(raw))
        ok, why = budget.allowance_decision(waiting, self.latest(0, 100, freshness=UsageFreshness.STALE), 20)
        self.assertFalse(ok)


if __name__ == "__main__":
    unittest.main()
