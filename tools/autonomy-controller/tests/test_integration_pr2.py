"""PR 2 controller integration: adapter refusals, the turn-ceiling resume, usage observations and
the readiness evaluation. Everything here runs over the fake adapter; nothing imports the Claude
adapter or the readiness module, so these tests prove the controller's side of the contract alone."""
import json
import os
import subprocess
import sys
import unittest
from types import SimpleNamespace

from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.models import (LaunchRefused, RunState, RunStatus, StopReason, TaskState, UsageFreshness,
                                      UsageKind, UsageObservation)
from ashlar_controller.store import Store

from .helpers import ControllerCase, PACKAGE_ROOT

CLI = PACKAGE_ROOT / "controller.py"


class RefusingAdapter(FakeAdapter):
    """A fake whose script entries may say {"launch": "refuse", "stop_reason": ..., "blocker": ...}."""

    def launch(self, request):
        entry = self.script[0] if self.script else {}
        if entry.get("launch") == "refuse":
            self.script.pop(0)
            self.launch_count += 1
            self.requests.append(request)
            raise LaunchRefused(entry["stop_reason"], entry["blocker"])
        return super().launch(request)


def failed(reason, **kw):
    return RunStatus(state=RunState.FAILED, stop_reason=reason, **kw)


def report(*identities, source="repo-gates", commit="abc1234", passed=("tests/scripts/a.test.sh",)):
    failures = [SimpleNamespace(gate="repo-gates", identity=i, evidence=f"::error::{i} failed (exit 1)") for i in identities]
    return SimpleNamespace(source=source, commit=commit, failures=failures, passed=list(passed), raw={})


class LaunchRefusals(ControllerCase):
    def refuse(self, reason, blocker):
        adapter = RefusingAdapter(script=[{"launch": "refuse", "stop_reason": reason, "blocker": blocker}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        return ctl, adapter, ctl.tick()

    def test_auth_missing_refusal_blocks_auth_with_the_exact_blocker(self):
        ctl, adapter, r = self.refuse(StopReason.AUTH_MISSING, "Not logged in; run claude auth login")
        self.assertEqual((r.action, r.reason, r.task_id), ("blocked", StopReason.AUTH_MISSING, "t1"))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"], t["blocker"]),
                         (TaskState.BLOCKED_AUTH, StopReason.AUTH_MISSING, "Not logged in; run claude auth login"))
        run = self.run_row(r.run_id)
        self.assertEqual((run["state"], run["stop_reason"], run["consumed"]), (RunState.FAILED, StopReason.AUTH_MISSING, 0.0))
        self.assertTrue(json.loads(run["result_json"])["refused"])
        p = self.pool()
        self.assertEqual((p["reserved"], p["consumed"], p["attempts_used"]), (0.0, 0.0, 1))
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(adapter.launch_count, 1)
        kinds = [r["kind"] for r in self.store.rows("SELECT kind FROM journal WHERE run_id=?", (r.run_id,))]
        self.assertIn("launch-refused", kinds)

    def test_funding_route_mismatch_refusal_blocks_auth(self):
        ctl, adapter, r = self.refuse(StopReason.FUNDING_ROUTE_MISMATCH, "api-key would be billed; policy selected claude.ai")
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.FUNDING_ROUTE_MISMATCH))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.BLOCKED_AUTH, StopReason.FUNDING_ROUTE_MISMATCH))
        self.assertIn("api-key would be billed", t["blocker"])
        self.assertEqual(self.run_row(r.run_id)["stop_reason"], StopReason.FUNDING_ROUTE_MISMATCH)
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(adapter.launch_count, 1)

    def test_model_unrecognized_refusal_parks_for_a_decision(self):
        ctl, adapter, r = self.refuse(StopReason.MODEL_UNRECOGNIZED, "model claude-sonnet-5-5 is not recognised by the CLI")
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.MODEL_UNRECOGNIZED))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.PARKED_DECISION, StopReason.MODEL_UNRECOGNIZED))
        self.assertIn("claude-sonnet-5-5", t["blocker"])
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(adapter.launch_count, 1)

    def test_other_refusal_requeues_as_launch_failed(self):
        ctl, adapter, r = self.refuse(StopReason.LAUNCH_FAILED, "speed fast is not supported by the Claude CLI; only standard")
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.LAUNCH_FAILED))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.QUEUED, StopReason.LAUNCH_FAILED))
        self.assertIn("only standard", t["blocker"])
        self.assertEqual(self.pool()["reserved"], 0.0)


