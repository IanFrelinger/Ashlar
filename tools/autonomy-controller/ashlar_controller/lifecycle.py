"""The PR lifecycle (PR 3): from a completed implementation run to a merged PR, and the next task's base.

The worker only EDITS files in its workspace (a headless CLI under `acceptEdits` cannot run git or gh).
The controller does everything around it, and records every step:

  1. before an implementation launch it clones the base SHA onto a fresh branch (`gitops.prepare_workspace`)
     and hands the worker that workspace with a compact handoff (facts about the previous PR, never its
     transcript);
  2. when the run completes it commits and pushes the workspace (`gitops.commit_and_push`); no diff means
     `no-change-to-propose`, a diagnosed failure through the same retry-once-then-park counter;
  3. it opens the PR idempotently (`prs.open_or_reuse_pr`): an existing row for the task or an open PR for
     the branch is reused, never duplicated; the task moves to `pr-open`;
  4. on every tick it observes each open PR BY HEAD SHA: the PR's current head is read, the required checks
     are fetched for that commit alone, and a green run on an earlier head never counts;
  5. every required check green on the current head: `gh pr merge --squash --match-head-commit <head>`,
     the merge SHA is recorded, the task is `done` (`pr-merged`);
  6. a red required check: one repair round, at most `limits.repair_rounds` per PR, as a NEW run
     (`role=readiness-fixer`, `resume_session_id=None`) in a fresh clone of the PR head, whose prompt is
     `prs.compact_handoff` (identities and evidence; no transcript, no session, no previous prompt);
     the repair's commit is pushed and the new head is observed next;
  7. a PR closed without a merge is `outcome=closed-unmerged` and the task parks: never success;
  8. PRs are serial: while a PR is open nothing new launches (`pr-open-blocks-dispatch`), and the next task's
     base is the newest recorded merge SHA.

PR events (`pr-opened`, `checks-green`, `merged`, `closed`) are hints: a new event id triggers one
observation of the task's PR, which is idempotent; a repeated id does nothing at all.
"""
from __future__ import annotations

import json
import os
from typing import Any, Dict, Optional

from . import budget, prs, readiness
from .models import (BudgetExhausted, LaunchOutcomeUnknown, LaunchRefused, LaunchRequest, PolicyError, RunState,
                     StopReason, TaskState)
from .store import utcnow_iso

PR_EVENT_KINDS = ("pr-opened", "checks-green", "checks-red", "merged", "closed", "pr-head")
IMPLEMENTATION_PROMPT_LIMIT = 6000


def implementation_prompt(task_row, base_sha: str, base_branch: str, branch: str, workspace: str, handoff: str) -> str:
    """The implementation worker's entire brief. Built from the task row and the PR ledger only."""
    head = f"card #{task_row['card_number']}: {task_row['title']}" if task_row["card_number"] else str(task_row["title"])
    text = (
        f"{head}\n\n"
        f"Workspace: {workspace}\n"
        f"It is a clone of {base_branch} at {base_sha} on the branch {branch}. Edit files in the workspace only. "
        "Do not run git, gh or dotnet: the controller commits your changes, pushes the branch and opens the "
        "pull request. Keep the change minimal and leave a note of what you verified in your final message.\n\n"
        f"Handoff from the previous task: {handoff}\n"
    )
    return text[:IMPLEMENTATION_PROMPT_LIMIT]


def resume_prompt(task_row, base_sha: str, base_branch: str, branch: str, workspace: str, handoff: str) -> str:
    """The brief for a launch that RESUMES the session of a run that stopped at the turn ceiling, in the
    workspace that run was editing. The session already holds the task; this says where it is and that
    the edits so far are still there. Built from the task row and the PR ledger only."""
    head = f"card #{task_row['card_number']}: {task_row['title']}" if task_row["card_number"] else str(task_row["title"])
    text = (
        "Continue the task: the previous invocation stopped at the turn ceiling. "
        f"Your edits so far are still in the same workspace: {workspace}\n"
        f"It is a clone of {base_branch} at {base_sha} on the branch {branch}. Edit files in the workspace only. "
        "Do not run git, gh or dotnet: the controller commits your changes, pushes the branch and opens the "
        "pull request. Keep the change minimal and leave a note of what you verified in your final message.\n\n"
        f"The task: {head}\n"
        f"Handoff from the previous task: {handoff}\n"
    )
    return text[:IMPLEMENTATION_PROMPT_LIMIT]


