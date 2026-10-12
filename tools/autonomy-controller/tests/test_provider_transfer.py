"""PR 5 acceptance for the bounded provider transfer, over two fake adapters (`fake` and `fake2`), a fake GitHub
and a fake git: a transfer keeps the task, its PR, its attempts and its budget pool; the previous writer is stopped
and settled before the new provider launches; `limits.provider_transfers_per_pr` bounds it and the count is written
before any launch; the new provider never resumes another provider's session; a refusal changes nothing."""
import json
import os
import subprocess
import sys
import unittest

from ashlar_controller import cli as cli_mod
from ashlar_controller import metrics
from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.controller import Controller
from ashlar_controller.models import (BudgetExhausted, ControllerError, PolicyError, ReconcileResult, RunState,
                                      RunStatus, StopReason, TaskState)
from ashlar_controller.store import Store

from .helpers import PACKAGE_ROOT
from .test_pr_lifecycle import TRANSCRIPT_MARKER, LifecycleCase, done, red

RUNNING = RunStatus(state=RunState.RUNNING)


def with_fake2(raw, **extra):
    raw["providers"]["fake2"] = dict(raw["providers"]["fake"], funding_pool="fake2-pool", **extra)


class TransferCase(LifecycleCase):
    def setUp(self):
        super().setUp()
        self.policy_path = self.write_policy(mutate=with_fake2)

    def make2(self, a1=None, a2=None, policy=None, adapters=None):
        store = Store.open(self.state_path) if os.path.exists(self.state_path) else self.init_store()
        self.store = store
        self.addCleanup(store.close)
        self.a1 = a1 or FakeAdapter()
        self.a2 = a2 or FakeAdapter()
        for a in (self.a1, self.a2):
            a.default_status = done()
        self.adapter = self.a1
        self.policy = policy or self.load()
        regs = adapters if adapters is not None else {"fake": self.a1, "fake2": self.a2}
        self.ctl = Controller(store, self.policy, regs, holder_id="holder-a", host="host-a", pid=4242, clock=self.clock,
                              gh=self.gh, git=self.git, workspaces_dir=str(self.tmp / "ws"))
        return self.ctl

    def other_controller(self, adapters):
        """A second controller over the same ledger, as a CLI invocation with other `--adapter`s would be."""
        return Controller(self.store, self.policy, adapters, holder_id="holder-a", host="host-a", pid=4242,
                          clock=self.clock, gh=self.gh, git=self.git, workspaces_dir=str(self.tmp / "ws"))

    def journal_ids(self, kind, run_id=None):
        if run_id:
            return [r["id"] for r in self.store.rows("SELECT id FROM journal WHERE kind=? AND run_id=? ORDER BY id", (kind, run_id))]
        return [r["id"] for r in self.store.rows("SELECT id FROM journal WHERE kind=? ORDER BY id", (kind,))]

    def snapshot(self, task_id="t1"):
        t = self.task(task_id)
        p = self.pool(task_id)
        return {"provider": t["provider"], "state": t["state"], "pool_id": t["pool_id"], "card": t["card_number"],
                "title": t["title"], "branch": t["branch"], "base_sha": t["base_sha"],
                "consumed": p["consumed"], "reserved": p["reserved"], "attempts_used": p["attempts_used"],
                "transfers": self.journal_count("provider-transferred")}