class TurnCeilingResume(ControllerCase):
    def test_invocation_ceiling_requeues_without_a_retry_bump_and_resumes_exactly(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.INVOCATION_CEILING, consumed_units=20.0)},
                                      {"launch": "ok", "status": failed(StopReason.INVOCATION_CEILING, consumed_units=20.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r1 = ctl.tick()
        self.assertEqual(r1.action, "launched")
        self.assertIsNone(adapter.requests[0].resume_session_id)
        r = ctl.tick()
        self.assertEqual(r.action, "settled")
        self.assertEqual(r.settled[0]["stop_reason"], StopReason.INVOCATION_CEILING)
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.QUEUED, StopReason.INVOCATION_CEILING))
        self.assertEqual(t["blocker"], "turn ceiling reached; next attempt resumes session fake-session-1")
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM failure_retries")["n"], 0)
        self.assertEqual(self.pool()["consumed"], 20.0)
        r2 = ctl.tick()
        self.assertEqual(r2.action, "launched")
        self.assertEqual(adapter.launch_count, 2)
        self.assertEqual(adapter.requests[1].resume_session_id, "fake-session-1")
        kinds = [row["kind"] for row in self.store.rows("SELECT kind FROM journal WHERE run_id=?", (r2.run_id,))]
        self.assertIn("resume", kinds)
        # A second ceiling resumes the session the second run recorded, not the first.
        ctl.tick()
        self.assertEqual(self.task()["blocker"], "turn ceiling reached; next attempt resumes session fake-session-2")
        ctl.tick()
        self.assertEqual(adapter.requests[2].resume_session_id, "fake-session-2")

    def test_resume_is_exact_across_a_restart(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.INVOCATION_CEILING, consumed_units=5.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.store.close()
        adapter2 = FakeAdapter()
        ctl2 = self.make_controller(store=Store.open(self.state_path), adapter=adapter2)
        self.assertEqual(ctl2.tick().action, "launched")
        self.assertEqual(adapter2.requests[0].resume_session_id, "fake-session-1")

    def test_a_completed_or_failed_run_does_not_resume(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="x", consumed_units=1.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.QUEUED)
        ctl.tick()
        self.assertEqual(adapter.launch_count, 2)
        self.assertIsNone(adapter.requests[1].resume_session_id)

    def test_the_turn_ceiling_is_bounded_by_the_pool(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.INVOCATION_CEILING, consumed_units=20.0)}
                                      for _ in range(6)])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        for _ in range(12):
            ctl.tick()
        # task ceiling 80 at 20 per invocation: four launches, then the pool refuses.
        self.assertEqual(adapter.launch_count, 4)
        self.assertEqual(self.task()["state"], TaskState.PARKED_BUDGET)


