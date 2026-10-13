"""Phase F pre-flight fixes, lane A: the controller and CLI operator paths (live-run review of PR 5).

- A1 (D1) `task transfer` without the PR lifecycle refuses a completed previous writer instead of marking the task
  done and then moving it to `queued` with the work dropped; `--gh` and `--workspaces` reach the command; a
  settlement that leaves the task done ends the transfer without moving it; the reconcile is this task's only.
- A2 (E2) the `provider-transferred` row records how each previous run was stopped.
- A3 (T4, T5) `run cancel` refuses a live run whose adapter is not registered, takes `--adapter`, and settles a run
  that had already finished with what it did, not as cancelled.
- A4 (SEQ-5, T8) `run settle` of a settled run is refused and moves nothing; a second settlement moves no task;
  `run settle --stop-reason completed` of a writer run is refused (it has no PR lifecycle).
- A5 (T8) a passing readiness report leaves a task with an open PR to the PR phase.
- A6 (E8) a git or gh failure while proposing a completed run is journalled and parks the task in needs-reconcile,
  instead of a traceback with the task stranded `running`.
- A7 (SEQ-6) the dry run applies the tick's dispatch rules: repair writers, the PR phase, registered adapters.
- A8 (REPORT-CP1252) output redirected under a legacy code page is written as UTF-8.

Every behaviour is driven through a fake adapter, the fake GitHub and a fake git; no worker CLI, gh or git runs.
"""
import json
import os
import subprocess
import sys
import unittest

from ashlar_controller import cli as cli_mod
from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.controller import Controller
from ashlar_controller.gitops import GitError
from ashlar_controller.models import (ControllerError, ReconcileResult, RunState, RunStatus, StopReason, TaskState)
from ashlar_controller.prs import FakeGh, GhError
from ashlar_controller.store import Store

from .helpers import PACKAGE_ROOT, ControllerCase
from .test_pr_lifecycle import FakeGit, done, green, red
from .test_provider_transfer import RUNNING, TransferCase, with_fake2

CLI = PACKAGE_ROOT / "controller.py"


class FinishesAtCancel(FakeAdapter):
    """Polls as running until it is asked to cancel; every poll after that finds it finished. That is a run that
    completed between the controller's poll and its cancel, or a cancel that reached a process already gone."""

    def __init__(self, finished=None):
        super().__init__()
        self.finished = finished or done()

    def poll(self, handle):
        self.poll_count += 1
        return self.finished if self.cancel_count else RUNNING


class FailingPrCreate(FakeGh):
    def pr_create(self, repo, head_branch, base_branch, title, body):
        self.create_count += 1
        raise GhError("gh pr create: HTTP 422: Validation Failed\nstderr tail: a pull request already exists")


def rejected_push(branch):
    return f"git push origin {branch} exited 128: ! [remote rejected] {branch} (pre-receive hook declined)"


class FailingPush(FakeGit):
    """A push that the remote rejects while `fail` is set."""

    def __init__(self, gh, root):
        super().__init__(gh, root)
        self.fail = True

    def commit_and_push(self, path, branch, message, **kw):
        if not self.fail:
            return super().commit_and_push(path, branch, message, **kw)
        self.pushes.append({"path": path, "branch": branch, "message": message})
        raise GitError(rejected_push(branch) + "\nerror: failed to push some refs")


def run_cli(case, *args, expect=0, holder="holder-a", env=None):
    """The real command line in a subprocess over the case's ledger and policy."""
    cmd = [sys.executable, str(CLI), "--state", case.state_path, "--policy", case.policy_path, "--holder", holder,
           "--json", *args]
    p = subprocess.run(cmd, capture_output=True, text=True, env=env or dict(os.environ, PYTHONWARNINGS="ignore"),
                       timeout=120)
    case.assertEqual(p.returncode, expect, f"{' '.join(args)}\nstdout: {p.stdout}\nstderr: {p.stderr}")
    return json.loads(p.stdout) if p.stdout.strip().startswith(("{", "[")) else p.stdout, p.stderr


def journal_kinds(store):
    return [r["kind"] for r in store.rows("SELECT kind FROM journal ORDER BY id")]


def journal_detail(store, kind):
    row = store.row("SELECT detail FROM journal WHERE kind=? ORDER BY id DESC LIMIT 1", (kind,))
    return json.loads(row["detail"]) if row is not None else None


