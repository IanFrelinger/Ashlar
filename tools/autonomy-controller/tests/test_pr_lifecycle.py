"""PR 3 acceptance, over the fake adapter, a fake GitHub (prs.FakeGh) and a fake git (FakeGit below):
old green checks cannot satisfy a changed head; duplicate events create no PR and no worker; closure
without merge is not success; the next task uses the updated base and a compact handoff; the previous
transcript is not injected; repair rounds are bounded; PRs are serial."""
import json
import os
import unittest

from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.controller import Controller
from ashlar_controller.models import RunState, RunStatus, StopReason, TaskState
from ashlar_controller.prs import FakeGh, MergeRefused, latest_merge_sha
from ashlar_controller.readiness import FIXER_ROLE
from ashlar_controller.store import Store

from .helpers import ControllerCase

REQUIRED = ["cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary"]
TRANSCRIPT_MARKER = "TRANSCRIPT-MARKER-do-not-inject"


class FakeGit:
    """Stands in for gitops. Records every call; a push updates the FakeGh branch tip and any open PR's head,
    the way GitHub would see it."""

    def __init__(self, gh, root, push_shas=None):
        self.gh = gh
        self.root = root
        self.prepared = []
        self.checkouts = []
        self.pushes = []
        self.push_shas = list(push_shas or [])
        self.no_change = False

    def prepare_workspace(self, path, remote_url, base_sha, branch, reference=None, **kw):
        os.makedirs(path, exist_ok=True)
        self.prepared.append({"path": path, "remote_url": remote_url, "base_sha": base_sha, "branch": branch,
                              "reference": reference})
        return base_sha

    def checkout_pr_head(self, path, remote_url, branch, expected_head, reference=None, **kw):
        os.makedirs(path, exist_ok=True)
        self.checkouts.append({"path": path, "branch": branch, "expected_head": expected_head})
        return expected_head

    def commit_and_push(self, path, branch, message, **kw):
        self.pushes.append({"path": path, "branch": branch, "message": message})
        if self.no_change:
            return None
        sha = self.push_shas.pop(0) if self.push_shas else f"head-{len(self.pushes)}"
        self.gh.branches[branch] = sha
        for pr in list(self.gh.prs.values()):
            if pr.head_branch == branch and pr.state == "OPEN":
                self.gh.push_head(pr.number, sha)
        return sha


def done(**kw):
    return RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed_units=3.0,
                     result={"result_text": TRANSCRIPT_MARKER + " the worker's final message", **kw})


def green(sha):
    return {name: "success" for name in REQUIRED}


def red(sha, failing="shell-lint"):
    d = green(sha)
    d[failing] = "failure"
    return d


class LifecycleCase(ControllerCase):
    def setUp(self):
        super().setUp()
        self.gh = FakeGh()
        self.gh.branches["master"] = "base-0000000"
        self.git = FakeGit(self.gh, str(self.tmp / "ws"))

    def make(self, adapter=None, store=None, policy=None):
        store = store or (Store.open(self.state_path) if os.path.exists(self.state_path) else self.init_store())
        self.store = store
        self.addCleanup(store.close)
        self.adapter = adapter or FakeAdapter()
        self.adapter.default_status = done()
        self.policy = policy or self.load()
        self.ctl = Controller(store, self.policy, {"fake": self.adapter}, holder_id="holder-a", host="host-a", pid=4242,
                              clock=self.clock, gh=self.gh, git=self.git, workspaces_dir=str(self.tmp / "ws"))
        return self.ctl

    def open_pr(self, task_id="t1"):
        """Launch, complete and propose: returns the prs row of the open PR."""
        self.add_task(task_id)
        self.assertEqual(self.ctl.tick().action, "launched")
        r = self.ctl.tick()
        self.assertEqual(r.action, "settled")
        row = self.store.row("SELECT * FROM prs WHERE task_id=? AND state='open'", (task_id,))
        self.assertIsNotNone(row)
        return row

    def pr_row(self, number):
        return self.store.row("SELECT * FROM prs WHERE pr_number=?", (number,))

    def journal_count(self, kind):
        return self.store.row("SELECT COUNT(*) AS n FROM journal WHERE kind=?", (kind,))["n"]