class Observations(ControllerCase):
    def test_every_observation_is_recorded_by_kind_and_session(self):
        allowance = UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                     freshness=UsageFreshness.UNAVAILABLE, native_session_id="fake-session-1")
        estimate = UsageObservation(provider="fake", kind=UsageKind.ESTIMATED_API_VALUE, unit="usd",
                                    freshness=UsageFreshness.FRESH, value=0.42, native_session_id="fake-session-1",
                                    raw={"input_tokens": 10})
        status = RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed_units=3.0,
                           usage=estimate, observations=[allowance])
        adapter = FakeAdapter(script=[{"launch": "ok", "status": status}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r1 = ctl.tick(); ctl.tick()
        rows = [dict(x) for x in self.store.rows("SELECT * FROM usage_observations WHERE run_id=? ORDER BY id", (r1.run_id,))]
        self.assertEqual([x["kind"] for x in rows], [UsageKind.ESTIMATED_API_VALUE, UsageKind.SUBSCRIPTION_ALLOWANCE])
        self.assertEqual(rows[0]["delta"], 0.42)
        self.assertEqual(json.loads(rows[0]["raw"]), {"input_tokens": 10})
        self.assertEqual((rows[1]["freshness"], rows[1]["value"], rows[1]["delta"]), (UsageFreshness.UNAVAILABLE, None, None))
        # The estimate never stands in for consumption when the adapter reported turns.
        self.assertEqual(self.pool()["consumed"], 3.0)

    def test_a_second_observation_of_the_same_session_is_stored_as_a_delta(self):
        def est(v):
            return UsageObservation(provider="fake", kind=UsageKind.ESTIMATED_API_VALUE, unit="usd",
                                    freshness=UsageFreshness.FRESH, value=v, native_session_id="s")
        adapter = FakeAdapter(script=[
            {"launch": "ok", "status": RunStatus(state=RunState.FAILED, stop_reason=StopReason.INVOCATION_CEILING,
                                                 consumed_units=20.0, observations=[est(1.0)])},
            {"launch": "ok", "status": RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED,
                                                 consumed_units=2.0, observations=[est(1.5)])}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        for _ in range(4):
            ctl.tick()
        deltas = [x["delta"] for x in self.store.rows("SELECT delta FROM usage_observations WHERE kind=? ORDER BY id",
                                                      (UsageKind.ESTIMATED_API_VALUE,))]
        self.assertEqual(deltas, [1.0, 0.5])


class PercentAllowance(ControllerCase):
    """The Claude CLI reports its five-hour window as a utilization fraction, recorded as `percent`
    of 100. Turns cannot be compared with a percentage, so the decision is on the unused share."""

    def obs(self, value, unit="percent", total=100.0, freshness=UsageFreshness.FRESH):
        return {"freshness": freshness, "value": value, "total": total, "unit": unit}

    def test_percent_allowance_permits_while_the_unused_share_exceeds_the_reserve(self):
        from ashlar_controller import budget
        policy = self.load()   # interactive_reserve_fraction 0.25
        self.assertEqual(budget.allowance_decision(policy, self.obs(69.0), 20.0), (True, "allowance-ok:percent"))
        self.assertEqual(budget.allowance_decision(policy, self.obs(74.99), 20.0), (True, "allowance-ok:percent"))
        # 20 turns would refuse a 50-turn allowance in the pool's unit; in percent the ceiling is not consulted.
        self.assertEqual(budget.allowance_decision(policy, self.obs(60.0), 1000.0), (True, "allowance-ok:percent"))

    def test_percent_allowance_refuses_at_or_inside_the_reserve(self):
        from ashlar_controller import budget
        policy = self.load()
        self.assertEqual(budget.allowance_decision(policy, self.obs(75.0), 1.0), (False, StopReason.ALLOWANCE_RESERVE))
        self.assertEqual(budget.allowance_decision(policy, self.obs(100.0), 1.0), (False, StopReason.ALLOWANCE_RESERVE))
        self.assertEqual(budget.allowance_decision(policy, self.obs(10.0, total=0.0), 1.0), (False, StopReason.ALLOWANCE_UNOBSERVABLE))

    def test_own_unit_allowance_keeps_the_turn_arithmetic(self):
        from ashlar_controller import budget
        policy = self.load()
        # 100 turns, 60 used: 40 left minus a 25-turn reserve leaves 15, short of a 20-turn invocation.
        self.assertEqual(budget.allowance_decision(policy, self.obs(60.0, unit="turns"), 20.0), (False, StopReason.ALLOWANCE_RESERVE))
        self.assertEqual(budget.allowance_decision(policy, self.obs(60.0, unit="turns"), 15.0), (True, "allowance-ok"))
        self.assertEqual(budget.allowance_decision(policy, self.obs(60.0, unit="turns", freshness=UsageFreshness.STALE), 15.0),
                         (True, "allowance-stale:task-ceiling-only"))

    def test_a_percent_allowance_inside_the_reserve_blocks_the_next_launch(self):
        from ashlar_controller import budget
        ctl = self.make_controller()
        self.add_task()
        budget.record_usage(self.store, UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="percent",
                                                         freshness=UsageFreshness.FRESH, value=80.0, total=100.0), None)
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.ALLOWANCE_RESERVE))
        self.assertEqual(self.adapter.launch_count, 0)