def reusable_workspace(run_row) -> Optional[str]:
    """The workspace of the run whose session the next launch resumes, when it is still on disk with
    content in it (the worker's edits); otherwise None, and the caller clones afresh. An empty directory
    is not reused: a fresh clone needs an empty or absent path, and there is nothing in it to keep."""
    if run_row is None:
        return None
    workspace = run_row["workspace"]
    if not workspace or not os.path.isdir(workspace):
        return None
    try:
        if not os.listdir(workspace):
            return None
    except OSError:
        return None
    return workspace


class PrLifecycle:
    """Owned by the Controller when a GhClient is configured. `git` is the gitops module or a stand-in with
    `prepare_workspace`, `checkout_pr_head` and `commit_and_push`."""

    def __init__(self, ctl, gh, git, workspaces_dir: str):
        self.ctl = ctl
        self.gh = gh
        self.git = git
        self.workspaces_dir = os.path.abspath(workspaces_dir)

    # ----- helpers -------------------------------------------------------------------------------

    @property
    def store(self):
        return self.ctl.store

    @property
    def cfg(self) -> Dict[str, Any]:
        return self.ctl.policy.pr

    def branch_for(self, task_row) -> str:
        return task_row["branch"] or f"{self.cfg['branch_prefix']}{task_row['task_id']}"

    def workspace_for(self, run_id: str) -> str:
        return os.path.join(self.workspaces_dir, run_id)

    def current_base(self, task_row) -> Optional[str]:
        """The newest recorded merge SHA wins over the task's own base, which wins over the remote tip."""
        merged = prs.latest_merge_sha(self.store)
        if merged:
            return merged
        if task_row["base_sha"]:
            return task_row["base_sha"]
        return self.gh.branch_head(self.cfg["repo"], self.cfg["base_branch"])

    def handoff_text(self) -> str:
        """Compact: the previous PR's number, outcome and merge SHA. Never a transcript or a session."""
        row = self.store.row("SELECT pr_number, outcome, merge_sha, head_sha FROM prs WHERE state != 'open' "
                             "ORDER BY updated_at DESC, rowid DESC LIMIT 1")
        if row is None:
            return "none (first task on this base)"
        if row["outcome"] == "merged":
            return f"PR #{row['pr_number']} merged as {row['merge_sha']}; start from that base"
        return f"PR #{row['pr_number']} ended {row['outcome']} at head {row['head_sha']}; nothing from it landed"

    def open_pr_rows(self):
        return self.store.rows("SELECT * FROM prs WHERE state='open' ORDER BY created_at, pr_number")

    # ----- 1. before an implementation launch -----------------------------------------------------

    def prepare_implementation(self, task_row, run_id: str, resume_run=None) -> Dict[str, Any]:
        """Clone the base onto the task's branch. Records `tasks.base_sha` and `tasks.branch`. Raises on a git
        failure (the caller settles the run as launch-failed).

        `resume_run` is the run row whose session this launch resumes (the previous run stopped at the turn
        ceiling). When that run's workspace is still on disk with content, it is REUSED, not cloned over: the
        resumed session's edits are in it, and a fresh clone would discard them. The PR 4 pilot (2026-10-11)
        did exactly that: attempt 1 of `pilot-1` edited `docs/EgressInventory.md` and stopped at the ceiling;
        attempt 2 resumed the session in a fresh clone, found none of its edits, and redid the work. The base
        and branch are the ones the task recorded when that workspace was prepared."""
        now = self.ctl.clock()
        reused = reusable_workspace(resume_run)
        if reused:
            base = task_row["base_sha"] or self.current_base(task_row)
            branch = self.branch_for(task_row)
            with self.store.tx():
                self.store.journal("workspace-reused", task_id=task_row["task_id"], run_id=run_id,
                                   detail={"from_run": resume_run["run_id"], "base_sha": base, "branch": branch,
                                           "workspace": reused}, now=now)
            handoff = self.handoff_text()
            return {"base_sha": base, "branch": branch, "workspace": reused, "handoff": handoff,
                    "reused_from": resume_run["run_id"],
                    "prompt": resume_prompt(task_row, base or "", self.cfg["base_branch"], branch, reused, handoff)}
        base = self.current_base(task_row)
        if not base:
            raise RuntimeError(f"no base SHA for task {task_row['task_id']}: no merged PR, no task base, "
                               f"and {self.cfg['base_branch']} not found on {self.cfg['repo']}")
        branch = self.branch_for(task_row)
        workspace = self.workspace_for(run_id)
        self.git.prepare_workspace(workspace, self.cfg["remote_url"], base, branch, reference=self.cfg.get("reference_checkout"))
        with self.store.tx():
            self.store.conn.execute("UPDATE tasks SET base_sha=?, branch=?, updated_at=? WHERE task_id=?",
                                    (base, branch, utcnow_iso(now), task_row["task_id"]))
            self.store.journal("workspace-prepared", task_id=task_row["task_id"], run_id=run_id,
                               detail={"base_sha": base, "branch": branch, "workspace": workspace}, now=now)
        handoff = self.handoff_text() + self.transfer_note(task_row, run_id)
        return {"base_sha": base, "branch": branch, "workspace": workspace, "handoff": handoff,
                "prompt": implementation_prompt(task_row, base, self.cfg["base_branch"], branch, workspace, handoff)}

    def transfer_note(self, task_row, run_id: str) -> str:
        """After a provider transfer, one line of facts for the new provider's first run: which provider and
        run came before and how it stopped. Never its transcript, prompt, session or workspace. `run_id` is
        the run being prepared (already reserved, so it is excluded)."""
        last = self.store.row("SELECT run_id, provider, stop_reason FROM runs WHERE task_id=? AND run_id != ? "
                              "ORDER BY reserved_at DESC, rowid DESC LIMIT 1", (task_row["task_id"], run_id))
        if last is None or last["provider"] == task_row["provider"]:
            return ""
        return (f"; this task was transferred from {last['provider']} after run {last['run_id']} "
                f"({last['stop_reason'] or 'unsettled'}); nothing from that run is in this workspace")

    # ----- 2./3. after a completed run --------------------------------------------------------------

    def after_completed(self, run, limits: Dict[str, Any]) -> Dict[str, Any]:
        """Commit and push the run's workspace; open (or reuse) the PR for an implementation run, or record
        the new head for a repair run. Returns what happened for the settlement record."""
        task_id = run["task_id"]
        task = self.store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))
        now = self.ctl.clock()
        branch = self.branch_for(task)
        workspace = run["workspace"]
        role = run["role"]
        if not workspace or not os.path.isdir(workspace):
            # A completed run with no workspace on disk cannot be proposed: diagnosed, retried once, then parked.
            identity = f"pr:workspace-missing:{task_id}"
            out = self.ctl._apply_failure_retry(task_id, run["pool_id"], identity, StopReason.NO_CHANGE_TO_PROPOSE,
                                                int(limits["same_failure_retries"]))
            out["lifecycle"] = "workspace-missing"
            return out
        message = (f"{task['title'] or task_id}\n\nTask {task_id}" + (f", card #{task['card_number']}" if task["card_number"] else "")
                   + f", run {run['run_id']} ({role}) by the budgeted autonomy controller.\n\n"
                   + prs.attribution_trailers(run))
        head = self.git.commit_and_push(workspace, branch, message)
        if head is None:
            self.store.journal("no-change-to-propose", task_id=task_id, run_id=run["run_id"],
                               detail={"role": role, "workspace": workspace, "branch": branch}, now=now)
            if role == readiness.FIXER_ROLE:
                # The fixer changed nothing. The PR stays open on the same head; the round is spent, and the
                # next observation sees the same red checks, so the rounds bound it.
                self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING,
                                        blocker="repair run proposed no change; the PR head is unchanged")
                return {"task_id": task_id, "action": "repair-no-change", "pr_number": run["pr_number"]}
            identity = f"pr:no-change:{task_id}"
            out = self.ctl._apply_failure_retry(task_id, run["pool_id"], identity, StopReason.NO_CHANGE_TO_PROPOSE,
                                                int(limits["same_failure_retries"]))
            out["lifecycle"] = "no-change-to-propose"
            return out
        self.store.journal("pushed", task_id=task_id, run_id=run["run_id"],
                           detail={"role": role, "branch": branch, "head_sha": head}, now=now)
        if role == readiness.FIXER_ROLE and run["pr_number"]:
            prs.record_head(self.store, int(run["pr_number"]), head, now=now)
            self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING,
                                    blocker=f"repair pushed {head}; awaiting the required checks on that head")
            return {"task_id": task_id, "action": "repair-pushed", "pr_number": run["pr_number"], "head_sha": head}
        base = task["base_sha"]
        title = f"{task['title'] or task_id}" + (f" (#{task['card_number']})" if task["card_number"] else "")
        row = prs.open_or_reuse_pr(self.store, self.gh, self.cfg["repo"], task_id, branch, self.cfg["base_branch"], base,
                                   title, prs.pr_body(task, base or "", head, run), now=now)
        prs.record_head(self.store, int(row["pr_number"]), head, now=now)
        self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING,
                                blocker=f"PR #{row['pr_number']} open at {head}; awaiting the required checks on that head")
        return {"task_id": task_id, "action": "pr-open", "pr_number": int(row["pr_number"]), "head_sha": head}

    # ----- 4.-7. observation --------------------------------------------------------------------

    def observe(self, pr_row, task_row, launch_allowed: bool = True) -> Optional[Dict[str, Any]]:
        """One observation of one open PR, by its current head. Returns a report when it acted (merged,
        closed, repair launched, parked), None when the PR is simply waiting."""
        number = int(pr_row["pr_number"])
        task_id = task_row["task_id"]
        repo = self.cfg["repo"]
        now = self.ctl.clock()
        view = self.gh.pr_view(repo, number)
        if view.state == "MERGED":
            return self._merged(number, task_id, view.merge_sha or "")
        if view.state == "CLOSED":
            prs.record_closed(self.store, number, now=now)
            self.ctl.set_task_state(task_id, TaskState.PARKED_FAILURE, stop_reason=StopReason.PR_CLOSED_UNMERGED,
                                    blocker=f"PR #{number} was closed without a merge at head {view.head_sha}; "
                                            f"nothing landed, the task is not done")
            return {"action": "pr-closed-unmerged", "pr_number": number, "task_id": task_id, "head_sha": view.head_sha}
        head = view.head_sha
        prs.record_head(self.store, number, head, now=now)
        runs = self.gh.check_runs(repo, head)
        verdict = prs.evaluate_checks(self.cfg["required_checks"], head, runs)
        self.store.journal("pr-observed", task_id=task_id,
                           detail={"pr_number": number, "head_sha": head, "verdict": verdict.verdict,
                                   "green": verdict.green, "pending": verdict.pending,
                                   "red": [f.identity for f in verdict.red]}, now=now)
        if verdict.verdict == "green":
            try:
                self.gh.pr_merge(repo, number, head)
            except prs.MergeRefused as e:
                self.store.journal("merge-refused", task_id=task_id, detail={"pr_number": number, "head_sha": head,
                                                                              "error": str(e)[:500]}, now=now)
                self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING,
                                        blocker=f"merge of PR #{number} at {head} refused: {str(e)[:300]}")
                return {"action": "merge-refused", "pr_number": number, "task_id": task_id, "head_sha": head}
            after = self.gh.pr_view(repo, number)
            if after.state == "MERGED" and after.merge_sha:
                return self._merged(number, task_id, after.merge_sha)
            self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING,
                                    blocker=f"merge of PR #{number} requested; GitHub has not reported the merge SHA yet")
            return {"action": "merge-requested", "pr_number": number, "task_id": task_id, "head_sha": head}
        if verdict.verdict == "red":
            used = int(pr_row["repair_rounds_used"])
            rounds_max = int(self.ctl.policy.limits["repair_rounds"])
            identities = [f.identity for f in verdict.red]
            if used >= rounds_max:
                if task_row["stop_reason"] == StopReason.REPAIR_ROUNDS_EXHAUSTED:
                    return None   # already parked on this verdict; the operator acts, the tick does not repeat it
                self.ctl.set_task_state(task_id, TaskState.PARKED_FAILURE, stop_reason=StopReason.REPAIR_ROUNDS_EXHAUSTED,
                                        blocker=f"PR #{number} head {head} is red ({', '.join(identities)}) and "
                                                f"{used} of {rounds_max} repair rounds are used; operator step required")
                return {"action": "parked-repair-rounds", "pr_number": number, "task_id": task_id, "head_sha": head,
                        "red": identities}
            if not launch_allowed:
                return None
            return self._launch_repair(pr_row, task_row, view, verdict, used + 1, rounds_max)
        blocker = f"PR #{number} head {head}: pending {', '.join(verdict.pending)}"
        if task_row["state"] in (TaskState.PR_OPEN,) + tuple(TaskState.DISPATCHABLE) and (
                task_row["state"] != TaskState.PR_OPEN or task_row["stop_reason"] != StopReason.PR_CHECKS_PENDING
                or task_row["blocker"] != blocker):
            # Parked and blocked tasks keep their own blocker; a waiting or open one follows the head.
            self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING, blocker=blocker)
        return None

    def _merged(self, number: int, task_id: str, merge_sha: str) -> Dict[str, Any]:
        prs.record_merged(self.store, number, merge_sha, now=self.ctl.clock())
        self.ctl.set_task_state(task_id, TaskState.DONE, stop_reason=StopReason.PR_MERGED,
                                blocker=None)
        return {"action": "merged", "pr_number": number, "task_id": task_id, "merge_sha": merge_sha}

    def _launch_repair(self, pr_row, task_row, view, verdict, round_no: int, rounds_max: int) -> Dict[str, Any]:
        """One repair round: a NEW run that resumes nothing, in a fresh clone of the PR head, briefed only by
        the compact handoff. The round is counted before the launch (a crash in between costs a round, never
        an extra launch)."""
        import uuid
        ctl = self.ctl
        now = ctl.clock()
        number = int(pr_row["pr_number"])
        task_id = task_row["task_id"]
        adapter = ctl.adapters.get(task_row["provider"])
        if adapter is None:
            return {"action": "blocked", "reason": StopReason.PROVIDER_DISABLED, "task_id": task_id, "pr_number": number}
        pool = budget.pool_row(self.store, task_row["pool_id"])
        permitted, why = budget.allowance_decision(ctl.policy, budget.latest_allowance(self.store, task_row["provider"]),
                                                   float(pool["invocation_ceiling"]))
        if not permitted:
            ctl.set_task_state(task_id, TaskState.WAITING_ALLOWANCE, stop_reason=why,
                               next_eligible_in=ctl.policy.idle["quota_wait_seconds"])
            return {"action": "blocked", "reason": why, "task_id": task_id, "pr_number": number}
        run_id = f"run-{uuid.uuid4().hex[:12]}"
        workspace = self.workspace_for(run_id)
        try:
            reservation = budget.reserve(self.store, run_id=run_id, task_id=task_id, pool_id=task_row["pool_id"],
                                         provider=task_row["provider"], policy=ctl.policy,
                                         policy_version_id=ctl.policy_version_id, holder=ctl.holder_id, now=now,
                                         role=readiness.FIXER_ROLE, workspace=workspace)
        except (BudgetExhausted, PolicyError) as e:
            reason = str(e).split(":", 1)[0]
            ctl.set_task_state(task_id, TaskState.PARKED_BUDGET, stop_reason=reason, blocker=str(e))
            return {"action": "blocked", "reason": reason, "task_id": task_id, "pr_number": number, "error": str(e)}
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET pr_number=? WHERE run_id=?", (number, run_id))
        prs.bump_repair_round(self.store, number, now=now)
        handoff = prs.compact_handoff(pr_row, verdict, round_no, rounds_max, self.cfg["base_branch"], workspace)
        request = LaunchRequest(run_id=run_id, task_id=task_id, provider=task_row["provider"],
                                model=reservation["model"], effort=reservation["effort"], speed=reservation["speed"],
                                invocation_ceiling=int(reservation["reservation"]), workspace=workspace,
                                prompt=handoff, resume_session_id=None, role=readiness.FIXER_ROLE,
                                agent=readiness.FIXER_ROLE, allowed_tools=None, base_sha=pr_row["base_sha"],
                                branch=view.head_branch, handoff=handoff, pr_number=number)
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state=?, launched_at=? WHERE run_id=?",
                                    (RunState.LAUNCHING, utcnow_iso(now), run_id))
            self.store.journal("repair-round", task_id=task_id, run_id=run_id,
                               detail={"pr_number": number, "round": round_no, "of": rounds_max, "head_sha": view.head_sha,
                                       "red": [f.identity for f in verdict.red], "resume_session_id": None}, now=now)
        try:
            self.git.checkout_pr_head(workspace, self.cfg["remote_url"], view.head_branch, view.head_sha,
                                      reference=self.cfg.get("reference_checkout"))
            result = readiness.launch_fixer(adapter, request)
        except LaunchOutcomeUnknown as e:
            with self.store.tx():
                self.store.conn.execute("UPDATE runs SET state=? WHERE run_id=?", (RunState.UNKNOWN, run_id))
                self.store.journal("launch-unknown", task_id=task_id, run_id=run_id, detail={"error": str(e)}, now=now)
            ctl.set_task_state(task_id, TaskState.NEEDS_RECONCILE, stop_reason=StopReason.LAUNCH_UNKNOWN)
            return {"action": "blocked", "reason": StopReason.LAUNCH_UNKNOWN, "task_id": task_id, "run_id": run_id}
        except LaunchRefused as e:
            budget.settle(self.store, run_id, state=RunState.FAILED, stop_reason=e.stop_reason, consumed=0.0,
                          result={"blocker": e.blocker, "refused": True}, now=now)
            state, stop_reason = ctl._refusal_target(e.stop_reason)
            if state == TaskState.QUEUED:
                state = TaskState.PR_OPEN   # the PR is still there; the next observation retries the round
            ctl.set_task_state(task_id, state, stop_reason=stop_reason, blocker=e.blocker)
            self.store.journal("launch-refused", task_id=task_id, run_id=run_id,
                               detail={"stop_reason": e.stop_reason, "blocker": e.blocker, "task_state": state}, now=now)
            return {"action": "blocked", "reason": e.stop_reason, "task_id": task_id, "run_id": run_id}
        except Exception as e:  # noqa: BLE001 - a git or spawn failure: the run is settled, the PR stays open
            budget.settle(self.store, run_id, state=RunState.FAILED, stop_reason=StopReason.LAUNCH_FAILED, consumed=0.0,
                          result={"error": str(e)}, now=now)
            ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.LAUNCH_FAILED, blocker=str(e)[:500])
            return {"action": "blocked", "reason": StopReason.LAUNCH_FAILED, "task_id": task_id, "run_id": run_id,
                    "error": str(e)}
        with self.store.tx():
            self.store.conn.execute(
                "UPDATE runs SET state=?, native_session_id=?, native_run_handle=?, workspace=? WHERE run_id=?",
                (RunState.RUNNING, result.native_session_id, result.native_run_handle, result.workspace or workspace, run_id))
            self.store.conn.execute("UPDATE tasks SET state=?, updated_at=? WHERE task_id=?",
                                    (TaskState.RUNNING, utcnow_iso(now), task_id))
            self.store.journal("launched", task_id=task_id, run_id=run_id,
                               detail={"handle": result.native_run_handle, "session": result.native_session_id,
                                       "role": readiness.FIXER_ROLE, "pr_number": number}, now=now)
        return {"action": "launched", "reason": "repair", "task_id": task_id, "run_id": run_id, "pr_number": number,
                "round": round_no, "of": rounds_max, "reservation": reservation["reservation"]}

    # ----- the tick's PR phase ----------------------------------------------------------------------

    def task_may_launch(self, task_row) -> bool:
        now = utcnow_iso(self.ctl.clock())
        if task_row["state"] in (TaskState.RUNNING, TaskState.RESERVED, TaskState.NEEDS_RECONCILE, TaskState.PARKED_BUDGET,
                                 TaskState.PARKED_DECISION, TaskState.PARKED_FAILURE, TaskState.BLOCKED_AUTH,
                                 TaskState.BLOCKED_PERMISSION, TaskState.CANCELLED, TaskState.DONE):
            return False
        if task_row["next_eligible_at"] and task_row["next_eligible_at"] > now:
            return False
        spec = self.ctl.policy.providers.get(task_row["provider"])
        if not spec or not spec.get("enabled"):
            return False
        disabled = self.store.control("disabled_providers")
        return not (disabled and task_row["provider"] in json.loads(disabled))

    def phase(self) -> Dict[str, Any]:
        """Observe every open PR once. Returns {"report": <action dict> or None, "open": <count of PRs still open>}."""
        acted = None
        still_open = 0
        for pr_row in self.open_pr_rows():
            task = self.store.row("SELECT * FROM tasks WHERE task_id=?", (pr_row["task_id"],))
            if task is None:
                continue
            if task["state"] in (TaskState.RUNNING, TaskState.RESERVED, TaskState.NEEDS_RECONCILE):
                still_open += 1
                continue
            if acted is None:
                # A parked or blocked task's PR is still watched for a merge or a closure, but nothing is
                # launched for it and it is not parked again.
                acted = self.observe(pr_row, task, launch_allowed=self.task_may_launch(task))
                if acted is not None and acted.get("action") in ("merged", "pr-closed-unmerged", "parked-repair-rounds"):
                    continue
            still_open += 1
        return {"report": acted, "open": still_open}

    # ----- events ------------------------------------------------------------------------------------

    def on_event(self, kind: str, task_id: Optional[str], payload: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """A NEW PR event (the caller has already deduplicated by id). `pr-opened` adopts an open PR for the
        task's branch; every PR kind then triggers one idempotent observation of the task's open PR."""
        if kind not in PR_EVENT_KINDS or not task_id:
            return None
        task = self.store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))
        if task is None:
            return None
        if kind == "pr-opened" and prs.open_pr_for_task(self.store, task_id) is None:
            branch = str(payload.get("branch") or self.branch_for(task))
            found = self.gh.pr_for_branch(self.cfg["repo"], branch)
            if found is not None:
                prs.open_or_reuse_pr(self.store, self.gh, self.cfg["repo"], task_id, branch, self.cfg["base_branch"],
                                     task["base_sha"], found.title, "", now=self.ctl.clock())
                self.ctl.set_task_state(task_id, TaskState.PR_OPEN, stop_reason=StopReason.PR_CHECKS_PENDING,
                                        blocker=f"PR #{found.number} adopted at {found.head_sha}")
        pr_row = prs.open_pr_for_task(self.store, task_id)
        if pr_row is None:
            return {"action": "no-open-pr", "task_id": task_id, "kind": kind}
        task = self.store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))
        if task["state"] in (TaskState.RUNNING, TaskState.RESERVED, TaskState.NEEDS_RECONCILE):
            return {"action": "worker-active", "task_id": task_id, "kind": kind}
        report = self.observe(pr_row, task, launch_allowed=self.task_may_launch(task))
        return report or {"action": "observed", "task_id": task_id, "kind": kind, "pr_number": int(pr_row["pr_number"])}