class OpeningThePr(LifecycleCase):
    def test_a_completed_implementation_run_is_proposed_not_done(self):
        self.make()
        row = self.open_pr()
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.PR_OPEN, StopReason.PR_CHECKS_PENDING))
        self.assertNotEqual(t["state"], TaskState.DONE)
        self.assertEqual(self.gh.create_count, 1)
        self.assertEqual((row["state"], row["outcome"], row["branch"], row["head_sha"], row["base_sha"]),
                         ("open", None, "auto/t1", "head-1", "base-0000000"))
        self.assertEqual(self.git.prepared[0]["base_sha"], "base-0000000")
        self.assertEqual(self.git.prepared[0]["branch"], "auto/t1")
        self.assertEqual(self.git.pushes[0]["branch"], "auto/t1")
        self.assertEqual(self.task()["branch"], "auto/t1")
        self.assertEqual(self.task()["base_sha"], "base-0000000")
        req = self.adapter.requests[0]
        self.assertEqual((req.base_sha, req.branch), ("base-0000000", "auto/t1"))
        self.assertIn("Do not run git", req.prompt)
        self.assertIn("none (first task", req.handoff)
        status = self.ctl.status()["tasks"][0]
        self.assertEqual(status["pr_record"]["number"], row["pr_number"])

    def test_the_run_workspace_is_the_prepared_clone(self):
        self.make()
        self.open_pr()
        run = self.store.row("SELECT * FROM runs")
        self.assertEqual(run["workspace"], self.git.prepared[0]["path"])
        self.assertEqual(self.adapter.requests[0].workspace, run["workspace"])

    def test_no_change_to_propose_is_a_diagnosed_failure_retried_once_then_parked(self):
        self.make()
        self.git.no_change = True
        self.add_task()
        self.ctl.tick(); r = self.ctl.tick()
        self.assertEqual(r.action, "settled")
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.QUEUED, StopReason.NO_CHANGE_TO_PROPOSE))
        self.assertEqual(self.gh.create_count, 0)
        self.ctl.tick(); self.ctl.tick()
        t = self.task()
        self.assertEqual(t["state"], TaskState.PARKED_FAILURE)
        self.assertEqual(self.gh.create_count, 0)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM prs")["n"], 0)

    def test_without_a_gh_client_a_completed_run_is_done_as_before(self):
        ctl = self.make_controller()
        self.adapter.default_status = done()
        self.add_task()
        ctl.tick(); ctl.tick()
        self.assertEqual(self.task()["state"], TaskState.DONE)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM prs")["n"], 0)


