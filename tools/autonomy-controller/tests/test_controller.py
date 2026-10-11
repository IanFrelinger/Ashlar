import hashlib
import json
import sqlite3
import subprocess
import sys
import unittest
from pathlib import Path

from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.models import ReconcileResult, RunState, RunStatus, StopReason, TaskState
from ashlar_controller.store import Store

from .helpers import ControllerCase


def finished(consumed=3.0, **kw):
    return RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed_units=consumed, **kw)


def failed(reason, **kw):
    return RunStatus(state=RunState.FAILED, stop_reason=reason, **kw)


class EmptyQueue(ControllerCase):
    def test_empty_queue_is_idle_with_zero_adapter_calls(self):
        ctl = self.make_controller()
        for _ in range(5):
            r = ctl.tick()
            self.assertEqual((r.action, r.reason), ("idle", StopReason.EMPTY_QUEUE))
        self.assertEqual(self.adapter_calls(), 0)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs")["n"], 0)

    def test_waiting_task_before_its_time_makes_no_adapter_call(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.set_task_state("t1", TaskState.WAITING_QUOTA, stop_reason=StopReason.QUOTA_EXHAUSTED, next_eligible_in=3600)
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(self.adapter_calls(), 0)


class DryRun(ControllerCase):
    def digest(self):
        """Bytes of the main file plus a logical dump: the -shm side file is created by any WAL
        reader, so it is not evidence of a write; the dump is."""
        h = hashlib.sha256(Path(self.state_path).read_bytes())
        conn = sqlite3.connect(f"file:{self.state_path}?mode=ro", uri=True)
        for line in conn.iterdump():
            h.update(line.encode("utf-8"))
        conn.close()
        return h.hexdigest()

    def test_dry_run_reads_only_and_touches_no_adapter(self):
        ctl = self.make_controller()
        self.add_task()
        self.store.close()
        before = self.digest()
        ro = Store.open(self.state_path, read_only=True)
        self.addCleanup(ro.close)
        adapter = FakeAdapter()
        dry = self.make_controller(store=ro, adapter=adapter, holder="dry")
        r = dry.tick(dry_run=True)
        self.assertEqual((r.action, r.reason, r.task_id), ("would-launch", StopReason.DRY_RUN, "t1"))
        self.assertEqual(r.details["reservation"], 20.0)
        self.assertEqual(adapter.launch_count + adapter.poll_count + adapter.reconcile_count + adapter.usage_count, 0)
        self.assertEqual(self.digest(), before)
        self.assertEqual(ro.row("SELECT COUNT(*) AS n FROM runs")["n"], 0)
        self.assertIsNone(ro.lease_row())

    def test_dry_run_reports_blocks_without_changing_them(self):
        ctl = self.make_controller()
        self.add_task()
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        ctl.tick()
        r = ctl.tick(dry_run=True)
        self.assertEqual((r.action, r.reason), ("would-block", StopReason.WRITER_BUSY))
        self.assertEqual(self.adapter.launch_count, 1)


class PauseResume(ControllerCase):
    def test_pause_stops_launching_and_resume_allows_it(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.pause("operator review")
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("paused", StopReason.PAUSED))
        self.assertEqual(r.details["reason"], "operator review")
        self.assertEqual(self.adapter_calls(), 0)
        ctl.resume()
        self.assertEqual(ctl.tick().action, "launched")


class QuotaAndRateLimits(ControllerCase):
    def test_quota_exhaustion_waits_and_never_launches_a_fallback(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.QUOTA_EXHAUSTED, consumed_units=0.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        self.assertEqual(ctl.tick().action, "launched")
        r = ctl.tick()
        self.assertEqual(r.settled[0]["stop_reason"], StopReason.QUOTA_EXHAUSTED)
        t = self.task()
        self.assertEqual(t["state"], TaskState.WAITING_QUOTA)
        self.assertIsNotNone(t["next_eligible_at"])
        for _ in range(3):
            self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(adapter.launch_count, 1)
        self.assertEqual(self.pool()["reserved"], 0.0)
        # No run ever goes to a paid route: every run so far funds from the subscription pool.
        pools = {r["funding_pool"] for r in self.store.rows("SELECT funding_pool FROM runs")}
        self.assertEqual(pools, {"fake-pool"})
        self.clock.advance(self.policy.idle["quota_wait_seconds"] + 1)
        self.assertEqual(ctl.tick().action, "launched")
        self.assertEqual(adapter.launch_count, 2)
        self.assertEqual(self.run_row(self.store.row("SELECT run_id FROM runs ORDER BY reserved_at DESC LIMIT 1")["run_id"])["provider"], "fake")

    def test_rate_limit_backs_off_within_the_budget(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.RATE_LIMITED, consumed_units=1.0)},
                                      {"launch": "ok", "status": failed(StopReason.RATE_LIMITED, consumed_units=1.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.WAITING_RATE_LIMIT)
        self.assertEqual(ctl.tick().action, "idle")
        self.clock.advance(61)
        self.assertEqual(ctl.tick().action, "launched")
        ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.WAITING_RATE_LIMIT)
        self.clock.advance(61)
        self.assertEqual(ctl.tick().action, "idle")   # second backoff is 300s
        self.clock.advance(300)
        self.assertEqual(ctl.tick().action, "launched")
        self.assertEqual(self.pool()["consumed"], 2.0)

    def test_allowance_below_reserve_waits_without_a_launch(self):
        from ashlar_controller import budget
        from ashlar_controller.models import UsageFreshness, UsageKind, UsageObservation
        ctl = self.make_controller()
        self.add_task()
        budget.record_usage(self.store, UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                                         freshness=UsageFreshness.FRESH, value=70, total=100), None)
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.ALLOWANCE_RESERVE))
        self.assertEqual(self.task()["state"], TaskState.WAITING_ALLOWANCE)
        self.assertEqual(self.adapter.launch_count, 0)