class KeepsTaskPrAttemptsAndPool(TransferCase):
    # Kills: the PR mirror (`provider_transfers_used + 1` dropped), a transfer that resets the pool or the attempts,
    # and a repair that still goes to the old provider's adapter.
    def test_a_transfer_with_an_open_pr_keeps_task_pr_attempts_and_pool(self):
        self.make2()
        row = self.open_pr()
        n = row["pr_number"]
        before_task, before_pool = self.task(), self.pool()
        out = self.ctl.transfer_task("t1", "fake2", reason="claude quota")
        self.assertEqual((out["from"], out["to"], out["transfer_no"], out["of"], out["state"], out["pr_number"]),
                         ("fake", "fake2", 1, 1, TaskState.PR_OPEN, n))
        t, p, pr = self.task(), self.pool(), self.pr_row(n)
        self.assertEqual((t["provider"], t["state"], t["stop_reason"]),
                         ("fake2", TaskState.PR_OPEN, StopReason.PROVIDER_TRANSFERRED))
        self.assertIn("transferred from fake to fake2 (1 of 1): claude quota", t["blocker"])
        for k in ("task_id", "card_number", "title", "pool_id", "branch", "base_sha", "workstream"):
            self.assertEqual(t[k], before_task[k], k)
        for k in ("pool_id", "consumed", "reserved", "attempts_used", "attempts_max", "task_ceiling", "overshoot"):
            self.assertEqual(p[k], before_pool[k], k)
        self.assertEqual((pr["pr_number"], pr["state"], pr["head_sha"], pr["base_sha"], pr["branch"]),
                         (n, row["state"], row["head_sha"], row["base_sha"], row["branch"]))
        self.assertEqual(pr["provider_transfers_used"], 1)
        detail = json.loads(self.store.row("SELECT detail FROM journal WHERE kind='provider-transferred'")["detail"])
        self.assertEqual((detail["from"], detail["to"], detail["pr_number"], detail["pool_id"]),
                         ("fake", "fake2", n, before_task["pool_id"]))
        # The PR phase now repairs through the NEW provider, in the same pool, with a fresh context.
        self.gh.set_checks("head-1", red("head-1"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("launched", "repair"))
        self.assertEqual((self.a1.launch_count, self.a2.launch_count), (1, 1))
        req = self.a2.requests[0]
        self.assertIsNone(req.resume_session_id)
        self.assertNotIn(TRANSCRIPT_MARKER, req.prompt)
        run = self.store.row("SELECT * FROM runs WHERE run_id=?", (r.run_id,))
        self.assertEqual((run["provider"], run["pool_id"]), ("fake2", before_task["pool_id"]))
        self.assertEqual(self.pool()["attempts_used"], before_pool["attempts_used"] + 1)


class StopsThePreviousWriter(TransferCase):
    # Kills: skipping the stop (`for run in active: ... cancel_run` removed): the old run stays running, and the
    # next reservation is refused by the one-active-writer index, or two writers would be active.
    def test_a_running_writer_is_cancelled_and_settled_before_the_new_provider_launches(self):
        self.make2(a1=FakeAdapter(script=[{"launch": "ok", "status": RUNNING}]))
        self.add_task()
        r1 = self.ctl.tick()
        self.assertEqual(r1.action, "launched")
        self.assertEqual(self.ctl.tick().action, "blocked")      # writer-busy while it runs
        out = self.ctl.transfer_task("t1", "fake2")
        self.assertEqual(self.a1.cancel_count, 1)
        self.assertEqual([(s["run_id"], s["how"]) for s in out["stopped"]], [(r1.run_id, "cancelled")])
        run1 = self.run_row(r1.run_id)
        self.assertEqual(run1["state"], RunState.CANCELLED)
        self.assertIsNotNone(run1["settled_at"])
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs WHERE state IN ('reserved','launching','running')")["n"], 0)
        self.assertEqual((self.task()["state"], self.task()["provider"]), (TaskState.QUEUED, "fake2"))
        r2 = self.ctl.tick()
        self.assertEqual(r2.action, "launched")
        self.assertEqual((self.a1.launch_count, self.a2.launch_count), (1, 1))
        settled = self.journal_ids("settled", r1.run_id)
        moved = self.journal_ids("provider-transferred")
        launched = self.journal_ids("launched", r2.run_id)
        self.assertEqual(len(settled) * len(moved) * len(launched), 1)
        self.assertLess(settled[0], moved[0])
        self.assertLess(moved[0], launched[0])
        self.assertEqual(self.pool()["attempts_used"], 2)

    # Kills: always cancelling (`if status is not None and status.state != RUNNING` -> `if False`): a run that had
    # already completed would be thrown away as cancelled instead of proposed.
    def test_a_run_that_already_finished_is_settled_with_its_outcome_not_cancelled(self):
        self.make2()
        self.add_task()
        r1 = self.ctl.tick()
        self.assertEqual(r1.action, "launched")                  # the fake run has finished; nothing has polled it
        out = self.ctl.transfer_task("t1", "fake2")
        self.assertEqual(self.a1.cancel_count, 0)
        self.assertEqual([(s["run_id"], s["how"], s["stop_reason"]) for s in out["stopped"]],
                         [(r1.run_id, "settled", StopReason.COMPLETED)])
        self.assertEqual(self.run_row(r1.run_id)["state"], RunState.FINISHED)
        pr = self.store.row("SELECT * FROM prs WHERE task_id='t1'")
        self.assertIsNotNone(pr)
        self.assertEqual((out["state"], out["pr_number"], pr["provider_transfers_used"]), (TaskState.PR_OPEN, pr["pr_number"], 1))
        self.assertEqual(self.task()["state"], TaskState.PR_OPEN)

    # Kills: dropping the adapter check (the transfer would settle a run it cannot stop).
    def test_a_running_writer_without_its_adapter_refuses_and_changes_nothing(self):
        self.make2(a1=FakeAdapter(script=[{"launch": "ok", "status": RUNNING}]))
        self.add_task()
        r1 = self.ctl.tick()
        before = self.snapshot()
        other = self.other_controller({"fake2": self.a2})
        with self.assertRaises(ControllerError) as cm:
            other.transfer_task("t1", "fake2")
        self.assertIn(StopReason.TRANSFER_WRITER_NOT_STOPPABLE, str(cm.exception))
        self.assertIn("--adapter fake", str(cm.exception))
        self.assertEqual(self.snapshot(), before)
        self.assertEqual(self.run_row(r1.run_id)["state"], RunState.RUNNING)
        self.assertEqual(self.a1.cancel_count, 0)

    # Kills: skipping the reconcile of an uncertain launch (`if any(... UNCERTAIN ...)` -> `if False`).
    def test_an_uncertain_launch_is_reconciled_first_and_refused_while_still_uncertain(self):
        self.make2(a1=FakeAdapter(script=[{"launch": "unknown"}]))
        self.add_task()
        r1 = self.ctl.tick()
        self.assertEqual(r1.reason, StopReason.LAUNCH_UNKNOWN)
        before = self.snapshot()
        other = self.other_controller({"fake2": self.a2})
        with self.assertRaises(ControllerError) as cm:
            other.transfer_task("t1", "fake2")
        self.assertIn(StopReason.RECONCILE_PENDING, str(cm.exception))
        self.assertEqual(self.snapshot(), before)
        # With the old provider's adapter registered, the launch is reconciled (never started) and the move happens.
        out = self.ctl.transfer_task("t1", "fake2")
        self.assertEqual(self.a1.reconcile_count, 1)
        self.assertEqual(self.run_row(r1.run_id)["stop_reason"], StopReason.LAUNCH_FAILED)
        self.assertEqual((out["state"], self.task()["provider"]), (TaskState.QUEUED, "fake2"))


class TheBound(TransferCase):
    # Kills: `used >= limit` -> `used > limit`, and the bound checked AFTER the writer is stopped (the running run
    # on fake2 would be cancelled by a transfer that is then refused).
    def test_the_bound_refuses_a_second_transfer_before_stopping_anything(self):
        self.make2(a2=FakeAdapter(script=[{"launch": "ok", "status": RUNNING}]))
        self.add_task()
        self.ctl.transfer_task("t1", "fake2")
        r = self.ctl.tick()
        self.assertEqual(r.action, "launched")
        before = self.snapshot()
        with self.assertRaises(BudgetExhausted) as cm:
            self.ctl.transfer_task("t1", "fake")
        self.assertIn(StopReason.PROVIDER_TRANSFERS_EXHAUSTED, str(cm.exception))
        self.assertIn("1 of 1", str(cm.exception))
        self.assertEqual(self.a2.cancel_count, 0)
        self.assertEqual(self.run_row(r.run_id)["state"], RunState.RUNNING)
        self.assertEqual(self.snapshot(), before)
        refused = json.loads(self.store.row("SELECT detail FROM journal WHERE kind='transfer-refused'")["detail"])
        self.assertEqual((refused["reason"], refused["used"], refused["limit"]),
                         (StopReason.PROVIDER_TRANSFERS_EXHAUSTED, 1, 1))

    # Kills: a limit read from anywhere but the policy (0 must refuse the first transfer).
    def test_a_zero_limit_refuses_the_first_transfer(self):
        self.policy_path = self.write_policy(mutate=lambda raw: (with_fake2(raw), raw["limits"].__setitem__("provider_transfers_per_pr", 0)))
        self.make2()
        self.add_task()
        with self.assertRaises(BudgetExhausted):
            self.ctl.transfer_task("t1", "fake2")
        self.assertEqual((self.task()["provider"], self.journal_count("provider-transferred")), ("fake", 0))

    # Kills: counting transfers per task instead of per pool (`task_id IN (... pool_id=?)` -> `task_id=?`): a
    # child that shares its parent's pool would get a fresh transfer.
    def test_a_child_task_shares_the_count_of_its_pool(self):
        self.make2()
        self.add_task("t1")
        self.ctl.ensure_task("t2", workstream="budgeted-autonomy", provider="fake", parent_task_id="t1", title="split")
        self.ctl.transfer_task("t1", "fake2")
        with self.assertRaises(BudgetExhausted):
            self.ctl.transfer_task("t2", "fake2")
        self.assertEqual(self.task("t2")["provider"], "fake")


class FreshContextOnTheNewProvider(TransferCase):
    # Kills: the provider guard on resume (`and last["provider"] == task["provider"]` dropped): the new provider
    # would be handed the old CLI's session id and the old workspace.
    def test_a_session_is_never_resumed_across_providers(self):
        ceiling = RunStatus(state=RunState.FAILED, stop_reason=StopReason.INVOCATION_CEILING, consumed_units=12.0)
        self.make2(a1=FakeAdapter(script=[{"launch": "ok", "status": ceiling}]))
        self.add_task()
        r1 = self.ctl.tick()
        ws1 = self.run_row(r1.run_id)["workspace"]
        with open(os.path.join(ws1, "edited.txt"), "w", encoding="utf-8") as f:
            f.write("edit by the first provider\n")
        self.assertEqual(self.ctl.tick().action, "settled")
        self.assertEqual(self.task()["stop_reason"], StopReason.INVOCATION_CEILING)
        self.ctl.transfer_task("t1", "fake2")
        r2 = self.ctl.tick()
        self.assertEqual(r2.action, "launched")
        req = self.a2.requests[0]
        self.assertIsNone(req.resume_session_id)
        self.assertNotEqual(req.workspace, ws1)
        self.assertEqual(len(self.git.prepared), 2)
        self.assertEqual(self.journal_count("resume"), 0)
        self.assertEqual(self.journal_count("workspace-reused"), 0)
        self.assertIn(f"transferred from fake after run {r1.run_id} ({StopReason.INVOCATION_CEILING})", req.prompt)
        self.assertNotIn(TRANSCRIPT_MARKER, req.prompt)
        self.assertNotIn("fake-session-1", req.prompt)


class Refusals(TransferCase):
    def refused(self, exc, task_id, to, contains):
        before = self.snapshot(task_id)
        with self.assertRaises(exc) as cm:
            self.ctl.transfer_task(task_id, to)
        self.assertIn(contains, str(cm.exception))
        self.assertEqual(self.snapshot(task_id), before)

    # Kills: each target check in transfer_task (unknown, same, disabled by policy, disabled by the operator, a
    # different unit, a done task).
    def test_invalid_targets_are_refused_and_change_nothing(self):
        self.policy_path = self.write_policy(mutate=lambda raw: (
            with_fake2(raw), raw["providers"].__setitem__("fake3", dict(raw["providers"]["fake"], unit="requests"))))
        self.make2()
        self.add_task()
        self.refused(PolicyError, "t1", "nosuch", "names no provider")
        self.refused(PolicyError, "t1", "fake", "already on provider fake")
        self.refused(PolicyError, "t1", "codex", "disabled by policy")
        self.refused(PolicyError, "t1", "fake3", "never converted")
        self.store.set_control("disabled_providers", json.dumps(["fake2"]))
        self.refused(PolicyError, "t1", "fake2", "disabled by the operator")
        self.store.set_control("disabled_providers", "[]")
        self.ctl.set_task_state("t1", TaskState.DONE)
        self.refused(PolicyError, "t1", "fake2", "is done")
        self.assertEqual(self.journal_count("provider-transferred"), 0)


class OperatorView(TransferCase):
    # Kills: the transfers entry of `status`, the `transfers` counts of `metrics`, and their report lines.
    def test_status_metrics_and_report_show_the_transfers(self):
        self.make2()
        self.add_task()
        self.ctl.transfer_task("t1", "fake2")
        with self.assertRaises(BudgetExhausted):
            self.ctl.transfer_task("t1", "fake")
        t = self.ctl.status()["tasks"][0]
        self.assertEqual((t["provider"], t["transfers"]), ("fake2", {"used": 1, "max": 1}))
        m = metrics.collect(self.store, now=self.clock())
        self.assertEqual(m["transfers"], {"total": 1, "refused": 1})
        text = metrics.render_report(self.ctl.status(), m)
        self.assertIn("- Provider: fake2", text)
        self.assertIn("- Provider transfers: 1/1", text)
        self.assertIn("Provider transfers 1; refused 1", text)


class CommandLine(TransferCase):
    def cli(self, *args, expect=0):
        cmd = [sys.executable, str(PACKAGE_ROOT / "controller.py"), "--state", self.state_path, "--policy", self.policy_path,
               "--holder", "cli-test", "--json", *args]
        p = subprocess.run(cmd, capture_output=True, text=True, env=dict(os.environ, PYTHONWARNINGS="ignore"), timeout=120)
        self.assertEqual(p.returncode, expect, f"{' '.join(args)}\nstdout: {p.stdout}\nstderr: {p.stderr}")
        return json.loads(p.stdout) if p.stdout.strip().startswith(("{", "[")) else p.stdout, p.stderr

    # Kills: the `task transfer` dispatch, and the exit code of a refused transfer (6, budget refused).
    def test_task_transfer_from_the_command_line(self):
        self.cli("init")
        self.cli("task", "add", "--task-id", "t1", "--workstream", "budgeted-autonomy", "--provider", "fake", "--title", "x")
        out, _ = self.cli("task", "transfer", "t1", "--to", "fake2", "--reason", "operator")
        self.assertEqual((out["from"], out["to"], out["transfer_no"], out["state"]), ("fake", "fake2", 1, TaskState.QUEUED))
        out, _ = self.cli("task", "show", "t1")
        self.assertEqual(out["provider"], "fake2")
        _, err = self.cli("task", "transfer", "t1", "--to", "fake", expect=6)
        self.assertIn(StopReason.PROVIDER_TRANSFERS_EXHAUSTED, err)
        _, err = self.cli("task", "transfer", "t1", "--to", "nosuch", expect=4)

    # Kills: `--adapter` losing `action="append"` (only the last one would register) or the fake not always present.
    def test_adapter_is_repeatable_and_fake_is_always_registered(self):
        args = cli_mod.build_parser().parse_args(["--state", "s", "tick", "--adapter", "claude", "--adapter", "codex",
                                                  "--adapter", "claude"])
        self.assertEqual(cli_mod.adapter_names(args), ["fake", "claude", "codex"])
        args = cli_mod.build_parser().parse_args(["--state", "s", "tick"])
        self.assertEqual(cli_mod.adapter_names(args), ["fake"])
        args = cli_mod.build_parser().parse_args(["--state", "s", "task", "transfer", "t1", "--to", "codex",
                                                  "--adapter", "cursor"])
        self.assertEqual((args.to_provider, cli_mod.adapter_names(args)), ("codex", ["fake", "cursor"]))


if __name__ == "__main__":
    unittest.main()
