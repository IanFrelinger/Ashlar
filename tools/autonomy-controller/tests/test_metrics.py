"""metrics.collect counts the ledger and render_report writes the operator's Markdown, both LLM-free.
Every ledger here is built through the Controller and the fake adapter; the test asserts the adapter
was not called by the metrics pass. Each test names the one-line mutation of metrics.py it kills."""
import json
import unittest

from ashlar_controller import budget, events, metrics
from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.controller import Controller
from ashlar_controller.models import (RunState, RunStatus, StopReason, TaskState, UsageFreshness, UsageKind,
                                      UsageObservation)
from ashlar_controller.policy import parse_policy

from .helpers import ControllerCase, example_policy_dict
from .test_pr_lifecycle import LifecycleCase, green, red

REQUIRED_TOP_KEYS = ("generated_at", "controller_id", "schema_version", "tasks", "runs", "prs", "retries", "usage",
                     "events", "journal", "controls")


def finished(consumed=3.0):
    return RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed_units=consumed)


def failed(reason, **kw):
    return RunStatus(state=RunState.FAILED, stop_reason=reason, **kw)


class CollectCounts(ControllerCase):
    # Kills: `_count_by(store, "runs", "stop_reason", ...)` -> `_count_by(store, "runs", "state", ...)`: the
    # by_stop_reason keys would be run states, not stop reasons.
    def test_by_stop_reason_after_one_completed_and_one_task_failed_run(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": finished(3.0)},
                                      {"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="build:CS1",
                                                                        consumed_units=2.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task("t1"); self.add_task("t2")
        ctl.tick(); ctl.tick()          # t1 launched, settled completed -> done
        ctl.tick(); ctl.tick()          # t2 launched, settled task-failed -> requeued
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["runs"]["by_stop_reason"], {StopReason.COMPLETED: 1, StopReason.TASK_FAILED: 1})
        self.assertEqual(m["runs"]["total"], 2)
        self.assertEqual(m["runs"]["by_state"], {RunState.FINISHED: 1, RunState.FAILED: 1})
        self.assertEqual(m["runs"]["by_role"], {"implementation": 2})
        self.assertEqual(m["tasks"], {"total": 2, "by_state": {TaskState.DONE: 1, TaskState.QUEUED: 1}})

    # Kills: `"consumed_total": runs_consumed` -> `runs_consumed + sum(estimated_by_provider.values())`: the
    # estimated API value would leak into the turns figure (3.28 instead of 3.0).
    def test_consumed_total_is_settled_turns_and_excludes_the_estimated_api_value(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.tick(); ctl.tick()          # one completed run, 3.0 turns charged
        run_id = self.store.row("SELECT run_id FROM runs")["run_id"]
        budget.record_usage(self.store, UsageObservation(provider="fake", kind=UsageKind.ESTIMATED_API_VALUE, unit="USD",
                                                         freshness=UsageFreshness.FRESH, value=0.28,
                                                         native_session_id=run_id), run_id, now=self.clock())
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["runs"]["consumed_total"], 3.0)
        kinds = {(u["provider"], u["kind"]): u for u in m["usage"]["latest"]}
        self.assertIn(("fake", UsageKind.ESTIMATED_API_VALUE), kinds)
        self.assertEqual(kinds[("fake", UsageKind.ESTIMATED_API_VALUE)]["value"], 0.28)
        self.assertEqual(m["usage"]["estimated_api_value_total_by_provider"], {"fake": 0.28})
        # The estimate never leaks into any turns figure.
        for key in ("consumed_total", "reserved_active_total", "overshoot_total"):
            self.assertNotEqual(m["runs"][key], 0.28)
            self.assertNotEqual(m["runs"][key], 3.28)

    # Kills: `ACTIVE_RUN_STATES = ("reserved", "launching", "running")` -> `("reserved", "launching")`:
    # the running run's 20.0 reservation would no longer be counted.
    def test_reserved_active_total_counts_a_running_run_and_drops_after_settlement(self):
        ctl = self.make_controller()
        self.add_task()
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        self.assertEqual(ctl.tick().action, "launched")
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["runs"]["reserved_active_total"], 20.0)
        self.assertEqual(m["runs"]["by_state"], {RunState.RUNNING: 1})
        self.assertEqual(m["runs"]["consumed_total"], 0.0)
        self.adapter.default_status = finished(3.0)
        self.assertEqual(ctl.tick().action, "settled")
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["runs"]["reserved_active_total"], 0.0)
        self.assertEqual(m["runs"]["consumed_total"], 3.0)

    # Kills: `SELECT COALESCE(SUM(overshoot), 0) FROM budget_pools` -> `SUM(reserved)`: overshoot would read 0.0,
    # not the 5.0 the 25-turn run charged above its 20-turn reservation.
    def test_overshoot_total_and_attempts_used_total_come_from_the_pools(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": finished(25.0)}])   # 5 above the 20 reservation
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["runs"]["overshoot_total"], 5.0)
        self.assertEqual(m["runs"]["consumed_total"], 25.0)
        self.assertEqual(m["runs"]["attempts_used_total"], 1)

    # Kills: `SUM(attempts_used)` -> `MAX(attempts_used)` over budget_pools: two pools with one attempt each
    # give 1, not 2.
    def test_attempts_used_total_sums_every_pool(self):
        ctl = self.make_controller()
        self.add_task("t1"); self.add_task("t2")
        ctl.tick(); ctl.tick(); ctl.tick(); ctl.tick()
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["runs"]["attempts_used_total"], 2)
        self.assertEqual(m["tasks"]["by_state"], {TaskState.DONE: 2})

    # Kills: `FROM failure_retries WHERE count > 0` -> `FROM failure_retries WHERE count > 1`: the single
    # requeue would report zero identities and zero total.
    def test_retries_total_after_a_requeued_diagnosed_failure(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="gate:shell-lint:abc",
                                                                        consumed_units=1.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        m0 = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m0["retries"], {"identities": 0, "total": 0})
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.QUEUED)
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["retries"], {"identities": 1, "total": 1})

    # Kills: `latest_usage` query losing `WHERE id IN (SELECT MAX(id) ... GROUP BY provider, kind)`: every
    # observation would be listed and the first value (70) would appear beside the latest (80).
    def test_usage_latest_is_the_newest_observation_per_provider_and_kind(self):
        self.make_controller()
        for value in (70, 80):
            budget.record_usage(self.store, UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                                             freshness=UsageFreshness.FRESH, value=value, total=100), None,
                                now=self.clock())
            self.clock.advance(1)
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(len(m["usage"]["latest"]), 1)
        u = m["usage"]["latest"][0]
        self.assertEqual((u["provider"], u["kind"], u["unit"], u["freshness"], u["value"], u["total"]),
                         ("fake", UsageKind.SUBSCRIPTION_ALLOWANCE, "turns", UsageFreshness.FRESH, 80.0, 100.0))
        self.assertEqual(m["usage"]["estimated_api_value_total_by_provider"], {})

    # Kills: `COUNT(handled_at) AS handled` -> `COUNT(*) AS handled`: the unhandled event would count as handled.
    def test_events_total_and_handled(self):
        ctl = self.make_controller()
        self.add_task()
        self.assertTrue(ctl.ingest_event("evt-1", "github", "pr-opened", "t1", {}))
        self.assertTrue(ctl.ingest_event("evt-2", "github", "checks-green", "t1", {}))
        self.assertFalse(ctl.ingest_event("evt-2", "github", "checks-green", "t1", {}))   # duplicate: no row
        events.mark_handled(self.store, "evt-1", now=self.clock())
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["events"], {"total": 2, "handled": 1})

    # Kills: `"by_kind": _count_by(store, "journal", "kind")` -> `_count_by(store, "journal", "task_id")`:
    # the keys would be task ids and `task-created` would be absent.
    def test_journal_by_kind_counts_what_the_controller_wrote(self):
        ctl = self.make_controller()
        self.add_task("t1"); self.add_task("t2")
        ctl.tick()
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["journal"]["by_kind"]["init"], 1)
        self.assertEqual(m["journal"]["by_kind"]["task-created"], 2)
        self.assertEqual(m["journal"]["by_kind"]["reserved"], 1)
        self.assertEqual(m["journal"]["by_kind"]["launched"], 1)
        self.assertEqual(m["journal"]["total"], sum(m["journal"]["by_kind"].values()))
        self.assertEqual(m["journal"]["total"], self.store.row("SELECT COUNT(*) AS n FROM journal")["n"])

    # Kills: `"paused": bool(paused)` -> `"paused": paused is None`, or the disabled list read from the
    # `paused` control: after `pause` paused must be True and the disabled list must be exactly ["fake"].
    def test_controls_reflect_pause_and_disabled_providers(self):
        ctl = self.make_controller()
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["controls"], {"paused": False, "disabled_providers": []})
        ctl.pause("operator review")
        self.store.set_control("disabled_providers", json.dumps(["fake"]), now=self.clock())
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["controls"], {"paused": True, "disabled_providers": ["fake"]})
        ctl.resume()
        self.assertFalse(metrics.collect(self.store, now=self.clock())["controls"]["paused"])

    # Kills: `"generated_at": utcnow_iso(now)` -> `utcnow_iso()` (wall clock): the fixed clock's ISO
    # string would not come back; and any top-level key dropped from the dict.
    def test_every_key_is_present_on_an_empty_ledger_and_generated_at_uses_now(self):
        self.make_controller()
        m = metrics.collect(self.store, now=1_800_000_000.0)
        for key in REQUIRED_TOP_KEYS:
            self.assertIn(key, m)
        self.assertEqual(m["generated_at"], "2027-01-15T08:00:00Z")
        self.assertEqual(m["controller_id"], "ctl-test")
        self.assertEqual(m["schema_version"], "2")
        self.assertEqual(m["tasks"], {"total": 0, "by_state": {}})
        self.assertEqual(m["runs"], {"total": 0, "by_state": {}, "by_stop_reason": {}, "by_role": {}, "consumed_total": 0.0,
                                     "reserved_active_total": 0.0, "overshoot_total": 0.0, "attempts_used_total": 0})
        self.assertEqual(m["prs"], {"total": 0, "open": 0, "merged": 0, "closed_unmerged": 0, "repair_rounds_total": 0,
                                    "by_outcome": {}})
        self.assertEqual(m["events"], {"total": 0, "handled": 0})
        self.assertEqual(m["usage"], {"latest": [], "estimated_api_value_total_by_provider": {}})

    # Kills: any adapter call added to collect (e.g. `store` replaced by a controller that polls): the
    # fake's counters must not move across collect and render_report.
    def test_collect_and_render_make_zero_adapter_calls(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.tick(); ctl.tick()
        before = self.adapter_calls()
        self.assertGreater(before, 0)
        m = metrics.collect(self.store, now=self.clock())
        text = metrics.render_report(ctl.status(), m)
        self.assertEqual(self.adapter_calls(), before)
        self.assertIn("## Metrics", text)
        import ashlar_controller.metrics as mod
        with open(mod.__file__, encoding="utf-8") as fh:
            source = fh.read()
        self.assertNotIn("adapters", source)   # LLM-free: no adapter import in the module


class CollectPrs(LifecycleCase):
    # Kills: `_count_by(store, "prs", "outcome", "outcome IS NOT NULL")` -> `_count_by(store, "prs", "state")`:
    # by_outcome's keys would be PR states ("merged", "closed"), not outcomes ("merged", "closed-unmerged").
    def test_pr_counts_after_a_merge_and_a_closed_unmerged(self):
        self.make()
        n1 = self.open_pr("t1")["pr_number"]
        self.gh.set_checks("head-1", green("head-1"))
        self.assertEqual(self.ctl.tick().reason, "merged")
        n2 = self.open_pr("t2")["pr_number"]
        m_open = metrics.collect(self.store, now=self.clock())
        self.assertEqual((m_open["prs"]["total"], m_open["prs"]["open"], m_open["prs"]["merged"]), (2, 1, 1))
        self.gh.close(n2)
        self.assertEqual(self.ctl.tick().reason, "pr-closed-unmerged")
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["prs"], {"total": 2, "open": 0, "merged": 1, "closed_unmerged": 1, "repair_rounds_total": 0,
                                    "by_outcome": {"merged": 1, "closed-unmerged": 1}})
        self.assertEqual(m["runs"]["by_stop_reason"], {StopReason.COMPLETED: 2})
        self.assertEqual(m["tasks"]["by_state"], {TaskState.DONE: 1, TaskState.PARKED_FAILURE: 1})
        self.assertNotEqual(n1, n2)

    # Kills: `SUM(repair_rounds_used)` -> `COUNT(repair_rounds_used)` over prs: one PR with one round gives 1
    # either way, so two rounds on the one PR are needed: SUM gives 2, COUNT gives 1.
    def test_repair_rounds_total_and_the_fixer_role_after_two_red_heads(self):
        self.make()
        n = self.open_pr()["pr_number"]
        for head in ("head-1", "head-2"):
            self.gh.set_checks(head, red(head))
            self.assertEqual(self.ctl.tick().reason, "repair")
            self.assertEqual(self.ctl.tick().action, "settled")
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["prs"]["repair_rounds_total"], 2)
        self.assertEqual(m["prs"]["open"], 1)
        self.assertEqual(m["runs"]["by_role"], {"implementation": 1, "readiness-fixer": 2})
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 2)