class PreflightCase(TransferCase):
    """Two fake providers with the fake PR lifecycle (`self.ctl`, as `tick --gh cli` builds it), plus the
    controller a CLI command builds WITHOUT `--gh cli`: no GhClient, so no PR lifecycle."""

    def without_lifecycle(self, adapters):
        return Controller(self.store, self.policy, adapters, holder_id="holder-a", host="host-a", pid=4242,
                          clock=self.clock)

    def fresh_fake(self):
        """A fake built by another process: it answers any handle as completed, as a fresh Codex adapter classifies
        a finished run from its run directory."""
        a = FakeAdapter()
        a.default_status = done()
        return a


class A1TransferWithoutTheLifecycle(PreflightCase):
    # Kills: dropping the refusal on the polled runs (`if self._completed_unproposed(run, status):` -> `if False:`):
    # the completed run is settled `done` with no commit, push or PR, then moved to `queued` on the new provider.
    def test_a_completed_writer_is_refused_without_the_lifecycle_and_nothing_changes(self):
        self.make2()
        self.add_task()
        r1 = self.ctl.tick()
        self.assertEqual(r1.action, "launched")          # finished, never polled: the launching process has exited
        before, kinds = self.snapshot(), journal_kinds(self.store)
        fresh = self.fresh_fake()
        with self.assertRaises(ControllerError) as cm:
            self.without_lifecycle({"fake": fresh, "fake2": self.a2}).transfer_task("t1", "fake2")
        self.assertTrue(str(cm.exception).startswith(StopReason.TRANSFER_NEEDS_PR_LIFECYCLE), str(cm.exception))
        self.assertIn("--gh cli", str(cm.exception))
        self.assertEqual(cm.exception.exit_code, 1)
        self.assertEqual(self.snapshot(), before)
        self.assertEqual(journal_kinds(self.store), kinds)
        run = self.run_row(r1.run_id)
        self.assertEqual((run["state"], run["settled_at"], run["stop_reason"]), (RunState.RUNNING, None, None))
        self.assertEqual((self.task()["state"], self.task()["provider"]), (TaskState.RUNNING, "fake"))
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM prs")["n"], 0)
        self.assertEqual((fresh.poll_count, fresh.cancel_count), (1, 0))
        # The operator's way through: a tick with the lifecycle proposes the run, then the transfer moves the PR.
        self.assertEqual(self.ctl.tick().action, "settled")
        pr = self.store.row("SELECT * FROM prs WHERE task_id='t1'")
        out = self.without_lifecycle({"fake": self.fresh_fake()}).transfer_task("t1", "fake2")
        self.assertEqual((out["moved"], out["state"], out["pr_number"], out["stopped"]),
                         (True, TaskState.PR_OPEN, pr["pr_number"], []))
        self.assertEqual((self.task()["provider"], self.task()["state"]), ("fake2", TaskState.PR_OPEN))

    # Kills: dropping the same refusal on the reconcile path (`if refuse_unproposed and found.found and ...` ->
    # `if False and ...`): an uncertain launch that turns out completed would be settled `done` with nothing proposed.
    def test_a_reconciled_launch_that_completed_is_refused_without_the_lifecycle(self):
        found = ReconcileResult(found=True, status=done(), native_session_id="s-1", native_run_handle="h-1")
        a1 = FakeAdapter(script=[{"launch": "unknown", "reconcile": found}])
        self.make2(a1=a1)
        self.add_task()
        r1 = self.ctl.tick()
        self.assertEqual(r1.reason, StopReason.LAUNCH_UNKNOWN)
        before, kinds = self.snapshot(), journal_kinds(self.store)
        with self.assertRaises(ControllerError) as cm:
            self.without_lifecycle({"fake": a1}).transfer_task("t1", "fake2")
        self.assertTrue(str(cm.exception).startswith(StopReason.TRANSFER_NEEDS_PR_LIFECYCLE), str(cm.exception))
        self.assertEqual((self.snapshot(), journal_kinds(self.store)), (before, kinds))
        run = self.run_row(r1.run_id)
        self.assertEqual((run["state"], run["native_run_handle"], run["settled_at"]), (RunState.UNKNOWN, None, None))
        # With the lifecycle the reconciled run is proposed first, and the transfer reports it as reconciled.
        out = self.ctl.transfer_task("t1", "fake2")
        pr = self.store.row("SELECT * FROM prs WHERE task_id='t1'")
        self.assertEqual((out["moved"], out["state"], out["pr_number"]), (True, TaskState.PR_OPEN, pr["pr_number"]))
        self.assertEqual([(s["run_id"], s["how"], s["stop_reason"]) for s in out["stopped"]],
                         [(r1.run_id, "reconciled", StopReason.COMPLETED)])

    # Kills: dropping the check after the settlements (`if after["state"] == TaskState.DONE:` -> `if False:`): a task
    # that a settlement left `done` would be moved to `queued` on the new provider anyway.
    def test_a_settlement_that_finishes_the_task_ends_the_transfer_without_moving_it(self):
        a1 = FinishesAtCancel()
        self.make2(a1=a1)
        self.add_task()
        r1 = self.ctl.tick()
        self.assertEqual(r1.action, "launched")
        out = self.without_lifecycle({"fake": a1}).transfer_task("t1", "fake2")
        self.assertEqual((out.get("moved"), out.get("ended"), out["state"], out["transfer_no"]),
                         (False, "task-done-by-settlement", TaskState.DONE, None))
        self.assertEqual([(s["run_id"], s["how"], s["stop_reason"]) for s in out["stopped"]],
                         [(r1.run_id, "settled", StopReason.COMPLETED)])
        self.assertEqual((self.task()["provider"], self.task()["state"]), ("fake", TaskState.DONE))
        self.assertEqual(self.journal_count("provider-transferred"), 0)
        self.assertEqual(self.journal_count("transfer-ended"), 1)
        ended = journal_detail(self.store, "transfer-ended")
        self.assertEqual((ended["reason"], ended["to"], ended["stopped_runs"]), ("task-done-by-settlement", "fake2", [r1.run_id]))
        self.assertEqual(ended["stopped"], out["stopped"])
        self.assertEqual(a1.cancel_count, 1)

    # Kills: the transfer's reconcile sweeping the whole ledger (`self.settle_and_reconcile(task_id=task_id,
    # refuse_unproposed=True)` -> `self.settle_and_reconcile(refuse_unproposed=True)`): another task's run would be
    # polled and settled by a transfer that was not about it.
    def test_the_transfer_reconciles_only_its_own_tasks_runs(self):
        a1 = FakeAdapter(script=[{"launch": "unknown", "reconcile": ReconcileResult(found=False)}, {"launch": "ok"}])
        self.make2(a1=a1)
        self.add_task("t1", priority=10)
        self.add_task("t2", priority=20)
        r1 = self.ctl.tick()
        self.assertEqual(r1.reason, StopReason.LAUNCH_UNKNOWN)
        # Emulate a ledger where t1's launch is still uncertain while t2's run is recorded running (its process has
        # finished, so any poll would settle it): hide r1 for one tick, launch t2, then restore r1.
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state='failed' WHERE run_id=?", (r1.run_id,))
        r2 = self.ctl.tick()
        self.assertEqual((r2.action, r2.task_id), ("launched", "t2"))
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state='unknown' WHERE run_id=?", (r1.run_id,))
        polls = a1.poll_count
        out = self.ctl.transfer_task("t1", "fake2")
        self.assertEqual((out.get("moved"), out["state"]), (True, TaskState.QUEUED))
        self.assertEqual(self.run_row(r1.run_id)["stop_reason"], StopReason.LAUNCH_FAILED)
        self.assertEqual((self.run_row(r2.run_id)["state"], self.task("t2")["state"]), (RunState.RUNNING, TaskState.RUNNING))
        self.assertEqual(a1.poll_count, polls)
        self.assertEqual(self.gh.create_count, 0)