class UncertainLaunches(ControllerCase):
    def test_unknown_launch_outcome_is_investigated_before_another_launch(self):
        adapter = FakeAdapter(script=[{"launch": "unknown",
                                       "reconcile": ReconcileResult(found=True, native_run_handle="h-recovered",
                                                                    native_session_id="s-recovered",
                                                                    status=RunStatus(state=RunState.RUNNING))}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.LAUNCH_UNKNOWN))
        run = self.run_row(r.run_id)
        self.assertEqual(run["state"], RunState.UNKNOWN)
        self.assertIsNone(run["native_run_handle"])
        self.assertEqual(self.task()["state"], TaskState.NEEDS_RECONCILE)
        self.assertEqual(self.pool()["reserved"], 20.0)
        adapter.default_status = RunStatus(state=RunState.RUNNING)
        r2 = ctl.tick()
        self.assertEqual(adapter.reconcile_count, 1)
        self.assertEqual(adapter.launch_count, 1)          # no second launch while the first was unknown
        self.assertEqual((r2.action, r2.reason), ("blocked", StopReason.WRITER_BUSY))
        run = self.run_row(r.run_id)
        self.assertEqual((run["state"], run["native_run_handle"], run["native_session_id"]),
                         (RunState.RUNNING, "h-recovered", "s-recovered"))

    def test_unknown_launch_that_never_started_releases_its_reservation(self):
        adapter = FakeAdapter(script=[{"launch": "unknown", "reconcile": ReconcileResult(found=False)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r = ctl.tick()
        r2 = ctl.tick()
        self.assertEqual(r2.action, "settled")
        self.assertEqual(r2.settled[0]["stop_reason"], StopReason.LAUNCH_FAILED)
        self.assertEqual(self.pool()["reserved"], 0.0)
        self.assertEqual(self.pool()["attempts_used"], 1)   # the attempt stays counted
        self.assertEqual(adapter.launch_count, 1)
        self.assertEqual(self.task()["state"], TaskState.QUEUED)

    def test_restart_mid_launch_reconciles_before_launching(self):
        gone = subprocess.Popen([sys.executable, "-c", "pass"])
        gone.wait()
        ctl = self.make_controller(pid=gone.pid)
        self.add_task()
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        r = ctl.tick()
        # Simulate dying between "launch issued" and "handle recorded".
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state='launching', native_run_handle=NULL WHERE run_id=?", (r.run_id,))
        self.store.close()
        adapter2 = FakeAdapter(script=[])
        adapter2.handles["ghost"] = {"reconcile": ReconcileResult(found=True, native_run_handle="h2", status=finished(4.0))}
        ctl2 = self.make_controller(store=Store.open(self.state_path), adapter=adapter2, holder="holder-b", host="host-a", pid=4243)
        # holder-a died (its pid is gone) and its lease has expired: the same host may take over.
        self.clock.advance(self.policy.idle["lease_ttl_seconds"] + 1)
        r2 = ctl2.tick()
        self.assertEqual(r2.action, "settled")
        self.assertEqual(adapter2.reconcile_count, 1)
        self.assertEqual(adapter2.launch_count, 0)
        self.assertEqual(r2.settled[0]["stop_reason"], StopReason.COMPLETED)
        self.assertEqual(self.pool()["consumed"], 4.0)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs")["n"], 1)

    def test_no_final_result_parks_for_reconciliation(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.NO_FINAL_RESULT)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.NEEDS_RECONCILE)
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(adapter.launch_count, 1)
        self.assertEqual(self.pool()["consumed"], 20.0)   # unknown consumption charged at the reservation


class Cancellation(ControllerCase):
    def test_cancel_preserves_the_workspace_and_the_incomplete_run(self):
        ctl = self.make_controller()
        self.add_task()
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        r = ctl.tick()
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET workspace='/work/t1-clone' WHERE run_id=?", (r.run_id,))
        out = ctl.cancel_run(r.run_id)
        self.assertTrue(out["cancelled"])
        self.assertEqual(self.adapter.cancel_count, 1)
        run = self.run_row(r.run_id)
        self.assertEqual((run["state"], run["stop_reason"], run["workspace"]), (RunState.CANCELLED, StopReason.CANCELLED, "/work/t1-clone"))
        t = self.task()
        self.assertEqual(t["state"], TaskState.CANCELLED)
        self.assertIn("/work/t1-clone", t["blocker"])
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 1)


class FailureHandling(ControllerCase):
    def test_same_diagnosed_failure_is_retried_once_then_parked(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="build:CS0246", consumed_units=2.0)},
                                      {"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="build:CS0246", consumed_units=2.0)},
                                      {"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="build:CS0246")}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.QUEUED)
        self.assertIn("1 of 1", self.task()["blocker"])
        ctl.tick(); ctl.tick()
        t = self.task()
        self.assertEqual(t["state"], TaskState.PARKED_FAILURE)
        self.assertEqual(t["stop_reason"], StopReason.SAME_FAILURE_RETRY_EXHAUSTED)
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(adapter.launch_count, 2)

    def test_the_retry_counter_survives_a_restart(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="f1", consumed_units=1.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.store.close()
        adapter2 = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="f1", consumed_units=1.0)}])
        ctl2 = self.make_controller(store=Store.open(self.state_path), adapter=adapter2)
        ctl2.tick(); ctl2.tick()
        self.assertEqual(self.task()["state"], TaskState.PARKED_FAILURE)

    def test_auth_permission_and_decision_block_with_the_exact_blocker(self):
        cases = [(StopReason.AUTH_MISSING, TaskState.BLOCKED_AUTH, "claude login expired"),
                 (StopReason.PERMISSION_MISSING, TaskState.BLOCKED_PERMISSION, "needs repo:write"),
                 (StopReason.DECISION_MISSING, TaskState.PARKED_DECISION, "owner must pick the merge method")]
        for reason, state, blocker in cases:
            with self.subTest(reason=reason):
                adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(reason, consumed_units=1.0, result={"blocker": blocker})}])
                ctl = self.make_controller(adapter=adapter)
                self.add_task(task_id=f"t-{reason}")
                ctl.tick(); ctl.tick()
                t = self.task(f"t-{reason}")
                self.assertEqual((t["state"], t["blocker"]), (state, blocker))
                self.assertEqual(ctl.tick().action, "idle")
                self.assertEqual(adapter.launch_count, 1)
                self.store.close()
                Path(self.state_path).unlink()

    def test_definite_launch_failure_releases_the_reservation(self):
        adapter = FakeAdapter(script=[{"launch": "fail", "error": "cli not installed"}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.LAUNCH_FAILED))
        p = self.pool()
        self.assertEqual((p["reserved"], p["consumed"], p["attempts_used"]), (0.0, 0.0, 1))
        self.assertEqual(self.task()["state"], TaskState.QUEUED)


class WritersAndProviders(ControllerCase):
    def test_one_writer_across_tasks(self):
        ctl = self.make_controller()
        self.add_task("t1"); self.add_task("t2")
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        self.assertEqual(ctl.tick().action, "launched")
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.WRITER_BUSY))
        self.assertEqual(self.adapter.launch_count, 1)

    def test_disabled_provider_control_skips_its_tasks(self):
        ctl = self.make_controller()
        self.add_task()
        self.store.set_control("disabled_providers", json.dumps(["fake"]))
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 0)

    def test_status_carries_what_an_operator_needs(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.tick()
        s = ctl.status()
        t = s["tasks"][0]
        for key in ("owner", "task_id", "pr", "state", "funding_pool", "blocker"):
            self.assertIn(key, t)
        self.assertEqual(t["pool"]["attempts_remaining"], 3)
        self.assertEqual(t["funding_pool"], "fake-pool")
        self.assertFalse(s["policy"]["paid_inference_enabled"])


if __name__ == "__main__":
    unittest.main()