class RenderReport(ControllerCase):
    def _status_and_metrics(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.AUTH_MISSING, consumed_units=1.0,
                                                                        result={"blocker": "claude login expired; run claude auth login"})}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        ctl.tick(); ctl.tick()
        run_id = self.store.row("SELECT run_id FROM runs")["run_id"]
        budget.record_usage(self.store, UsageObservation(provider="fake", kind=UsageKind.ESTIMATED_API_VALUE, unit="USD",
                                                         freshness=UsageFreshness.FRESH, value=0.053), run_id, now=self.clock())
        return ctl, ctl.status(), metrics.collect(self.store, now=self.clock())

    # Kills: `lines.append(f"- Blocker: {t.get('blocker') or '-'}")` dropped, or
    # `f"- Attempts: {_num(used)}/{_num(amax)} ({_num(rem)} permitted)"` losing `rem`: the exact blocker text
    # and the "1/<max> (<remaining> permitted)" attempts figure must both appear under the task heading.
    def test_report_carries_the_blocker_and_the_attempts_remaining(self):
        ctl, status, m = self._status_and_metrics()
        text = metrics.render_report(status, m)
        self.assertIn("# Controller ctl-test", text.splitlines()[0])
        self.assertIn("### t1", text)
        self.assertIn("claude login expired; run claude auth login", text)
        pool = status["tasks"][0]["pool"]
        self.assertEqual((pool["attempts_used"], pool["attempts_remaining"]), (1, pool["attempts_max"] - 1))
        self.assertIn(f"Attempts: 1/{pool['attempts_max']} ({pool['attempts_remaining']} permitted)", text)
        self.assertIn("blocked-auth", text)
        self.assertIn("Funding pool: fake-pool", text)
        self.assertIn(f"Policy: {self.policy.version} ({self.policy.sha256[:12]})", text)
        self.assertLess(text.index("## Tasks"), text.index("## Metrics"))

    # Kills: `ESTIMATE_CAVEAT` line removed from the usage section (`out.append(ESTIMATE_CAVEAT)`), or the
    # by-stop-reason table losing its rows.
    def test_report_carries_the_estimate_caveat_and_the_metric_tables(self):
        ctl, status, m = self._status_and_metrics()
        text = metrics.render_report(status, m)
        self.assertIn("estimated_api_value is an estimate of API list value, not a charge", text)
        self.assertIn("| auth-missing | 1 |", text)
        self.assertIn("| fake | estimated_api_value | USD | fresh | 0.053 |", text)
        self.assertIn("Estimated API value by provider (estimate, not a charge): fake 0.053", text)
        self.assertIn("| task-created | 1 |", text)
        self.assertIn("### Retries", text)
        self.assertIn("### Journal by kind", text)

    # Kills: the `evidence` branch of `_render_task` (`if evidence:` -> `if False:`): every path the
    # integrator attaches must be printed, and None entries render as "-".
    def test_report_lists_every_evidence_path_when_present(self):
        ctl, status, m = self._status_and_metrics()
        evidence = {"run_dir": "/state/runs/run-1", "manifest": "/state/runs/run-1/manifest.json",
                    "stdout": "/state/runs/run-1/stdout.json", "stderr": "/state/runs/run-1/stderr.log",
                    "transcript": "/home/op/.claude/projects/x/sess.jsonl", "workspace": "/state/workspaces/t1",
                    "archive": None}
        status["tasks"][0]["evidence"] = evidence
        text = metrics.render_report(status, m)
        self.assertIn("- Evidence:", text)
        for key, path in evidence.items():
            if path is not None:
                self.assertIn(f"  - {key}: {path}", text)
        self.assertIn("  - archive: -", text)

    # Kills: `t.get("evidence")` -> `t["evidence"]` (KeyError on a status without it), or `t.get("pr_record")`
    # -> `t["pr_record"]` on an older status dict missing that key entirely.
    def test_report_renders_a_status_without_evidence_and_an_older_status_without_pr_record(self):
        ctl, status, m = self._status_and_metrics()
        # A status written by a PR 3 controller carries no `evidence` key; the report must still render it.
        status["tasks"][0].pop("evidence", None)
        self.assertNotIn("evidence", status["tasks"][0])
        text = metrics.render_report(status, m)
        self.assertNotIn("Evidence:", text)
        self.assertIn("### t1", text)
        older = {"controller_id": "ctl-old", "tasks": [{"task_id": "legacy", "state": "queued", "pool": {}}]}
        text2 = metrics.render_report(older, {})
        self.assertIn("# Controller ctl-old", text2)
        self.assertIn("### legacy", text2)
        self.assertIn("## Metrics", text2)
        self.assertIn("estimated_api_value is an estimate", text2)
        self.assertIn("(no tasks)", metrics.render_report({}, {}))