class AddendumShapedObservations(ControllerCase):
    """What the Claude adapter reports after the live probe: consumed_units=num_turns, usage=None,
    and two observations (the allowance in percent by session, the cost estimate in USD by run)."""

    def status_for(self, run_id, num_turns, cost, utilization, session="s-1", reason=StopReason.COMPLETED):
        allowance = UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="percent",
                                     freshness=UsageFreshness.FRESH, value=round(utilization * 100, 2), total=100.0,
                                     native_session_id=session, raw={"rateLimitType": "five_hour"})
        estimate = UsageObservation(provider="fake", kind=UsageKind.ESTIMATED_API_VALUE, unit="usd",
                                    freshness=UsageFreshness.FRESH, value=cost, native_session_id=run_id)
        state = RunState.FINISHED if reason == StopReason.COMPLETED else RunState.FAILED
        return RunStatus(state=state, stop_reason=reason, consumed_units=float(num_turns), usage=None,
                         observations=[allowance, estimate])

    def test_both_observations_are_recorded_and_status_shows_the_latest_per_kind(self):
        adapter = FakeAdapter(script=[
            {"launch": "ok", "status": self.status_for("r1", 1, 0.2817, 0.69, reason=StopReason.INVOCATION_CEILING)},
            {"launch": "ok", "status": self.status_for("r2", 1, 0.0144, 0.71)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        for _ in range(4):
            ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.DONE)
        self.assertEqual(self.pool()["consumed"], 2.0)   # num_turns per invocation, never the dollar figure
        rows = [dict(x) for x in self.store.rows("SELECT kind, unit, value, delta, native_session_id FROM usage_observations ORDER BY id")]
        self.assertEqual([(x["kind"], x["unit"], x["value"]) for x in rows],
                         [(UsageKind.SUBSCRIPTION_ALLOWANCE, "percent", 69.0), (UsageKind.ESTIMATED_API_VALUE, "usd", 0.2817),
                          (UsageKind.SUBSCRIPTION_ALLOWANCE, "percent", 71.0), (UsageKind.ESTIMATED_API_VALUE, "usd", 0.0144)])
        # Keyed by run id, each cost delta is the per-invocation value itself.
        self.assertEqual([x["delta"] for x in rows if x["kind"] == UsageKind.ESTIMATED_API_VALUE], [0.2817, 0.0144])
        usage = {u["kind"]: u for u in ctl.status()["usage"]}
        self.assertEqual((usage[UsageKind.SUBSCRIPTION_ALLOWANCE]["value"], usage[UsageKind.SUBSCRIPTION_ALLOWANCE]["unit"]), (71.0, "percent"))
        self.assertEqual(usage[UsageKind.ESTIMATED_API_VALUE]["value"], 0.0144)
        self.assertEqual(len(ctl.status()["usage"]), 2)

    def test_a_fresh_percent_allowance_over_the_reserve_still_launches(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": self.status_for("r1", 1, 0.01, 0.69, reason=StopReason.INVOCATION_CEILING)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(ctl.tick().action, "launched")


class ReadinessEvaluation(ControllerCase):
    def test_no_failures_completes_the_task(self):
        ctl = self.make_controller()
        self.add_task()
        out = ctl.evaluate_readiness("t1", report())
        self.assertEqual(out["action"], "completed")
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.DONE, StopReason.COMPLETED))
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 0)
        row = self.store.row("SELECT detail FROM journal WHERE kind='readiness-evaluated' AND task_id='t1'")
        self.assertEqual(json.loads(row["detail"])["passed"], ["tests/scripts/a.test.sh"])

    def test_same_gate_failure_is_retried_once_then_parked(self):
        ctl = self.make_controller()
        self.add_task()
        out = ctl.evaluate_readiness("t1", report("gate:repo-gates:deadbeef0001", "gate:repo-gates:deadbeef0002"))
        self.assertEqual((out["action"], out["identity"], out["retry"]), ("requeued", "gate:repo-gates:deadbeef0001", 1))
        self.assertEqual(len(out["failures"]), 2)
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.QUEUED, StopReason.TASK_FAILED))
        self.assertIn("1 of 1", t["blocker"])
        # A second cycle with the same identity: the counter says no.
        out = ctl.evaluate_readiness("t1", report("gate:repo-gates:deadbeef0001"))
        self.assertEqual(out["action"], "parked")
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.PARKED_FAILURE, StopReason.SAME_FAILURE_RETRY_EXHAUSTED))
        self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 0)

    def test_a_different_failure_identity_gets_its_own_retry(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.evaluate_readiness("t1", report("gate:repo-gates:aaaaaaaaaaaa"))
        out = ctl.evaluate_readiness("t1", report("gate:repo-gates:bbbbbbbbbbbb"))
        self.assertEqual(out["action"], "requeued")
        self.assertEqual(self.task()["state"], TaskState.QUEUED)

    def test_the_readiness_identity_survives_a_restart(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.evaluate_readiness("t1", report("gate:container-tests:0123456789ab"))
        self.store.close()
        ctl2 = self.make_controller(store=Store.open(self.state_path), adapter=FakeAdapter())
        out = ctl2.evaluate_readiness("t1", report("gate:container-tests:0123456789ab"))
        self.assertEqual(out["action"], "parked")
        self.assertEqual(self.task()["state"], TaskState.PARKED_FAILURE)

    def test_the_readiness_counter_is_shared_with_run_failures(self):
        # A run that failed with identity X, then a gate report with identity X: one retry in total.
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="gate:repo-gates:shared0000", consumed_units=1.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.QUEUED)
        out = ctl.evaluate_readiness("t1", report("gate:repo-gates:shared0000"))
        self.assertEqual(out["action"], "parked")

    def test_unknown_task_is_refused(self):
        from ashlar_controller.models import PolicyError
        ctl = self.make_controller()
        with self.assertRaises(PolicyError):
            ctl.evaluate_readiness("nope", report())


class CommandLinePr2(ControllerCase):
    def cli(self, *args, expect=0):
        cmd = [sys.executable, str(CLI), "--state", self.state_path, "--policy", self.policy_path,
               "--holder", "cli-test", "--json", *args]
        env = dict(os.environ, PYTHONWARNINGS="ignore")
        p = subprocess.run(cmd, capture_output=True, text=True, env=env, timeout=120)
        self.assertEqual(p.returncode, expect, f"{' '.join(args)}\nstdout: {p.stdout}\nstderr: {p.stderr}")
        return json.loads(p.stdout) if p.stdout.strip().startswith(("{", "[")) else p.stdout, p.stderr

    def test_tick_accepts_the_adapter_flag_and_defaults_to_fake(self):
        self.cli("init")
        self.cli("task", "add", "--task-id", "t1", "--card", "755", "--workstream", "budgeted-autonomy", "--provider", "fake")
        out, _ = self.cli("tick", "--adapter", "fake")
        self.assertEqual(out["action"], "launched")
        # PR 5 made codex and cursor valid choices; a name no adapter has is still a usage error.
        _, err = self.cli("tick", "--adapter", "nosuch", expect=2)
        self.assertIn("invalid choice", err)

    def test_readiness_evaluate_applies_a_saved_report(self):
        self.cli("init")
        self.cli("task", "add", "--task-id", "t1", "--card", "755", "--workstream", "budgeted-autonomy", "--provider", "fake")
        rep = self.tmp / "report.json"
        rep.write_text(json.dumps({"report": {"source": "repo-gates", "commit": "abc", "passed": [],
                                              "failures": [{"gate": "repo-gates", "identity": "gate:repo-gates:cli000000000",
                                                            "evidence": "::error::x failed (exit 1)"}], "raw": {}}}),
                       encoding="utf-8")
        out, _ = self.cli("readiness", "evaluate", "--task-id", "t1", "--report", str(rep))
        self.assertEqual((out["action"], out["retry"]), ("requeued", 1))
        out, _ = self.cli("readiness", "evaluate", "--task-id", "t1", "--report", str(rep))
        self.assertEqual(out["action"], "parked")
        out, _ = self.cli("task", "show", "t1")
        self.assertEqual(out["state"], TaskState.PARKED_FAILURE)
        # A bare report (no "report" wrapper) with no failures completes the task.
        rep.write_text(json.dumps({"source": "repo-gates", "commit": "abc", "passed": ["a"], "failures": []}), encoding="utf-8")
        out, _ = self.cli("readiness", "evaluate", "--task-id", "t1", "--report", str(rep))
        self.assertEqual(out["action"], "completed")


if __name__ == "__main__":
    unittest.main()