class ChecksByHeadSha(LifecycleCase):
    def test_old_green_checks_cannot_satisfy_a_changed_head(self):
        self.make()
        row = self.open_pr()
        n = row["pr_number"]
        self.gh.set_checks("head-1", green("head-1"))
        # The head moves (a push the controller did not make) before the controller observes.
        self.gh.push_head(n, "head-moved")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertEqual(self.gh.merge_count, 0)
        self.assertEqual(self.pr_row(n)["head_sha"], "head-moved")
        self.assertEqual(self.task()["state"], TaskState.PR_OPEN)
        self.assertIn("pending", self.task()["blocker"])
        # Green on the current head: merged, with the merge SHA recorded and the task done.
        self.gh.set_checks("head-moved", green("head-moved"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "merged"))
        self.assertEqual(self.gh.merge_count, 1)
        pr = self.pr_row(n)
        self.assertEqual((pr["state"], pr["outcome"]), ("merged", "merged"))
        self.assertTrue(pr["merge_sha"])
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.DONE, StopReason.PR_MERGED))
        self.assertEqual(latest_merge_sha(self.store), pr["merge_sha"])

    def test_a_single_red_required_check_on_the_current_head_is_not_green(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", red("head-1"))
        r = self.ctl.tick()
        self.assertEqual(self.gh.merge_count, 0)
        self.assertEqual((r.action, r.reason), ("launched", "repair"))

    def test_a_missing_required_check_is_pending_not_green(self):
        self.make()
        n = self.open_pr()["pr_number"]
        four = green("head-1"); four.pop("Readiness summary")
        self.gh.set_checks("head-1", four)
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertEqual(self.gh.merge_count, 0)

    def test_the_merge_is_bound_to_the_observed_head(self):
        """The head moves between the check fetch and the merge: the merge is refused, nothing lands."""
        gh = self.gh
        number_holder = {}

        class MovingGh(FakeGh):
            def check_runs(inner, repo, sha):
                runs = super().check_runs(repo, sha)
                inner.push_head(number_holder["n"], "head-raced")
                return runs
        moving = MovingGh()
        moving.branches["master"] = "base-0000000"
        self.gh = moving
        self.git = FakeGit(moving, str(self.tmp / "ws"))
        self.make()
        n = self.open_pr()["pr_number"]
        number_holder["n"] = n
        moving.set_checks("head-1", green("head-1"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "merge-refused"))
        self.assertEqual(self.pr_row(n)["state"], "open")
        self.assertIsNone(self.pr_row(n)["merge_sha"])
        self.assertNotEqual(self.task()["state"], TaskState.DONE)
        self.assertEqual(self.journal_count("merge-refused"), 1)


class ClosureWithoutMerge(LifecycleCase):
    def test_a_pr_closed_without_a_merge_is_never_success(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", green("head-1"))
        self.gh.close(n)
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "pr-closed-unmerged"))
        pr = self.pr_row(n)
        self.assertEqual((pr["state"], pr["outcome"], pr["merge_sha"]), ("closed", "closed-unmerged", None))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.PARKED_FAILURE, StopReason.PR_CLOSED_UNMERGED))
        self.assertIsNone(latest_merge_sha(self.store))
        self.assertEqual(self.gh.merge_count, 0)
        # Nothing new is launched for it afterwards.
        self.assertEqual(self.ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 1)

    def test_a_merge_done_outside_the_controller_is_recorded_as_merged(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.merge_externally(n, "merge-outside")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "merged"))
        self.assertEqual(self.pr_row(n)["merge_sha"], "merge-outside")
        self.assertEqual(self.task()["state"], TaskState.DONE)