class CodexEnabled(PreflightCase):
    def setUp(self):
        super().setUp()
        self.policy_path = self.write_policy(mutate=lambda raw: (with_fake2(raw),
                                                                 raw["providers"]["codex"].update({"enabled": True})))


class A1TransferCommandLine(CodexEnabled):
    # Kills: the refusal reaching the command line (exit 1 with the reason on stderr), and a refused transfer
    # touching the ledger.
    def test_task_transfer_refuses_a_completed_writer_without_gh(self):
        self.make2()
        self.add_task()
        r1 = self.ctl.tick()                         # a `tick --gh cli` process launched it and exited
        _, err = run_cli(self, "task", "transfer", "t1", "--to", "fake2", "--adapter", "fake", "--gh", "none", expect=1)
        self.assertIn(f"controller: {StopReason.TRANSFER_NEEDS_PR_LIFECYCLE}", err)
        out, _ = run_cli(self, "task", "show", "t1")
        self.assertEqual((out["provider"], out["state"]), ("fake", TaskState.RUNNING))
        out, _ = run_cli(self, "run", "list")
        self.assertEqual([(r["run_id"], r["state"]) for r in out], [(r1.run_id, RunState.RUNNING)])

    # Kills: `--gh` or `--workspaces` missing from the transfer subcommand (a completed writer could not be proposed
    # from the command line at all: argparse refused `--gh cli` with exit 2).
    def test_the_transfer_command_takes_gh_and_workspaces_like_tick(self):
        p = cli_mod.build_parser()
        args = p.parse_args(["--state", "s", "task", "transfer", "t1", "--to", "fake2", "--gh", "cli", "--workspaces", "w"])
        self.assertEqual((args.gh, args.workspaces), ("cli", "w"))
        args = p.parse_args(["--state", "s", "task", "transfer", "t1", "--to", "fake2"])
        self.assertEqual((args.gh, args.workspaces), ("none", None))