class PermittedAttempts(ControllerCase):
    """The live run of 2026-10-13: pool pr5-776 had 10 of 36 turns left after two capped attempts charged 13 each, an
    invocation needs 12, and both the report and `status` printed `Attempts: 2/3 (1 permitted)`. The budget permitted
    none: the third reservation was refused and the task parked. Permitted is min(attempts left, floor(remaining /
    invocation ceiling)), remaining being ceiling - consumed - reserved."""

    def parked_after_two_13_turn_attempts(self):
        def limits(raw):
            raw["limits"].update({"task_turn_ceiling": 36, "invocation_turn_ceiling": 12, "max_attempts_per_task": 3})
        self.policy_path = self.write_policy(limits, name="policy-36-12-3.json")
        adapter = FakeAdapter(script=[
            {"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="build:CS1", consumed_units=13.0)},
            {"launch": "ok", "status": failed(StopReason.TASK_FAILED, failure_identity="build:CS2", consumed_units=13.0)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        for _ in range(4):
            ctl.tick()
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.TASK_BUDGET_EXHAUSTED))
        self.assertEqual(self.task()["state"], TaskState.PARKED_BUDGET)
        self.assertIn("10 turns left (ceiling 36, consumed 26, reserved 0); an invocation needs 12", self.task()["blocker"])
        return ctl, ctl.status()

    def assert_pool_figures(self, status):
        pool = status["tasks"][0]["pool"]
        # `attempts_remaining` stays the attempts left; the invocation ceiling the reservation refuses on is beside it.
        self.assertEqual((pool["attempts_used"], pool["attempts_max"], pool["attempts_remaining"], pool["remaining"],
                          pool.get("invocation_ceiling")), (2, 3, 1, 10.0, 12.0))

    # Kills: `_render_task` printing the attempts left (`attempts_remaining`) as the permitted figure.
    def test_report_shows_no_permitted_attempt_when_the_budget_left_is_below_one_invocation(self):
        ctl, status = self.parked_after_two_13_turn_attempts()
        text = metrics.render_report(status, metrics.collect(self.store, now=self.clock()))
        attempts = [line for line in text.splitlines() if line.startswith("- Attempts:")]
        self.assertEqual(attempts, ["- Attempts: 2/3 (0 permitted)"])
        self.assert_pool_figures(status)

    # Kills: the `status` text line printing `p['attempts_remaining']` as the permitted figure.
    def test_status_text_shows_no_permitted_attempt_when_the_budget_left_is_below_one_invocation(self):
        from ashlar_controller.cli import _format_status
        ctl, status = self.parked_after_two_13_turn_attempts()
        budget_line = [line.strip() for line in _format_status(status).splitlines() if "attempts " in line]
        self.assertEqual(len(budget_line), 1, budget_line)
        self.assertIn("attempts 2/3 (0 permitted)", budget_line[0])
        self.assert_pool_figures(status)

    # Kills: the status pool's invocation ceiling read from the pool alone. `budget.reserve` needs the smaller of the
    # pool's frozen ceiling and the current policy's, so under a policy tightened to 10 the 10 turns left cover one
    # invocation: 1 permitted, where the pool's own 12 would say 0.
    def test_a_tightened_policy_ceiling_is_the_one_the_permitted_figure_uses(self):
        self.parked_after_two_13_turn_attempts()
        raw = example_policy_dict()
        raw.pop("_comment", None)
        raw["policy_version"] = "2026-10-13.tightened-10"
        raw["limits"].update({"task_turn_ceiling": 36, "invocation_turn_ceiling": 10, "max_attempts_per_task": 3})
        tighter = parse_policy(json.dumps(raw), source="operator-tightened")
        ctl = Controller(self.store, tighter, {"fake": self.adapter}, holder_id="holder-a", host="host-a", pid=4242,
                         clock=self.clock)
        pool = ctl.status()["tasks"][0]["pool"]
        self.assertEqual((pool["remaining"], pool["invocation_ceiling"], metrics.permitted_attempts(pool)), (10.0, 10.0, 1))

    def other_policy(self, version, **limits):
        raw = example_policy_dict()
        raw.pop("_comment", None)
        raw["policy_version"] = version
        raw["limits"].update({"task_turn_ceiling": 36, "invocation_turn_ceiling": 12, "max_attempts_per_task": 3})
        raw["limits"].update(limits)
        return Controller(self.store, parse_policy(json.dumps(raw), source=version), {"fake": self.adapter},
                          holder_id="holder-a", host="host-a", pid=4242, clock=self.clock)

    # Kills: the status pool's invocation ceiling read from the policy alone (lane O's review): a policy that raises
    # the ceiling over a pool frozen at 12 must not let the 10 turns left look like an invocation.
    def test_a_raised_policy_ceiling_does_not_raise_the_pools_frozen_one(self):
        self.parked_after_two_13_turn_attempts()
        pool = self.other_policy("2026-10-13.raised-20", invocation_turn_ceiling=20).status()["tasks"][0]["pool"]
        self.assertEqual((pool["invocation_ceiling"], metrics.permitted_attempts(pool)), (12.0, 0))

    # Kills: `attempts_remaining` from the pool's attempts_max alone (lane O's review): `budget.reserve` caps it by
    # the policy's `max_attempts_per_task`, so under a policy tightened to 2 a pool that used 1 of 3 has 1 left, and
    # one that used 2 has none.
    def test_a_tightened_policy_attempts_max_is_the_one_the_attempts_left_use(self):
        def limits(raw):
            raw["limits"].update({"task_turn_ceiling": 36, "invocation_turn_ceiling": 12, "max_attempts_per_task": 3})
        self.policy_path = self.write_policy(limits, name="policy-36-12-3b.json")
        self.adapter = FakeAdapter(script=[{"launch": "ok", "status": failed(StopReason.TASK_FAILED,
                                                                            failure_identity="build:CS1", consumed_units=5.0)}])
        ctl = self.make_controller(adapter=self.adapter)
        self.add_task()
        ctl.tick()
        ctl.tick()
        self.assertEqual(self.pool()["attempts_used"], 1)
        pool = self.other_policy("2026-10-13.attempts-2", max_attempts_per_task=2).status()["tasks"][0]["pool"]
        self.assertEqual((pool["attempts_max"], pool["attempts_remaining"], metrics.permitted_attempts(pool)), (3, 1, 1))
        pool = self.other_policy("2026-10-13.attempts-1", max_attempts_per_task=1).status()["tasks"][0]["pool"]
        self.assertEqual((pool["attempts_remaining"], metrics.permitted_attempts(pool)), (0, 0))

    # Kills: dropping either bound of the min, or rounding the budget quotient up instead of down.
    def test_permitted_is_the_smaller_of_the_attempts_left_and_the_whole_invocations_left(self):
        def pool(left, remaining, invocation):
            return {"attempts_remaining": left, "remaining": remaining, "invocation_ceiling": invocation}
        cases = (("10 left, 12 needed, 1 attempt left", pool(1, 10.0, 12.0), 0),
                 ("budget binds", pool(3, 24.0, 12.0), 2),
                 ("attempts bind", pool(1, 36.0, 12.0), 1),
                 ("23 left is one invocation", pool(3, 23.0, 12.0), 1),
                 ("overshoot past the ceiling", pool(2, -3.0, 12.0), 0),
                 ("no attempt left", pool(0, 36.0, 12.0), 0))
        for name, p, expected in cases:
            with self.subTest(name):
                self.assertEqual(metrics.permitted_attempts(p), expected)
        # A status written before the invocation ceiling was in it: the attempts alone, as before.
        self.assertEqual(metrics.permitted_attempts({"attempts_used": 1, "attempts_max": 3, "remaining": 10.0}), 2)
        self.assertIsNone(metrics.permitted_attempts({}))


class RenderReportPr(LifecycleCase):
    # Kills: the `PR #` line of `_render_task` (`if pr:` -> `if False:`): the PR number, its state and outcome
    # and the twelve-character merge SHA must appear; the last-run line carries the run id and session.
    def test_report_carries_the_pr_record_and_the_last_run(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", green("head-1"))
        self.assertEqual(self.ctl.tick().reason, "merged")
        pr = self.pr_row(n)
        status = self.ctl.status()
        text = metrics.render_report(status, metrics.collect(self.store, now=self.clock()))
        # A merged PR's state and outcome are the same word, printed once (the PR 4 pilot's report read
        # "PR #777 merged merged"). Kills: `label = state if outcome == state else ...` -> always the pair.
        self.assertIn(f"- PR #{n} merged {pr['head_sha'][:12]} {pr['merge_sha'][:12]} rounds 0", text)
        self.assertNotIn("merged merged", text)
        run = self.store.row("SELECT * FROM runs")
        self.assertIn(f"- Last run: {run['run_id']} finished completed {run['model']}/{run['effort']} session fake-session-1", text)
        self.assertIn("| merged | 1 |", text)
        self.assertIn("PRs 1; open 0; merged 1; closed unmerged 0; repair rounds 0", text)


if __name__ == "__main__":
    unittest.main()