class RepairRounds(LifecycleCase):
    def test_a_red_head_launches_one_repair_with_fresh_context(self):
        self.make()
        n = self.open_pr()["pr_number"]
        first = self.adapter.requests[0]
        self.gh.set_checks("head-1", red("head-1"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("launched", "repair"))
        self.assertEqual(self.adapter.launch_count, 2)
        req = self.adapter.requests[1]
        self.assertIsNone(req.resume_session_id)
        self.assertEqual((req.role, req.agent, req.pr_number), (FIXER_ROLE, FIXER_ROLE, n))
        self.assertEqual(req.workspace, self.git.checkouts[0]["path"])
        self.assertEqual(self.git.checkouts[0]["expected_head"], "head-1")
        self.assertIn("shell-lint", req.prompt)
        self.assertIn("check:shell-lint:", req.prompt)
        self.assertIn("1 of 2", req.prompt)
        # Fresh context: no transcript, no previous session, no previous prompt.
        self.assertNotIn(TRANSCRIPT_MARKER, req.prompt)
        self.assertNotIn("fake-session-1", req.prompt)
        self.assertNotIn(first.prompt, req.prompt)
        self.assertNotIn("Handoff from the previous task", req.prompt)
        run = self.store.row("SELECT * FROM runs WHERE run_id=?", (r.run_id,))
        self.assertEqual((run["role"], run["pr_number"], run["native_session_id"]), (FIXER_ROLE, n, "fake-session-2"))
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 1)
        self.assertEqual(self.task()["state"], TaskState.RUNNING)
        # The repair completes: its push is the new head, observed next; green there merges.
        r = self.ctl.tick()
        self.assertEqual(r.action, "settled")
        self.assertEqual(self.pr_row(n)["head_sha"], "head-2")
        self.assertEqual(self.task()["state"], TaskState.PR_OPEN)
        self.gh.set_checks("head-2", green("head-2"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "merged"))
        self.assertEqual(self.task()["state"], TaskState.DONE)

    def test_repair_rounds_are_bounded_by_the_policy(self):
        self.make()
        n = self.open_pr()["pr_number"]
        for head in ("head-1", "head-2"):
            self.gh.set_checks(head, red(head))
            self.assertEqual(self.ctl.tick().reason, "repair")
            self.assertEqual(self.ctl.tick().action, "settled")
        self.gh.set_checks("head-3", red("head-3"))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "parked-repair-rounds"))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.PARKED_FAILURE, StopReason.REPAIR_ROUNDS_EXHAUSTED))
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 2)
        self.assertEqual(self.adapter.launch_count, 3)
        # Parked: the open PR still blocks dispatch (PRs are serial) but is not parked again, and no
        # launch follows; the operator closes or requeues.
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertEqual(self.adapter.launch_count, 3)
        self.assertEqual(self.gh.merge_count, 0)
        self.assertEqual(self.journal_count("task-state"), self.journal_count("task-state"))
        before = self.store.row("SELECT COUNT(*) AS n FROM journal WHERE kind='task-state'")["n"]
        self.ctl.tick()
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM journal WHERE kind='task-state'")["n"], before)
        # The operator closes the PR: recorded as closed-unmerged, then the queue is free.
        self.gh.close(n)
        self.assertEqual(self.ctl.tick().reason, "pr-closed-unmerged")
        self.assertEqual(self.ctl.tick().action, "idle")

    def test_a_repair_that_changes_nothing_spends_its_round(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", red("head-1"))
        self.assertEqual(self.ctl.tick().reason, "repair")
        self.git.no_change = True
        self.assertEqual(self.ctl.tick().action, "settled")
        self.assertEqual(self.pr_row(n)["head_sha"], "head-1")
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 1)
        self.assertEqual(self.task()["state"], TaskState.PR_OPEN)
        self.assertEqual(self.journal_count("no-change-to-propose"), 1)

    def test_a_repair_run_is_a_writer_and_blocks_dispatch(self):
        self.make()
        self.open_pr()
        self.gh.set_checks("head-1", red("head-1"))
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        self.assertEqual(self.ctl.tick().reason, "repair")
        self.add_task("t2")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.WRITER_BUSY))
        self.assertEqual(self.adapter.launch_count, 2)