class A2TheTransferJournalsHowEachRunStopped(PreflightCase):
    # Kills: the `stopped` list dropped from the `provider-transferred` detail (only bare run ids were journalled, so
    # how a run was stopped existed only in the command's stdout).
    def test_a_settled_previous_writer_is_journalled_with_how_it_stopped(self):
        self.make2()
        self.add_task()
        r1 = self.ctl.tick()
        out = self.ctl.transfer_task("t1", "fake2")
        detail = journal_detail(self.store, "provider-transferred")
        self.assertEqual(detail["stopped_runs"], [r1.run_id])
        self.assertEqual(detail.get("stopped"), [{"run_id": r1.run_id, "provider": "fake", "role": "implementation",
                                                  "how": "settled", "charged": 3.0, "stop_reason": StopReason.COMPLETED}])
        self.assertEqual(detail["stopped"], out["stopped"])

    def test_a_cancelled_previous_writer_is_journalled_with_how_it_stopped(self):
        self.make2(a1=FakeAdapter(script=[{"launch": "ok", "status": RUNNING}]))
        self.add_task()
        r1 = self.ctl.tick()
        out = self.ctl.transfer_task("t1", "fake2")
        detail = journal_detail(self.store, "provider-transferred")
        self.assertEqual(detail.get("stopped"), [{"run_id": r1.run_id, "provider": "fake", "role": "implementation",
                                                  "how": "cancelled", "charged": 20.0, "stop_reason": StopReason.CANCELLED}])
        self.assertEqual(detail["stopped"], out["stopped"])


class A3CancelRun(ControllerCase):
    def other(self, adapters):
        return Controller(self.store, self.policy, adapters, holder_id="holder-b", host="host-a", pid=4243,
                          clock=self.clock)

    # Kills: dropping the refusal (`if adapter is None and run["state"] in (...)` -> `if False and ...`): the run was
    # settled `cancelled` while its worker kept running, and nothing would ever poll it again.
    def test_cancel_of_a_live_run_without_its_adapter_is_refused_and_changes_nothing(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": RUNNING}, {"launch": "unknown"}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r = ctl.tick()
        kinds = journal_kinds(self.store)
        with self.assertRaises(ControllerError) as cm:
            self.other({}).cancel_run(r.run_id)
        self.assertTrue(str(cm.exception).startswith(StopReason.TRANSFER_WRITER_NOT_STOPPABLE), str(cm.exception))
        self.assertIn("--adapter fake", str(cm.exception))
        run = self.run_row(r.run_id)
        self.assertEqual((run["state"], run["settled_at"]), (RunState.RUNNING, None))
        self.assertEqual(self.task()["state"], TaskState.RUNNING)
        self.assertEqual((journal_kinds(self.store), adapter.cancel_count), (kinds, 0))
        # With its adapter it is stopped and settled as before.
        out = ctl.cancel_run(r.run_id)
        self.assertEqual((out["cancelled"], out["stop_reason"], adapter.cancel_count), (True, StopReason.CANCELLED, 1))
        # An unknown launch may have a live process too: refused the same way.
        self.add_task("t2")
        r2 = ctl.tick()
        self.assertEqual(r2.reason, StopReason.LAUNCH_UNKNOWN)
        with self.assertRaises(ControllerError):
            self.other({}).cancel_run(r2.run_id)
        self.assertEqual(self.run_row(r2.run_id)["state"], RunState.UNKNOWN)

    # Kills: settling every cancel as `cancelled` (`if after.state != RunState.RUNNING and ...` -> `if False and ...`):
    # a run that had already completed would be thrown away as cancelled and charged its whole reservation.
    def test_a_run_that_finished_before_the_cancel_is_settled_with_what_it_did(self):
        adapter = FinishesAtCancel()
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r = ctl.tick()
        self.assertEqual(ctl.tick().reason, StopReason.WRITER_BUSY)     # it polls as running
        out = ctl.cancel_run(r.run_id)
        self.assertEqual((out["stop_reason"], out["cancelled"], out.get("finished_before_cancel"), out["charged"]),
                         (StopReason.COMPLETED, False, True, 3.0))
        run = self.run_row(r.run_id)
        self.assertEqual((run["state"], run["stop_reason"], run["consumed"]), (RunState.FINISHED, StopReason.COMPLETED, 3.0))
        self.assertEqual(self.task()["state"], TaskState.DONE)            # no lifecycle here: completed is done
        self.assertEqual(adapter.cancel_count, 1)


class A3CancelCommandLine(CodexEnabled):
    # Kills: `--adapter` missing from `run cancel` (the CLI could only ever register the fake, so a Codex, Cursor or
    # Claude run could never be stopped by it), and the refusal reaching the command line.
    def test_run_cancel_takes_adapter_and_refuses_a_live_run_without_it(self):
        args = cli_mod.build_parser().parse_args(["--state", "s", "run", "cancel", "r1", "--adapter", "fake",
                                                  "--adapter", "codex"])
        self.assertEqual((args.run_id, cli_mod.adapter_names(args)), ("r1", ["fake", "codex"]))
        self.make2(adapters={"codex": FakeAdapter(script=[{"launch": "ok", "status": RUNNING}])})
        self.add_task(provider="codex")
        r = self.ctl.tick()
        self.assertEqual(r.action, "launched")
        _, err = run_cli(self, "run", "cancel", r.run_id, expect=1)
        self.assertIn(StopReason.TRANSFER_WRITER_NOT_STOPPABLE, err)
        self.assertIn("--adapter codex", err)
        self.assertEqual(self.run_row(r.run_id)["state"], RunState.RUNNING)


class A4RunSettle(ControllerCase):
    def settled_no_final_result(self):
        adapter = FakeAdapter(script=[{"launch": "ok", "status": RunStatus(state=RunState.FAILED,
                                                                           stop_reason=StopReason.NO_FINAL_RESULT)}])
        ctl = self.make_controller(adapter=adapter)
        self.add_task()
        r = ctl.tick()
        self.assertEqual(ctl.tick().action, "settled")
        self.assertEqual(self.task()["state"], TaskState.NEEDS_RECONCILE)
        return ctl, r.run_id

    def ledger(self, run_id):
        return (dict(self.run_row(run_id)), dict(self.task()), journal_kinds(self.store),
                self.store.row("SELECT COUNT(*) AS n FROM failure_retries")["n"])

    # Kills: the `settled_at` guard in `run settle` (`if run["settled_at"] is not None:` -> `if False:`): exit 0 and
    # `already_settled: true` was the only sign, while the task moved (done, a spent retry, or cancelled).
    def test_run_settle_of_a_settled_run_is_refused_and_changes_nothing(self):
        _, run_id = self.settled_no_final_result()
        before = self.ledger(run_id)
        for reason in (StopReason.COMPLETED, StopReason.TASK_FAILED, StopReason.CANCELLED):
            with self.subTest(stop_reason=reason):
                _, err = run_cli(self, "run", "settle", run_id, "--stop-reason", reason, expect=1)
                self.assertIn("already settled", err)
                self.assertIn(f"({RunState.FAILED}, stop reason {StopReason.NO_FINAL_RESULT}", err)
                self.assertEqual(self.ledger(run_id), before)

    # Kills: `_apply_outcome` ignoring `already_settled` (`if settled.get("already_settled"):` -> `if False:`): the
    # run row kept its first settlement while the task took the second one's transition.
    def test_a_second_settlement_moves_no_task(self):
        ctl, run_id = self.settled_no_final_result()
        before = self.ledger(run_id)
        out = ctl._apply_outcome(self.run_row(run_id), RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED,
                                                                 consumed_units=1.0))
        self.assertEqual((out.get("already_settled"), out.get("task_id")), (True, "t1"))
        self.assertEqual(self.ledger(run_id), before)

    # Kills: `run settle --stop-reason completed` of a writer run going through (`if args.stop_reason ==
    # StopReason.COMPLETED and run["role"] in WRITER_ROLES:` -> `if False:`): the task was `done` with nothing proposed.
    def test_run_settle_completed_of_a_writer_run_is_refused(self):
        ctl = self.make_controller(adapter=FakeAdapter(script=[{"launch": "ok", "status": RUNNING}]))
        self.add_task()
        r = ctl.tick()
        before = self.ledger(r.run_id)
        _, err = run_cli(self, "run", "settle", r.run_id, "--stop-reason", StopReason.COMPLETED, expect=1)
        self.assertIn("has no PR lifecycle", err)
        self.assertIn("--gh cli", err)
        self.assertEqual(self.ledger(r.run_id), before)
        # Any other stop reason is still the operator's manual reconciliation.
        out, _ = run_cli(self, "run", "settle", r.run_id, "--stop-reason", StopReason.NO_FINAL_RESULT)
        self.assertEqual(out["stop_reason"], StopReason.NO_FINAL_RESULT)
        self.assertEqual(self.task()["state"], TaskState.NEEDS_RECONCILE)