class SerialPrsAndTheNextBase(LifecycleCase):
    def test_nothing_new_launches_while_a_pr_is_open(self):
        self.make()
        self.open_pr()
        self.add_task("t2")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertEqual(self.adapter.launch_count, 1)
        self.assertEqual(self.task("t2")["state"], TaskState.QUEUED)

    def test_the_next_task_uses_the_merge_sha_and_a_compact_handoff(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", green("head-1"))
        self.assertEqual(self.ctl.tick().reason, "merged")
        merge_sha = self.pr_row(n)["merge_sha"]
        first = self.adapter.requests[0]
        self.add_task("t2")
        r = self.ctl.tick()
        self.assertEqual(r.action, "launched")
        req = self.adapter.requests[1]
        self.assertEqual(req.base_sha, merge_sha)
        self.assertEqual(self.git.prepared[1]["base_sha"], merge_sha)
        self.assertEqual(self.task("t2")["base_sha"], merge_sha)
        self.assertEqual(req.branch, "auto/t2")
        self.assertIsNone(req.resume_session_id)
        self.assertIn(f"PR #{n} merged as {merge_sha}", req.handoff)
        self.assertIn(req.handoff, req.prompt)
        self.assertNotIn(TRANSCRIPT_MARKER, req.prompt)
        self.assertNotIn("fake-session-1", req.prompt)
        self.assertNotIn(first.prompt, req.prompt)

    def test_after_a_closed_unmerged_pr_the_next_task_does_not_use_its_head(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.close(n)
        self.assertEqual(self.ctl.tick().reason, "pr-closed-unmerged")
        self.add_task("t2")
        self.assertEqual(self.ctl.tick().action, "launched")
        req = self.adapter.requests[1]
        self.assertEqual(req.base_sha, "base-0000000")
        self.assertIn("closed-unmerged", req.handoff)
        self.assertIn("nothing from it landed", req.handoff)


class DuplicateEvents(LifecycleCase):
    def test_duplicate_pr_events_create_no_second_pr_and_no_second_worker(self):
        self.make()
        n = self.open_pr()["pr_number"]
        runs_before = self.store.row("SELECT COUNT(*) AS n FROM runs")["n"]
        for _ in range(3):
            out = self.ctl.handle_event("evt-opened", "github", "pr-opened", "t1", {"number": n, "branch": "auto/t1"})
        self.assertTrue(out["duplicate"])
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM prs")["n"], 1)
        self.assertEqual(self.gh.create_count, 1)
        self.gh.set_checks("head-1", green("head-1"))
        first = self.ctl.handle_event("evt-green", "github", "checks-green", "t1", {"sha": "head-1"})
        self.assertEqual(first["action"], "merged")
        again = self.ctl.handle_event("evt-green", "github", "checks-green", "t1", {"sha": "head-1"})
        self.assertTrue(again["duplicate"]); self.assertIsNone(again["action"])
        self.assertEqual(self.gh.merge_count, 1)
        self.ctl.handle_event("evt-merged", "github", "merged", "t1", {}); self.ctl.handle_event("evt-merged", "github", "merged", "t1", {})
        self.assertEqual(self.journal_count("pr-merged"), 1)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs")["n"], runs_before)
        self.assertEqual(self.adapter.launch_count, 1)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM events")["n"], 3)

    def test_a_repeated_red_event_launches_one_repair_not_two(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", red("head-1"))
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        a = self.ctl.handle_event("evt-red", "github", "checks-red", "t1", {"sha": "head-1"})
        self.assertEqual(a["action"], "launched")
        b = self.ctl.handle_event("evt-red", "github", "checks-red", "t1", {"sha": "head-1"})
        self.assertTrue(b["duplicate"])
        c = self.ctl.handle_event("evt-red-2", "github", "checks-red", "t1", {"sha": "head-1"})
        self.assertEqual(c["action"], "worker-active")
        self.assertEqual(self.adapter.launch_count, 2)
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 1)

    def test_a_repeated_closed_event_records_one_closure(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.close(n)
        self.ctl.handle_event("evt-closed", "github", "closed", "t1", {})
        self.ctl.handle_event("evt-closed", "github", "closed", "t1", {})
        self.ctl.handle_event("evt-closed-2", "github", "closed", "t1", {})
        self.assertEqual(self.journal_count("pr-closed-unmerged"), 1)
        self.assertEqual(self.task()["state"], TaskState.PARKED_FAILURE)

    def test_pr_opened_adopts_an_open_pr_for_the_branch_without_creating_one(self):
        self.make()
        self.add_task()
        self.ctl.tick()                       # launched; the worker is still running
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        self.gh.branches["auto/t1"] = "head-x"
        pre = self.gh.pr_create("IanFrelinger/Ashlar", "auto/t1", "master", "opened by hand", "")
        self.assertEqual(self.ctl.tick().reason, StopReason.WRITER_BUSY)
        self.adapter.default_status = done()
        self.assertEqual(self.ctl.tick().action, "settled")
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM prs")["n"], 1)
        self.assertEqual(self.pr_row(pre.number)["task_id"], "t1")
        self.assertEqual(self.gh.create_count, 1)


class DryRunAndRestart(LifecycleCase):
    def test_state_survives_a_restart_mid_lifecycle(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.store.close()
        self.make(adapter=FakeAdapter(), store=Store.open(self.state_path))
        self.gh.set_checks("head-1", green("head-1"))
        self.assertEqual(self.ctl.tick().reason, "merged")
        self.assertEqual(self.pr_row(n)["state"], "merged")


class ResumeKeepsTheWorkspace(LifecycleCase):
    """The PR 4 pilot (2026-10-11): attempt 1 of `pilot-1` edited a file and stopped at the turn ceiling; attempt 2
    resumed the session in a FRESH clone, found none of its edits, and redid the work. A resumed session now
    keeps the workspace its capped run was editing; only a missing or empty workspace is cloned afresh."""

    def ceiling(self):
        return RunStatus(state=RunState.FAILED, stop_reason=StopReason.INVOCATION_CEILING, consumed_units=12.0,
                         result={"blocker": "turn ceiling reached"})

    def first_capped_run(self):
        """Launch, let the worker edit a file, and settle the run at the ceiling. Returns the workspace."""
        self.make(adapter=FakeAdapter(script=[{"launch": "ok", "status": self.ceiling()}]))
        self.add_task()
        self.assertEqual(self.ctl.tick().action, "launched")
        ws = self.store.row("SELECT workspace FROM runs")["workspace"]
        self.assertTrue(os.path.isdir(ws))
        with open(os.path.join(ws, "edited.txt"), "w", encoding="utf-8") as f:
            f.write("the worker's edit so far\n")
        self.assertEqual(self.ctl.tick().action, "settled")
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.QUEUED, StopReason.INVOCATION_CEILING))
        return ws

    # Kills: `if reused:` -> `if False:` in prepare_implementation (a second clone at a new path, the edit gone
    # from the run's workspace, no `workspace-reused` row).
    def test_a_resumed_session_keeps_the_workspace_its_capped_run_edited(self):
        ws = self.first_capped_run()
        r = self.ctl.tick()
        self.assertEqual(r.action, "launched")
        req = self.adapter.requests[1]
        self.assertEqual(req.resume_session_id, "fake-session-1")
        self.assertEqual(req.workspace, ws)
        self.assertEqual(self.store.row("SELECT workspace FROM runs WHERE run_id=?", (r.run_id,))["workspace"], ws)
        self.assertEqual(len(self.git.prepared), 1)
        self.assertTrue(os.path.isfile(os.path.join(ws, "edited.txt")))
        self.assertEqual(self.journal_count("workspace-reused"), 1)
        row = self.store.row("SELECT detail FROM journal WHERE kind='workspace-reused'")
        detail = json.loads(row["detail"])
        self.assertEqual((detail["workspace"], detail["branch"]), (ws, "auto/t1"))
        self.assertEqual(detail["from_run"], self.adapter.requests[0].run_id)
        self.assertIn("edits so far are still in the same workspace", req.prompt)
        self.assertIn(ws, req.prompt)
        self.assertIn("auto/t1", req.prompt)
        self.assertNotIn(TRANSCRIPT_MARKER, req.prompt)
        # The resumed run completes: its workspace, with the edit, is what gets committed and pushed.
        self.assertEqual(self.ctl.tick().action, "settled")
        self.assertEqual(self.git.pushes[0]["path"], ws)
        self.assertEqual(self.task()["state"], TaskState.PR_OPEN)

    # Kills: `reusable_workspace` returning the path of an empty directory (`if not os.listdir(...)` dropped):
    # the resumed launch would be handed an empty workspace instead of a clone.
    def test_an_empty_or_missing_workspace_is_cloned_afresh_and_the_session_still_resumes(self):
        ws = self.first_capped_run()
        os.remove(os.path.join(ws, "edited.txt"))
        self.assertEqual(os.listdir(ws), [])
        r = self.ctl.tick()
        self.assertEqual(r.action, "launched")
        req = self.adapter.requests[1]
        self.assertEqual(req.resume_session_id, "fake-session-1")
        self.assertNotEqual(req.workspace, ws)
        self.assertEqual(len(self.git.prepared), 2)
        self.assertEqual(self.git.prepared[1]["path"], req.workspace)
        self.assertEqual(self.journal_count("workspace-reused"), 0)
        self.assertIn("Handoff from the previous task", req.prompt)


if __name__ == "__main__":
    unittest.main()