class A5ReadinessWithAnOpenPr(PreflightCase):
    # Kills: the open-PR check in `evaluate_readiness` (`if not failures and open_pr is not None:` -> `if False:`): a
    # passing local report marked the task `done` while its PR was still open and unmerged.
    def test_a_passing_report_leaves_a_task_with_an_open_pr_to_the_pr_phase(self):
        self.make2()
        row = self.open_pr()
        before = dict(self.task())
        report = cli_mod._GateReport({"failures": [], "passed": ["repo-gates"], "source": "repo-gates"})
        out = self.ctl.evaluate_readiness("t1", report)
        self.assertEqual((out["action"], out.get("pr_number")), ("pr-open", row["pr_number"]))
        self.assertEqual(dict(self.task()), before)
        self.assertEqual(journal_detail(self.store, "readiness-evaluated")["action"], "pr-open")
        # The PR phase still decides: a merge on the current head makes the task done.
        self.gh.set_checks("head-1", green("head-1"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "merged"))
        self.assertEqual(self.task()["state"], TaskState.DONE)
        # A task with no open PR is still finished by a passing report.
        self.add_task("t2")
        self.assertEqual(self.ctl.evaluate_readiness("t2", report)["action"], "completed")
        self.assertEqual(self.task("t2")["state"], TaskState.DONE)


class A6AFailedProposal(PreflightCase):
    def make_failing(self, gh=None, git=None):
        if gh is not None:
            self.gh = gh
            self.gh.branches["master"] = "base-0000000"
        self.git = git(self.gh, str(self.tmp / "ws")) if git is not None else FakeGit(self.gh, str(self.tmp / "ws"))
        return self.make2()

    def assert_propose_failed(self, report, run_id, blocker):
        self.assertEqual((report.action, report.reason), ("settled", "settled"))
        json.dumps(report.to_json())                                  # the CLI prints it: no traceback, exit 0
        lc = report.settled[0]["lifecycle"]
        self.assertEqual(lc["action"], "propose-failed")
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"], t["blocker"]),
                         (TaskState.NEEDS_RECONCILE, StopReason.PROPOSE_FAILED, f"completed run not proposed: {blocker}"))
        detail = journal_detail(self.store, "propose-failed")
        self.assertEqual(detail["run_id"], run_id)
        self.assertIn(blocker, detail["error"])
        self.assertEqual(detail["error"], lc["error"])
        run = self.run_row(run_id)                                    # the run stays settled as it was
        self.assertEqual((run["state"], run["stop_reason"], run["consumed"]), (RunState.FINISHED, StopReason.COMPLETED, 3.0))
        self.assertIsNotNone(run["settled_at"])

    # Kills: the exception escaping `_apply_outcome` (`except Exception as e:` -> `except ZeroDivisionError as e:`):
    # the owner saw a traceback, and the task stayed `running` with no active run and no record of the error.
    def test_a_failed_pr_create_is_journalled_and_the_task_needs_reconcile(self):
        self.make_failing(gh=FailingPrCreate())
        self.add_task()
        self.add_task("t2")
        r1 = self.ctl.tick()
        self.assertEqual((r1.action, r1.task_id), ("launched", "t1"))
        self.assert_propose_failed(self.ctl.tick(), r1.run_id, "gh pr create: HTTP 422: Validation Failed")
        self.assertEqual((self.journal_count("pushed"), self.store.row("SELECT COUNT(*) AS n FROM prs")["n"]), (1, 0))
        # Kills: dropping the propose-failed dispatch guard (`if stranded:` in tick): t1 may have left a branch or a
        # PR on GitHub the ledger does not know, so nothing else launches (t2 stays queued) until an operator has
        # looked and moved t1; the dry run says the same. Then the next queued task launches.
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason, r.details), ("blocked", StopReason.PROPOSE_FAILED_BLOCKS_DISPATCH, {"tasks": ["t1"]}))
        self.assertEqual(self.task("t2")["state"], TaskState.QUEUED)
        d = self.ctl.tick(dry_run=True)
        self.assertEqual((d.action, d.reason), ("would-block", StopReason.PROPOSE_FAILED_BLOCKS_DISPATCH))
        self.ctl.set_task_state("t1", TaskState.PARKED_FAILURE, stop_reason="operator: checked the remote")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.task_id), ("launched", "t2"))

    def test_a_rejected_push_is_journalled_and_the_task_needs_reconcile(self):
        self.make_failing(git=FailingPush)
        self.add_task()
        r1 = self.ctl.tick()
        self.assert_propose_failed(self.ctl.tick(), r1.run_id, rejected_push("auto/t1"))
        self.assertEqual((self.journal_count("pushed"), self.gh.create_count), (0, 0))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PROPOSE_FAILED_BLOCKS_DISPATCH))

    # A repair whose push fails: the PR row stays open, and a task in needs-reconcile keeps its PR counted as open
    # without being observed, so dispatch stays blocked until an operator moves the task.
    def test_a_rejected_repair_push_keeps_the_pr_open_and_dispatch_blocked(self):
        self.make_failing(git=FailingPush)
        self.git.fail = False
        n = self.open_pr()["pr_number"]
        self.add_task("t2")
        self.git.fail = True
        self.gh.set_checks("head-1", red("head-1"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("launched", "repair"))
        self.assert_propose_failed(self.ctl.tick(), r.run_id, rejected_push("auto/t1"))
        self.assertEqual(self.pr_row(n)["state"], "open")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertEqual(self.task("t2")["state"], TaskState.QUEUED)

    # Kills: the transfer ignoring a failed proposal (`elif after["state"] == TaskState.NEEDS_RECONCILE and ...` ->
    # `elif False and ...`): the task would be moved to `queued` on the new provider and the completed work dropped.
    def test_a_transfer_whose_proposal_fails_ends_without_moving_the_task(self):
        self.make_failing(gh=FailingPrCreate())
        self.add_task()
        r1 = self.ctl.tick()
        out = self.ctl.transfer_task("t1", "fake2")
        self.assertEqual((out.get("moved"), out.get("ended"), out["state"]),
                         (False, StopReason.PROPOSE_FAILED, TaskState.NEEDS_RECONCILE))
        self.assertEqual([(s["run_id"], s["how"]) for s in out["stopped"]], [(r1.run_id, "settled")])
        self.assertEqual((self.task()["provider"], self.journal_count("provider-transferred")), ("fake", 0))
        self.assertEqual(journal_detail(self.store, "transfer-ended")["reason"], StopReason.PROPOSE_FAILED)


class A7DryRunParity(PreflightCase):
    def dry(self, lifecycle=True, adapters=None):
        """What `tick --dry-run` builds: the ledger opened read-only; `--gh cli` gives it the lifecycle."""
        ro = Store.open(self.state_path, read_only=True)
        self.addCleanup(ro.close)
        self.dry_adapters = adapters or {"fake": FakeAdapter()}
        self.dry_gh = FakeGh()
        return Controller(ro, self.policy, self.dry_adapters, holder_id="dry", clock=self.clock,
                          gh=self.dry_gh if lifecycle else None, git=self.git, workspaces_dir=str(self.tmp / "ws"))

    def assert_nothing_called(self):
        for a in self.dry_adapters.values():
            self.assertEqual(a.launch_count + a.poll_count + a.cancel_count + a.reconcile_count + a.usage_count, 0)
        g = self.dry_gh
        self.assertEqual(g.create_count + g.for_branch_count + g.view_count + g.check_runs_count + g.merge_count
                         + g.branch_head_count, 0)

    # Kills: the dry run counting only implementation runs as writers (`role IN ('implementation','readiness-fixer')`
    # -> `role IN ('implementation')`): during a repair it said would-launch where the tick says writer-busy.
    def test_a_repair_run_is_the_writer_in_the_dry_run_too(self):
        self.make2()
        self.open_pr()
        self.add_task("t2")
        self.gh.set_checks("head-1", red("head-1"))
        self.a1.default_status = RUNNING
        self.assertEqual(self.ctl.tick().reason, "repair")
        real = self.ctl.tick()
        plan = self.dry(lifecycle=False).tick(dry_run=True)
        self.assertEqual((real.action, real.reason), ("blocked", StopReason.WRITER_BUSY))
        self.assertEqual((plan.action, plan.reason), ("would-block", StopReason.WRITER_BUSY))
        self.assertEqual([a["role"] for a in plan.details["active"]], ["readiness-fixer"])
        self.assert_nothing_called()

    # Kills: the dry run's PR phase (`if self.lifecycle is not None:` in `_plan` -> `if False:`): with a PR open it
    # said would-launch for another task while a `--gh cli` tick launches nothing.
    def test_with_the_lifecycle_an_open_pr_blocks_the_dry_run_as_it_blocks_the_tick(self):
        self.make2()
        self.open_pr()
        self.add_task("t2")
        real = self.ctl.tick()
        self.assertEqual((real.action, real.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        plan = self.dry().tick(dry_run=True)
        self.assertEqual((plan.action, plan.reason, plan.details.get("open_prs")),
                         ("would-block", StopReason.PR_OPEN_BLOCKS_DISPATCH, 1))
        self.assert_nothing_called()
        # Without the lifecycle (`--gh none`) a tick does launch the other task, and so says the dry run.
        plan = self.dry(lifecycle=False).tick(dry_run=True)
        self.assertEqual((plan.action, plan.task_id), ("would-launch", "t2"))

    # Kills: the dry run's adapter check (`if self.adapters.get(task["provider"]) is None:` in `_plan` -> `if False:`):
    # it said would-launch where the tick, with the same adapters, is blocked.
    def test_a_provider_with_no_registered_adapter_blocks_the_dry_run_as_it_blocks_the_tick(self):
        self.make2(adapters={"fake": FakeAdapter()})
        self.add_task(provider="fake2")
        real = self.ctl.tick()
        plan = self.dry().tick(dry_run=True)
        self.assertEqual((real.action, real.reason, real.task_id), ("blocked", StopReason.PROVIDER_DISABLED, "t1"))
        self.assertEqual((plan.action, plan.reason, plan.task_id, plan.details.get("error")),
                         ("would-block", real.reason, real.task_id, real.details["error"]))
        self.assert_nothing_called()
        plan = self.dry(adapters={"fake": FakeAdapter(), "fake2": FakeAdapter()}).tick(dry_run=True)
        self.assertEqual((plan.action, plan.task_id), ("would-launch", "t1"))
        self.assert_nothing_called()


class A8OutputUnderALegacyCodePage(ControllerCase):
    # Kills: the UTF-8 reconfiguration of a redirected stdout and stderr (`_utf8_when_redirected()` dropped from
    # `main`): `report > file` exited 1 with UnicodeEncodeError and left an empty file.
    def test_report_and_status_survive_a_legacy_code_page_on_redirected_stdout(self):
        ctl = self.make_controller()
        self.add_task()
        blocker = "tool call failed → retry; ✓ checked"
        ctl.set_task_state("t1", TaskState.BLOCKED_AUTH, stop_reason=StopReason.AUTH_MISSING, blocker=blocker)
        env = dict(os.environ, PYTHONIOENCODING="cp1252", PYTHONUTF8="0", PYTHONWARNINGS="ignore")

        def run(*args):
            cmd = [sys.executable, str(CLI), "--state", self.state_path, "--policy", self.policy_path, *args]
            return subprocess.run(cmd, capture_output=True, env=env, timeout=120)    # pipes: not a terminal

        for args in (("report",), ("status",)):
            with self.subTest(command=args):
                p = run(*args)
                self.assertEqual(p.returncode, 0, p.stderr.decode("utf-8", "replace"))
                out = p.stdout.decode("utf-8")
                self.assertIn("→", out)
                self.assertIn(blocker, out)
        p = run("--json", "status")
        self.assertEqual(p.returncode, 0, p.stderr.decode("utf-8", "replace"))
        self.assertEqual(json.loads(p.stdout.decode("utf-8"))["tasks"][0]["blocker"], blocker)
        p = run("task", "show", "no-such-→")
        self.assertEqual(p.returncode, 1)
        self.assertIn("controller: no task no-such-→", p.stderr.decode("utf-8"))


if __name__ == "__main__":
    unittest.main()
