"""The dispatch loop. One `tick` does ONE thing, taking the first that applies:

  0. refuse if the state is missing or corrupt, or if paused, or if this process does not hold the
     writer lease (dry-run only reads);
  1. settle finished runs and reconcile uncertain launches; while any launch outcome is unknown,
     nothing new is launched (investigate the original attempt before launching another);
  2. if an implementation writer is already active, do nothing more (one writer across providers);
  3. pick the first eligible task; if none, go idle with zero adapter calls;
  4. check the provider's allowance against the interactive reserve;
  5. reserve budget and record the run (one transaction), then launch; record the handle, or the
     uncertainty, immediately.

Every decision is written to the journal with its exact stop reason.

PR 3 adds the PR phase between 1 and 2 when a GhClient is configured (`lifecycle.py`): every open PR is
observed by its current head, a green head is merged, a red head gets one bounded repair round, and while
any PR is open nothing new launches (PRs are serial; the next task's base is the recorded merge SHA).
"""
from __future__ import annotations

import json
import os
import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

from . import budget, events
from .adapters.base import ProviderAdapter
from .models import (BRIEF_MAX_CHARS, BudgetExhausted, ControllerError, LaunchOutcomeUnknown, LaunchRefused, LaunchRequest,
                     LeaseError, PolicyError, RunState, StopReason, TaskState, UsageFreshness)
from .policy import Policy
from .store import Store, utcnow_iso

# The roles that edit code. One writer at a time across both, and a completed one is proposed, not done.
WRITER_ROLES = ("implementation", "readiness-fixer")
# After a cancel, a run that still polls RUNNING is polled again this many times, this far apart, before it is
# settled `cancelled`: a cancel that found the run's turn over did nothing, and its process may be exiting.
CANCEL_SETTLE_POLLS = 10
CANCEL_SETTLE_INTERVAL_SECONDS = 0.5


@dataclass
class TickReport:
    action: str                      # idle | launched | would-launch | blocked | paused | settled | refused
    reason: str
    task_id: Optional[str] = None
    run_id: Optional[str] = None
    details: Dict[str, Any] = field(default_factory=dict)
    settled: List[Dict[str, Any]] = field(default_factory=list)

    def to_json(self) -> Dict[str, Any]:
        return {"action": self.action, "reason": self.reason, "task_id": self.task_id, "run_id": self.run_id,
                "details": self.details, "settled": self.settled}


def _iso_to_epoch(s: Optional[str]) -> float:
    if not s:
        return 0.0
    return time.mktime(time.strptime(s, "%Y-%m-%dT%H:%M:%SZ")) - time.timezone


class Controller:
    def __init__(self, store: Store, policy: Policy, adapters: Dict[str, ProviderAdapter], *, holder_id: str,
                 host: Optional[str] = None, pid: Optional[int] = None, clock=None, gh=None, git=None,
                 workspaces_dir: Optional[str] = None, runs_dir: Optional[str] = None,
                 evidence_dir: Optional[str] = None, sleep=None):
        self.store = store
        self.policy = policy
        self.adapters = adapters
        self.holder_id = holder_id
        self.host = host
        self.pid = pid
        self.clock = clock or time.time
        # Only `cancel_run` waits (for a run whose process is still exiting). A test that injects its clock gets
        # no real sleep unless it injects one too.
        self.sleep = sleep or (time.sleep if clock is None else (lambda _seconds: None))
        # PR 4: where the evidence of a run lives, so `status` and the archives can name it without an
        # adapter. The Claude adapter writes <state dir>/runs/<run_id>/; archives go to <state dir>/evidence/.
        state_dir = os.path.dirname(os.path.abspath(store.path))
        self.runs_dir = os.path.abspath(runs_dir) if runs_dir else os.path.join(state_dir, "runs")
        self.evidence_dir = os.path.abspath(evidence_dir) if evidence_dir else os.path.join(state_dir, "evidence")
        self.policy_version_id: Optional[int] = None
        if not store.read_only:
            self.policy_version_id = store.record_policy(policy.version, policy.sha256, policy.source_path,
                                                         policy.to_json(), now=self.clock())
        # The PR lifecycle is on when a GhClient is given (PR 3). Without one the controller behaves as in
        # PR 1 and PR 2: a completed implementation run makes the task `done` and no PR is opened.
        self.lifecycle = None
        if gh is not None:
            from .lifecycle import PrLifecycle
            if git is None:
                from . import gitops as git
            ws = workspaces_dir or os.path.join(os.path.dirname(os.path.abspath(store.path)), "workspaces")
            self.lifecycle = PrLifecycle(self, gh, git, ws)

    # ----- tasks ---------------------------------------------------------------------------------

    def ensure_task(self, task_id: str, *, workstream: str, provider: str, card_number: Optional[int] = None,
                    title: str = "", brief: Optional[str] = None, handoff_ref: Optional[str] = None,
                    decision_refs: Optional[List[str]] = None,
                    parent_task_id: Optional[str] = None, owner_agent: str = "claude", priority: int = 100,
                    base_sha: Optional[str] = None) -> bool:
        """Create the task and its pool if absent. A child task shares its parent's pool. Returns True
        when created. `brief` (card #776), when given, is the PR title and the commit subject of the task's
        controller-opened PR; `title` stays the worker's instruction. A brief over BRIEF_MAX_CHARS is refused
        before anything is written."""
        if brief is not None and len(brief) > BRIEF_MAX_CHARS:
            raise ControllerError(f"the brief of task {task_id} is {len(brief)} characters; a brief is at most "
                                  f"{BRIEF_MAX_CHARS} (it is the PR title and the commit subject)")
        now = self.clock()
        if self.store.row("SELECT 1 FROM tasks WHERE task_id=?", (task_id,)):
            return False
        spec = self.policy.provider(provider)
        if parent_task_id:
            parent = self.store.row("SELECT pool_id FROM tasks WHERE task_id=?", (parent_task_id,))
            if parent is None:
                raise PolicyError(f"parent task {parent_task_id} does not exist")
            pool_id = parent["pool_id"]
        else:
            pool_id = f"pool:{task_id}"
            budget.create_pool(self.store, pool_id, spec["unit"], self.policy, self.policy_version_id, now=now)
        at = utcnow_iso(now)
        with self.store.tx():
            self.store.conn.execute(
                "INSERT INTO tasks(task_id, card_number, workstream, title, brief, handoff_ref, decision_refs, owner_agent, "
                "provider, parent_task_id, pool_id, state, priority, base_sha, created_at, updated_at) "
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (task_id, card_number, workstream, title, brief, handoff_ref, json.dumps(decision_refs or []), owner_agent,
                 provider, parent_task_id, pool_id, TaskState.QUEUED, priority, base_sha, at, at))
            self.store.journal("task-created", task_id=task_id,
                               detail={"pool_id": pool_id, "parent": parent_task_id, "provider": provider}, now=now)
        return True

    def ingest_event(self, event_id: str, source: str, kind: str, task_id: Optional[str],
                     payload: Optional[Dict[str, Any]] = None) -> bool:
        """Record an event once. Returns False for a duplicate, which the caller must then ignore."""
        return events.ingest(self.store, event_id, source, kind, task_id, payload, now=self.clock())

    def handle_event(self, event_id: str, source: str, kind: str, task_id: Optional[str],
                     payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        """Ingest, and act once on a NEW PR event (one idempotent observation of the task's open PR). A
        repeated event id is reported as a duplicate and causes no observation, no PR and no launch."""
        new = self.ingest_event(event_id, source, kind, task_id, payload)
        out: Dict[str, Any] = {"event_id": event_id, "new": new, "duplicate": not new, "kind": kind, "action": None}
        if new and self.lifecycle is not None:
            acted = self.lifecycle.on_event(kind, task_id, payload or {})
            if acted:
                out.update({"action": acted.get("action"), "detail": acted})
            events.mark_handled(self.store, event_id, now=self.clock())
        return out

    def set_task_state(self, task_id: str, state: str, *, stop_reason: Optional[str] = None,
                       blocker: Optional[str] = None, next_eligible_in: Optional[float] = None) -> None:
        now = self.clock()
        next_at = utcnow_iso(now + next_eligible_in) if next_eligible_in is not None else None
        with self.store.tx():
            self.store.conn.execute(
                "UPDATE tasks SET state=?, stop_reason=?, blocker=?, next_eligible_at=?, updated_at=? WHERE task_id=?",
                (state, stop_reason, blocker, next_at, utcnow_iso(now), task_id))
            self.store.journal("task-state", task_id=task_id,
                               detail={"state": state, "stop_reason": stop_reason, "blocker": blocker,
                                       "next_eligible_at": next_at}, now=now)

    # ----- controls --------------------------------------------------------------------------------

    def pause(self, reason: str) -> None:
        self.store.set_control("paused", json.dumps({"reason": reason, "at": utcnow_iso(self.clock())}), now=self.clock())

    def resume(self) -> None:
        self.store.clear_control("paused")

    def paused(self) -> Optional[Dict[str, Any]]:
        v = self.store.control("paused")
        return json.loads(v) if v else None

    # ----- settlement and recovery -----------------------------------------------------------------

    def _apply_outcome(self, run, status) -> Dict[str, Any]:
        """Settle a finished run and move its task to the state its stop reason demands."""
        task_id = run["task_id"]
        limits = json.loads(run["limits_json"])
        consumed = status.consumed_units
        if status.usage is not None:
            delta = budget.record_usage(self.store, status.usage, run["run_id"], now=self.clock())
            if consumed is None and delta is not None and status.usage.freshness == UsageFreshness.FRESH:
                consumed = delta
        for obs in status.observations:
            # Further kinds (an unavailable allowance, an estimated API value): recorded by kind,
            # with their per-session delta, and never summed into the consumption figure.
            budget.record_usage(self.store, obs, run["run_id"], now=self.clock())
        reason = status.stop_reason or (StopReason.COMPLETED if status.state == RunState.FINISHED else StopReason.TASK_FAILED)
        run_state = status.state if status.state in (RunState.FINISHED, RunState.FAILED, RunState.CANCELLED) else RunState.FAILED
        settled = budget.settle(self.store, run["run_id"], state=run_state, stop_reason=reason, consumed=consumed,
                                failure_identity=status.failure_identity, result=status.result, now=self.clock())
        if settled.get("already_settled"):
            # A settled run never moves its task again: the run row keeps its first settlement, and a second
            # one (an operator's `run settle`, or a race with another settlement) changes no task state.
            settled["task_id"] = task_id
            return settled
        idle = self.policy.idle
        blocker = status.result.get("blocker") if status.result else None
        if reason == StopReason.COMPLETED:
            if self.lifecycle is not None and run["role"] in WRITER_ROLES:
                # PR 3: completed means "proposed", not "done". The workspace is committed and pushed and the
                # PR opened (or its new head recorded); only a merge on the current head makes the task done.
                try:
                    settled["lifecycle"] = self.lifecycle.after_completed(run, limits)
                except Exception as e:  # noqa: BLE001 - GitError, GhError, OSError: charged, but not proposed
                    settled["lifecycle"] = self._propose_failed(run, e)
            else:
                self.set_task_state(task_id, TaskState.DONE, stop_reason=reason)
        elif reason == StopReason.QUOTA_EXHAUSTED:
            # Checkpoint and wait. Never a fallback launch on another route.
            self.set_task_state(task_id, TaskState.WAITING_QUOTA, stop_reason=reason,
                                blocker=blocker or "provider allowance exhausted; waiting for an observed reset",
                                next_eligible_in=idle["quota_wait_seconds"])
        elif reason in (StopReason.RATE_LIMITED, StopReason.TRANSIENT):
            waits = int(self.store.row("SELECT COUNT(*) AS n FROM journal WHERE task_id=? AND kind='task-state' "
                                       "AND detail LIKE '%\"waiting-rate-limit\"%'", (task_id,))["n"])
            backoffs = idle["rate_limit_backoff_seconds"]
            delay = backoffs[min(waits, len(backoffs) - 1)]
            self.set_task_state(task_id, TaskState.WAITING_RATE_LIMIT, stop_reason=reason, next_eligible_in=delay)
        elif reason == StopReason.AUTH_MISSING:
            self.set_task_state(task_id, TaskState.BLOCKED_AUTH, stop_reason=reason,
                                blocker=blocker or "authentication missing for the provider; operator step required")
        elif reason == StopReason.PERMISSION_MISSING:
            self.set_task_state(task_id, TaskState.BLOCKED_PERMISSION, stop_reason=reason,
                                blocker=blocker or "a required permission is missing; operator step required")
        elif reason == StopReason.CANCELLED:
            self.set_task_state(task_id, TaskState.CANCELLED, stop_reason=reason,
                                blocker=f"cancelled; workspace {run['workspace'] or '(none)'} preserved for inspection")
        elif reason == StopReason.DECISION_MISSING:
            self.set_task_state(task_id, TaskState.PARKED_DECISION, stop_reason=reason,
                                blocker=blocker or "a product decision is missing (the run did not name it)")
        elif reason == StopReason.NO_FINAL_RESULT:
            self.set_task_state(task_id, TaskState.NEEDS_RECONCILE, stop_reason=reason,
                                blocker="worker exited without a final result; reconcile process, workspace, "
                                        "transcript and remote state before retrying")
        elif reason == StopReason.INVOCATION_CEILING:
            # The provider stopped at the turn limit. Not a failure: the task goes straight back to
            # the queue and the next launch resumes the same session. The failure-retry counter is not
            # touched; the pool's attempts and task ceiling bound how often this can happen.
            session = run["native_session_id"]
            self.set_task_state(task_id, TaskState.QUEUED, stop_reason=reason,
                                blocker=f"turn ceiling reached; next attempt resumes session {session or '(none recorded)'}")
        elif reason == StopReason.MODEL_UNRECOGNIZED:
            model = (status.result or {}).get("model") or run["model"]
            self.set_task_state(task_id, TaskState.PARKED_DECISION, stop_reason=reason,
                                blocker=f"model id {model} is not recognised by the provider CLI; the policy must name "
                                        f"a recognised model before this task runs again")
        elif reason == StopReason.FUNDING_ROUTE_MISMATCH:
            self.set_task_state(task_id, TaskState.BLOCKED_AUTH, stop_reason=reason,
                                blocker=blocker or "the provider would bill a funding route the policy did not select")
        else:  # task-failed and anything unrecognised
            self._apply_failure_retry(task_id, run["pool_id"], status.failure_identity, reason,
                                      int(limits["same_failure_retries"]))
        settled["task_id"] = task_id
        return settled

    def _propose_failed(self, run, error: Exception) -> Dict[str, Any]:
        """A completed writer run whose commit, push or PR failed. The run stays settled and charged as it was;
        the error is journalled (`propose-failed`) instead of escaping as a traceback, and the task goes to
        `needs-reconcile` so nothing launches for it until an operator has looked at the workspace and the
        remote (the branch may have been pushed, or the PR opened, before the failure)."""
        text = str(error).strip() or type(error).__name__
        first = text.splitlines()[0]
        self.store.journal("propose-failed", task_id=run["task_id"], run_id=run["run_id"],
                           detail={"run_id": run["run_id"], "role": run["role"], "error_type": type(error).__name__,
                                   "error": text[:2000], "workspace": run["workspace"]}, now=self.clock())
        self.set_task_state(run["task_id"], TaskState.NEEDS_RECONCILE, stop_reason=StopReason.PROPOSE_FAILED,
                            blocker=f"completed run not proposed: {first[:500]}")
        return {"action": "propose-failed", "error": text[:2000]}

    def _apply_failure_retry(self, task_id: str, pool_id: str, failure_identity: Optional[str], reason: str,
                             same_failure_retries: int) -> Dict[str, Any]:
        """The same diagnosed failure is retried `same_failure_retries` times across every cycle and
        provider (the counter lives in the pool, so it survives restarts and task splits); then the
        task is parked."""
        identity = failure_identity or "unidentified-failure"
        allowed, used = budget.failure_retry_allowed(self.store, pool_id, identity, same_failure_retries)
        if allowed:
            budget.bump_failure_retry(self.store, pool_id, identity)
            self.set_task_state(task_id, TaskState.QUEUED, stop_reason=reason,
                                blocker=f"retrying diagnosed failure {identity} ({used + 1} of {same_failure_retries})")
            return {"task_id": task_id, "action": "requeued", "identity": identity, "retry": used + 1}
        self.set_task_state(task_id, TaskState.PARKED_FAILURE, stop_reason=StopReason.SAME_FAILURE_RETRY_EXHAUSTED,
                            blocker=f"failure {identity} already retried {used} time(s); the same diagnosed "
                                    f"failure is not retried again on any cycle or provider")
        return {"task_id": task_id, "action": "parked", "identity": identity, "retry": used}

    def evaluate_readiness(self, task_id: str, report) -> Dict[str, Any]:
        """Apply a readiness gate report (the shape of readiness.GateReport: `failures`, each with a
        stable `identity`, plus `passed`, `source`, `commit`) to a task. No failures: the task is done,
        unless it has an open PR: then only a merge on the PR's current head finishes it, the PR phase
        decides, and the task is left as it is (action `pr-open`). Failures: the first failure's identity
        goes through the same retry counter as a diagnosed task failure, so the same gate failure is
        retried once and then parked, on any cycle and after any restart."""
        task = self.store.row("SELECT pool_id FROM tasks WHERE task_id=?", (task_id,))
        if task is None:
            raise PolicyError(f"no task {task_id}")
        failures = [{"gate": f.gate, "identity": f.identity, "evidence": f.evidence} for f in report.failures]
        passed = list(report.passed)
        open_pr = self.store.row("SELECT pr_number FROM prs WHERE task_id=? AND state='open' ORDER BY rowid DESC LIMIT 1",
                                 (task_id,))
        if not failures and open_pr is not None:
            out = {"task_id": task_id, "action": "pr-open", "identity": None, "retry": 0,
                   "pr_number": int(open_pr["pr_number"])}
        elif not failures:
            self.set_task_state(task_id, TaskState.DONE, stop_reason=StopReason.COMPLETED)
            out = {"task_id": task_id, "action": "completed", "identity": None, "retry": 0}
        else:
            out = self._apply_failure_retry(task_id, task["pool_id"], failures[0]["identity"], StopReason.TASK_FAILED,
                                            int(self.policy.limits["same_failure_retries"]))
        out.update({"failures": failures, "passed": passed, "source": getattr(report, "source", None),
                    "commit": getattr(report, "commit", None)})
        self.store.journal("readiness-evaluated", task_id=task_id,
                           detail={"action": out["action"], "identity": out["identity"], "source": out["source"],
                                   "commit": out["commit"], "failed": [f["identity"] for f in failures],
                                   "passed": passed}, now=self.clock())
        return out

    def _completed_unproposed(self, run, status) -> bool:
        """True when settling `status` would mark a completed writer run's task `done` with its work never
        committed, pushed or proposed: the run edits code, it completed, and there is no PR lifecycle."""
        if self.lifecycle is not None or status is None or status.state == RunState.RUNNING:
            return False
        if run["role"] not in WRITER_ROLES:
            return False
        reason = status.stop_reason or (StopReason.COMPLETED if status.state == RunState.FINISHED else StopReason.TASK_FAILED)
        return reason == StopReason.COMPLETED

    def _needs_lifecycle(self, run) -> ControllerError:
        return ControllerError(
            f"{StopReason.TRANSFER_NEEDS_PR_LIFECYCLE}: run {run['run_id']} of task {run['task_id']} ({run['role']}) has "
            f"completed on provider {run['provider']}; without the PR lifecycle it would be settled as done and its work "
            f"never committed, pushed or proposed. Pass `--gh cli` to the transfer (the run is then proposed before the "
            f"move), or tick with `--adapter {run['provider']} --gh cli` first. Nothing was changed")

    def settle_and_reconcile(self, task_id: Optional[str] = None, refuse_unproposed: bool = False) -> Dict[str, Any]:
        """Poll running runs, settle finished ones, and resolve uncertain launches. Returns the
        uncertainty that remains, if any. `task_id` limits it to that task's runs (the transfer settles
        only its own task's writer). `refuse_unproposed`: a reconciled run that completed as a writer with
        no PR lifecycle raises `transfer-needs-pr-lifecycle` before that run's row is touched."""
        out: Dict[str, Any] = {"settled": [], "uncertain": []}
        mine = " AND task_id=?" if task_id is not None else ""
        args = (task_id,) if task_id is not None else ()
        for run in self.store.rows(f"SELECT * FROM runs WHERE state IN ('launching','unknown'){mine} ORDER BY reserved_at",
                                   args):
            adapter = self.adapters.get(run["provider"])
            if adapter is None:
                out["uncertain"].append({"run_id": run["run_id"], "why": f"no adapter for {run['provider']}"})
                continue
            found = adapter.reconcile(run["run_id"], run["native_run_handle"])
            if refuse_unproposed and found.found and self._completed_unproposed(run, found.status):
                raise self._needs_lifecycle(run)
            if not found.found:
                # Definitely never started: the reservation is released; the attempt stays counted.
                budget.settle(self.store, run["run_id"], state=RunState.FAILED, stop_reason=StopReason.LAUNCH_FAILED,
                              consumed=0.0, result={"reconcile": "not-found"}, now=self.clock())
                self.set_task_state(run["task_id"], TaskState.QUEUED, stop_reason=StopReason.LAUNCH_FAILED)
                out["settled"].append({"run_id": run["run_id"], "stop_reason": StopReason.LAUNCH_FAILED})
                continue
            with self.store.tx():
                self.store.conn.execute(
                    "UPDATE runs SET native_session_id=COALESCE(?, native_session_id), "
                    "native_run_handle=COALESCE(?, native_run_handle), state=?, launched_at=COALESCE(launched_at, ?) "
                    "WHERE run_id=?",
                    (found.native_session_id, found.native_run_handle, RunState.RUNNING, utcnow_iso(self.clock()),
                     run["run_id"]))
            if found.status is not None and found.status.state != RunState.RUNNING:
                run = self.store.row("SELECT * FROM runs WHERE run_id=?", (run["run_id"],))
                out["settled"].append(self._apply_outcome(run, found.status))
        for run in self.store.rows(f"SELECT * FROM runs WHERE state='running'{mine} ORDER BY reserved_at", args):
            adapter = self.adapters.get(run["provider"])
            if adapter is None or not run["native_run_handle"]:
                continue
            status = adapter.poll(run["native_run_handle"])
            if status.state == RunState.RUNNING:
                continue
            if refuse_unproposed and self._completed_unproposed(run, status):
                # The first loop may just have turned a reconciled launch into this running run.
                raise self._needs_lifecycle(run)
            out["settled"].append(self._apply_outcome(run, status))
        return out

    def cancel_run(self, run_id: str) -> Dict[str, Any]:
        """Stop a run through its provider's adapter and settle it. A run that may have a live process
        (`launching`, `running`, `unknown`) is refused when its provider's adapter is not registered: settling
        it `cancelled` would leave the worker running with nothing left to poll it. After the adapter's cancel
        the run is polled once more; a run that had already finished is settled with what it did, not as
        `cancelled` (the output then says `cancelled: false`, `finished_before_cancel: true`)."""
        run = self.store.row("SELECT * FROM runs WHERE run_id=?", (run_id,))
        if run is None:
            raise PolicyError(f"no run {run_id}")
        if run["state"] not in RunState.ACTIVE and run["state"] != RunState.UNKNOWN:
            return {"run_id": run_id, "state": run["state"], "cancelled": False}
        adapter = self.adapters.get(run["provider"])
        if adapter is None and run["state"] in (RunState.LAUNCHING, RunState.RUNNING, RunState.UNKNOWN):
            raise ControllerError(f"{StopReason.TRANSFER_WRITER_NOT_STOPPABLE}: run {run_id} is {run['state']} on provider "
                                  f"{run['provider']} and no {run['provider']} adapter is registered to stop it; pass "
                                  f"`--adapter {run['provider']}`. Nothing was changed")
        from .models import RunStatus
        if adapter is not None and run["native_run_handle"]:
            adapter.cancel(run["native_run_handle"])
            after = adapter.poll(run["native_run_handle"])
            polls = 1
            while after.state == RunState.RUNNING and polls < CANCEL_SETTLE_POLLS:
                # An adapter's cancel does nothing to a run whose turn is already over (its pid may be exiting, or
                # reused), so the run can poll RUNNING a moment longer. Wait for it: never record a cancel that
                # nothing performed, and never drop the work of a run that completed.
                self.sleep(CANCEL_SETTLE_INTERVAL_SECONDS)
                after = adapter.poll(run["native_run_handle"])
                polls += 1
            if after.state != RunState.RUNNING and after.state != RunState.CANCELLED \
                    and after.stop_reason != StopReason.CANCELLED:
                # It had already finished before the cancel reached it: settle what it actually did. A completed
                # writer needs the PR lifecycle to be proposed; without it (`run cancel` has no `--gh`) the task
                # would be `done` with its work never proposed, so the cancel is refused and nothing is settled.
                if self._completed_unproposed(run, after):
                    raise self._needs_lifecycle(run)
                settled = self._apply_outcome(run, after)
                settled.update({"cancelled": False, "finished_before_cancel": True})
                return settled
        settled = self._apply_outcome(run, RunStatus(state=RunState.CANCELLED, stop_reason=StopReason.CANCELLED,
                                                     consumed_units=None, result={"workspace": run["workspace"]}))
        settled["cancelled"] = True
        return settled

    # ----- provider transfer (PR 5) ------------------------------------------------------------------

    def transfers_used(self, pool_id: str) -> int:
        """Transfers already made for the tasks of one budget pool. Counted from the append-only journal
        (`provider-transferred` rows), over every task that shares the pool, so splitting a task never
        resets the count; the PR row's `provider_transfers_used` mirrors it while a PR is open."""
        return int(self.store.row(
            "SELECT COUNT(*) AS n FROM journal WHERE kind='provider-transferred' "
            "AND task_id IN (SELECT task_id FROM tasks WHERE pool_id=?)", (pool_id,))["n"])

    def transfer_task(self, task_id: str, to_provider: str, reason: Optional[str] = None) -> Dict[str, Any]:
        """Move a task to another provider, bounded by `limits.provider_transfers_per_pr`.

        In this order, so a refusal changes nothing:
          1. the target must be a provider the policy names, enabled, not disabled by the operator, not the
             task's current provider, and in the same unit as the task's pool (units are never converted);
             a `done` task is not transferred;
          2. the bound: the transfers already made for the pool must be below the limit, else
             `provider-transfers-exhausted` is journalled and BudgetExhausted raised;
          3. the previous writer is stopped and reconciled: an uncertain launch of THIS task is reconciled
             through its adapter first (stopped `reconciled`; still uncertain: refused); a reserved run is
             refused; a running run whose adapter is not registered is refused (nothing changed). Every active
             run is then polled ONCE before anything is settled. A writer run that has completed (polled, or
             found by the reconcile) is proposed through the PR lifecycle; without the lifecycle (no `--gh cli`)
             the transfer is refused (`transfer-needs-pr-lifecycle`, nothing changed), because the run would be
             settled `done` and its work dropped. A run that ended otherwise is settled with what it did
             (`settled`); a run still running is cancelled through ITS adapter and settled (`cancelled`). A
             settlement that leaves the task `done`, or a completed run whose proposal failed
             (`propose-failed`), ends the transfer there: `transfer-ended` is journalled and nothing moves
             (`moved: False`);
          4. one transaction: the task's provider changes, the open PR row's `provider_transfers_used` is
             incremented, `provider-transferred` is journalled with the stopped runs and how each was stopped
             (that row is the count, written before any launch on the new provider), and the task goes back
             to `pr-open` when its PR is still open, else to `queued` (`moved: True`).
        The task row, its PR row (number, head, base), its budget pool and its attempts are untouched. The
        next launch is the new provider's: a fresh session (a session id recorded by another provider is
        never resumed) briefed by the compact handoff."""
        now = self.clock()
        task = self.store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))
        if task is None:
            raise PolicyError(f"no task {task_id}")
        from_provider = task["provider"]
        if to_provider == from_provider:
            raise PolicyError(f"task {task_id} is already on provider {to_provider}")
        if task["state"] == TaskState.DONE:
            raise PolicyError(f"task {task_id} is done; a done task is not transferred")
        spec = self.policy.provider(to_provider)
        if not spec.get("enabled"):
            raise PolicyError(f"{StopReason.PROVIDER_DISABLED}: provider {to_provider} is disabled by policy {self.policy.version}")
        disabled = json.loads(self.store.control("disabled_providers") or "[]")
        if to_provider in disabled:
            raise PolicyError(f"{StopReason.PROVIDER_DISABLED}: provider {to_provider} is disabled by the operator "
                              f"(`provider enable {to_provider}`)")
        pool = budget.pool_row(self.store, task["pool_id"])
        if spec["unit"] != pool["unit"]:
            raise PolicyError(f"provider {to_provider} counts {spec['unit']}; task {task_id}'s pool {pool['pool_id']} "
                              f"counts {pool['unit']}, and provider units are never converted")
        self.store.acquire_lease(self.holder_id, int(self.policy.idle["lease_ttl_seconds"]), now=now,
                                 host=self.host, pid=self.pid)
        limit = int(self.policy.limits["provider_transfers_per_pr"])
        used = self.transfers_used(task["pool_id"])
        if used >= limit:
            self.store.journal("transfer-refused", task_id=task_id,
                               detail={"reason": StopReason.PROVIDER_TRANSFERS_EXHAUSTED, "from": from_provider,
                                       "to": to_provider, "used": used, "limit": limit}, now=now)
            raise BudgetExhausted(f"{StopReason.PROVIDER_TRANSFERS_EXHAUSTED}: task {task_id} (pool {task['pool_id']}) "
                                  f"has made {used} of {limit} provider transfers")

        # 3. Stop and reconcile the previous writer before anything moves.
        stopped: List[Dict[str, Any]] = []
        active = self.store.rows("SELECT * FROM runs WHERE task_id=? AND state IN ('reserved','launching','running','unknown') "
                                 "ORDER BY reserved_at, rowid", (task_id,))
        if any(r["state"] in RunState.UNCERTAIN for r in active):
            # Only this task's runs: the transfer settles nothing of another task's. A run the reconcile settles
            # is reported (and journalled) as stopped `reconciled`.
            for s in self.settle_and_reconcile(task_id=task_id, refuse_unproposed=True)["settled"]:
                r = self.store.row("SELECT provider, role, consumed FROM runs WHERE run_id=?", (s["run_id"],))
                stopped.append({"run_id": s["run_id"], "provider": r["provider"], "role": r["role"], "how": "reconciled",
                                "charged": s.get("charged", r["consumed"]), "stop_reason": s.get("stop_reason")})
            active = self.store.rows("SELECT * FROM runs WHERE task_id=? AND state IN ('reserved','launching','running','unknown') "
                                     "ORDER BY reserved_at, rowid", (task_id,))
        for run in active:
            if run["state"] in RunState.UNCERTAIN or run["state"] == RunState.RESERVED:
                raise ControllerError(f"{StopReason.RECONCILE_PENDING}: run {run['run_id']} of task {task_id} is "
                                      f"{run['state']} on provider {run['provider']}; reconcile it (tick with that "
                                      f"provider's adapter, or `run settle`) before a transfer")
            if self.adapters.get(run["provider"]) is None:
                raise ControllerError(f"{StopReason.TRANSFER_WRITER_NOT_STOPPABLE}: run {run['run_id']} of task {task_id} is "
                                      f"running on provider {run['provider']} and no {run['provider']} adapter is "
                                      f"registered to stop it; pass `--adapter {run['provider']}`")
        # Every active run is polled ONCE, before anything is settled, so that the refusal below changes nothing.
        polled = [(run, self.adapters[run["provider"]].poll(run["native_run_handle"]) if run["native_run_handle"] else None)
                  for run in active]
        for run, status in polled:
            if self._completed_unproposed(run, status):
                # Settled without the lifecycle it would be `done`, its work never committed, pushed or proposed,
                # and the move below would then overwrite `done` with `queued` on the new provider.
                raise self._needs_lifecycle(run)
        for run, status in polled:
            # A run that already ended is settled with what it actually did (a completed one is proposed);
            # only a run still running is cancelled. Either way it is settled before the move.
            if status is not None and status.state != RunState.RUNNING:
                out = self._apply_outcome(run, status)
                how = "settled"
            else:
                out = self.cancel_run(run["run_id"])
                how = "cancelled" if out.get("cancelled") else "settled"
            stopped.append({"run_id": run["run_id"], "provider": run["provider"], "role": run["role"], "how": how,
                            "charged": out.get("charged"), "stop_reason": out.get("stop_reason")})

        # A settlement above can finish the task (a run that completed between the poll and the cancel, with no
        # lifecycle), or leave a completed run unproposed (`propose-failed`). A done task is not transferred
        # (rule 1), and moving an unproposed one would drop its work: the transfer ends here and moves nothing.
        after = self.store.row("SELECT state, stop_reason FROM tasks WHERE task_id=?", (task_id,))
        ended = None
        if after["state"] == TaskState.DONE:
            ended = "task-done-by-settlement"
        elif after["state"] == TaskState.NEEDS_RECONCILE and after["stop_reason"] == StopReason.PROPOSE_FAILED:
            ended = StopReason.PROPOSE_FAILED
        if ended is not None:
            pr = self.store.row("SELECT pr_number FROM prs WHERE task_id=? AND state='open' ORDER BY rowid DESC LIMIT 1",
                                (task_id,))
            self.store.journal("transfer-ended", task_id=task_id,
                               detail={"reason": ended, "from": from_provider, "to": to_provider, "state": after["state"],
                                       "stopped_runs": [s["run_id"] for s in stopped], "stopped": stopped}, now=now)
            return {"task_id": task_id, "from": from_provider, "to": to_provider, "moved": False, "ended": ended,
                    "transfer_no": None, "of": limit, "state": after["state"],
                    "pr_number": pr["pr_number"] if pr is not None else None, "stopped": stopped,
                    "pool_id": task["pool_id"]}

        # 4. The move itself, in one transaction.
        pr = self.store.row("SELECT * FROM prs WHERE task_id=? AND state='open' ORDER BY rowid DESC LIMIT 1", (task_id,))
        state = TaskState.PR_OPEN if pr is not None else TaskState.QUEUED
        transfer_no = used + 1
        blocker = (f"transferred from {from_provider} to {to_provider} ({transfer_no} of {limit})"
                   + (f": {reason}" if reason else ""))
        at = utcnow_iso(now)
        with self.store.tx():
            self.store.conn.execute(
                "UPDATE tasks SET provider=?, state=?, stop_reason=?, blocker=?, next_eligible_at=NULL, updated_at=? "
                "WHERE task_id=?", (to_provider, state, StopReason.PROVIDER_TRANSFERRED, blocker, at, task_id))
            if pr is not None:
                self.store.conn.execute(
                    "UPDATE prs SET provider_transfers_used = provider_transfers_used + 1, updated_at=? WHERE pr_number=?",
                    (at, pr["pr_number"]))
            self.store.journal("provider-transferred", task_id=task_id,
                               detail={"from": from_provider, "to": to_provider, "transfer_no": transfer_no, "of": limit,
                                       "pool_id": task["pool_id"], "pr_number": pr["pr_number"] if pr is not None else None,
                                       "stopped_runs": [s["run_id"] for s in stopped], "stopped": stopped,
                                       "state": state, "reason": reason}, now=now)
        return {"task_id": task_id, "from": from_provider, "to": to_provider, "moved": True, "transfer_no": transfer_no,
                "of": limit, "state": state, "pr_number": pr["pr_number"] if pr is not None else None, "stopped": stopped,
                "pool_id": task["pool_id"]}

    # ----- dispatch --------------------------------------------------------------------------------

    def _eligible_task(self):
        now = utcnow_iso(self.clock())
        for row in self.store.rows(
                "SELECT * FROM tasks WHERE state IN ('queued','waiting-quota','waiting-rate-limit','waiting-allowance') "
                # A task with an open PR is the PR phase's business (observe, repair), never a fresh implementation.
                "AND NOT EXISTS (SELECT 1 FROM prs WHERE prs.task_id = tasks.task_id AND prs.state = 'open') "
                "ORDER BY priority, created_at, task_id"):
            if row["next_eligible_at"] and row["next_eligible_at"] > now:
                continue
            spec = self.policy.providers.get(row["provider"])
            if not spec or not spec.get("enabled"):
                continue
            disabled = self.store.control("disabled_providers")
            if disabled and row["provider"] in json.loads(disabled):
                continue
            return row
        return None

    def tick(self, dry_run: bool = False) -> TickReport:
        now = self.clock()
        paused = self.paused()
        if paused:
            return TickReport("paused", StopReason.PAUSED, details=paused)
        if dry_run:
            return self._plan()
        try:
            self.store.acquire_lease(self.holder_id, int(self.policy.idle["lease_ttl_seconds"]), now=now,
                                     host=self.host, pid=self.pid)
        except LeaseError as e:
            self.store.journal("tick-refused", detail={"reason": StopReason.LEASE_NOT_HELD, "error": str(e)}, now=now)
            return TickReport("refused", StopReason.LEASE_NOT_HELD, details={"error": str(e)})

        recon = self.settle_and_reconcile()
        report = TickReport("idle", StopReason.EMPTY_QUEUE, settled=recon["settled"])
        if recon["uncertain"]:
            report.action, report.reason = "blocked", StopReason.RECONCILE_PENDING
            report.details = {"uncertain": recon["uncertain"]}
            self.store.journal("tick-blocked", detail=report.details, now=now)
            return report
        if recon["settled"]:
            # One action per tick: a tick that settled or reconciled something stops there, so the
            # record of what happened to the previous run is written before anything new starts.
            report.action, report.reason = "settled", "settled"
            return report

        active = self.store.rows("SELECT run_id, task_id, role FROM runs WHERE role IN ('implementation','readiness-fixer') "
                                 "AND state IN ('reserved','launching','running')")
        if len(active) >= int(self.policy.limits["active_writers"]):
            # One writer across providers AND roles: a repair run edits code like an implementation run.
            report.action, report.reason = "blocked", StopReason.WRITER_BUSY
            report.details = {"active": [dict(r) for r in active]}
            return report

        if self.lifecycle is not None:
            # The PR phase: observe every open PR by its current head; merge, repair or park as it demands.
            ph = self.lifecycle.phase()
            acted = ph["report"]
            if acted is not None:
                action = acted.get("action")
                if action == "launched":
                    return TickReport("launched", "repair", task_id=acted.get("task_id"), run_id=acted.get("run_id"),
                                      settled=recon["settled"], details=acted)
                if action == "blocked":
                    return TickReport("blocked", str(acted.get("reason")), task_id=acted.get("task_id"),
                                      run_id=acted.get("run_id"), settled=recon["settled"], details=acted)
                return TickReport("pr", action, task_id=acted.get("task_id"), settled=recon["settled"], details=acted)
            if ph["open"] > 0:
                # PRs are serial: nothing new launches while one is open, so the next task starts from the
                # merge SHA this one produces. An integrator decision (no owner row), matching the repository's
                # serial-merge rule.
                self.store.journal("tick-blocked", detail={"reason": StopReason.PR_OPEN_BLOCKS_DISPATCH,
                                                           "open_prs": ph["open"]}, now=now)
                return TickReport("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH, settled=recon["settled"],
                                  details={"open_prs": ph["open"]})

        stranded = self._propose_failed_tasks()
        if stranded:
            # A completed run whose commit, push or PR failed may have left a branch or even a PR on GitHub that
            # the ledger does not know: launching the next task would start a second line of work beside it, out
            # of order. Nothing launches until the operator has looked and moved the task (an integrator
            # decision, the same caution as PRs being serial).
            self.store.journal("tick-blocked", detail={"reason": StopReason.PROPOSE_FAILED_BLOCKS_DISPATCH,
                                                       "tasks": stranded}, now=now)
            return TickReport("blocked", StopReason.PROPOSE_FAILED_BLOCKS_DISPATCH, settled=recon["settled"],
                              details={"tasks": stranded})

        task = self._eligible_task()
        if task is None:
            # Idle: no adapter is touched, no model is called.
            self.store.journal("tick-idle", detail={"reason": StopReason.EMPTY_QUEUE}, now=now)
            return report

        adapter = self.adapters.get(task["provider"])
        if adapter is None:
            report.action, report.reason, report.task_id = "blocked", StopReason.PROVIDER_DISABLED, task["task_id"]
            report.details = {"error": f"no adapter registered for provider {task['provider']}"}
            return report

        pool = budget.pool_row(self.store, task["pool_id"])
        permitted, why = budget.allowance_decision(self.policy, budget.latest_allowance(self.store, task["provider"]),
                                                   float(pool["invocation_ceiling"]))
        if not permitted:
            self.set_task_state(task["task_id"], TaskState.WAITING_ALLOWANCE, stop_reason=why,
                                next_eligible_in=self.policy.idle["quota_wait_seconds"])
            report.action, report.reason, report.task_id = "blocked", why, task["task_id"]
            return report

        # Exact resume: when the task's latest run stopped at the turn ceiling and left a session id,
        # the next launch continues that session instead of starting a fresh one.
        resume_session_id: Optional[str] = None
        # Latest by insertion order (rowid): two runs reserved in the same second must not be ordered
        # by their random run ids.
        last = self.store.row("SELECT * FROM runs WHERE task_id=? ORDER BY reserved_at DESC, rowid DESC LIMIT 1",
                              (task["task_id"],))
        resume_run = None
        # A session belongs to the provider that recorded it: after a provider transfer the new provider
        # starts a fresh session (and a fresh clone), never another CLI's session id.
        if (last is not None and last["stop_reason"] == StopReason.INVOCATION_CEILING and last["native_session_id"]
                and last["provider"] == task["provider"]):
            resume_session_id = last["native_session_id"]
            resume_run = last

        run_id = f"run-{uuid.uuid4().hex[:12]}"
        try:
            # The adapter chooses the workspace (`tasks` has no workspace column; the run row records
            # what the adapter returns).
            reservation = budget.reserve(self.store, run_id=run_id, task_id=task["task_id"], pool_id=task["pool_id"],
                                         provider=task["provider"], policy=self.policy,
                                         policy_version_id=self.policy_version_id, holder=self.holder_id, now=now,
                                         workspace=None)
        except BudgetExhausted as e:
            reason = str(e).split(":", 1)[0]
            self.set_task_state(task["task_id"], TaskState.PARKED_BUDGET, stop_reason=reason, blocker=str(e))
            report.action, report.reason, report.task_id = "blocked", reason, task["task_id"]
            report.details = {"error": str(e)}
            return report
        except PolicyError as e:
            reason = str(e).split(":", 1)[0]
            self.set_task_state(task["task_id"], TaskState.PARKED_BUDGET, stop_reason=reason, blocker=str(e))
            report.action, report.reason, report.task_id = "blocked", reason, task["task_id"]
            report.details = {"error": str(e)}
            return report

        prepared: Dict[str, Any] = {}
        if self.lifecycle is not None:
            # PR 3: the workspace is a clone of the current base on the task's branch, prepared by the
            # controller; the prompt carries the compact handoff and never a transcript. A resumed session
            # keeps the workspace its capped run was editing (`lifecycle.prepare_implementation`).
            try:
                prepared = self.lifecycle.prepare_implementation(task, run_id, resume_run=resume_run)
            except Exception as e:  # noqa: BLE001 - a git failure is a launch failure; nothing was spent
                budget.settle(self.store, run_id, state=RunState.FAILED, stop_reason=StopReason.LAUNCH_FAILED,
                              consumed=0.0, result={"error": str(e), "stage": "prepare-workspace"}, now=now)
                self.set_task_state(task["task_id"], TaskState.QUEUED, stop_reason=StopReason.LAUNCH_FAILED,
                                    blocker=str(e)[:500])
                return TickReport("blocked", StopReason.LAUNCH_FAILED, task_id=task["task_id"], run_id=run_id,
                                  settled=recon["settled"], details={"error": str(e), "stage": "prepare-workspace"})
            with self.store.tx():
                self.store.conn.execute("UPDATE runs SET workspace=? WHERE run_id=?", (prepared["workspace"], run_id))
        request = LaunchRequest(run_id=run_id, task_id=task["task_id"], provider=task["provider"],
                                model=reservation["model"], effort=reservation["effort"], speed=reservation["speed"],
                                invocation_ceiling=int(reservation["reservation"]), workspace=prepared.get("workspace"),
                                prompt=prepared.get("prompt") or (
                                    f"card #{task['card_number']}: {task['title']}" if task["card_number"] else task["title"]),
                                resume_session_id=resume_session_id, base_sha=prepared.get("base_sha"),
                                branch=prepared.get("branch"), handoff=prepared.get("handoff"))
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state=?, launched_at=? WHERE run_id=?",
                                    (RunState.LAUNCHING, utcnow_iso(now), run_id))
            if resume_session_id:
                self.store.journal("resume", task_id=task["task_id"], run_id=run_id,
                                   detail={"session": resume_session_id}, now=now)
        try:
            result = adapter.launch(request)
        except LaunchOutcomeUnknown as e:
            # Leave the run in `launching` with no handle. The next tick reconciles before launching anything.
            with self.store.tx():
                self.store.conn.execute("UPDATE runs SET state=? WHERE run_id=?", (RunState.UNKNOWN, run_id))
                self.store.journal("launch-unknown", task_id=task["task_id"], run_id=run_id, detail={"error": str(e)}, now=now)
            self.set_task_state(task["task_id"], TaskState.NEEDS_RECONCILE, stop_reason=StopReason.LAUNCH_UNKNOWN)
            return TickReport("blocked", StopReason.LAUNCH_UNKNOWN, task_id=task["task_id"], run_id=run_id,
                              settled=recon["settled"], details={"error": str(e)})
        except LaunchRefused as e:
            # Nothing started, for a reason the adapter could name. The reservation is released, the
            # attempt stays counted, and the task goes where the reason sends it.
            budget.settle(self.store, run_id, state=RunState.FAILED, stop_reason=e.stop_reason, consumed=0.0,
                          result={"blocker": e.blocker, "refused": True}, now=now)
            state, stop_reason = self._refusal_target(e.stop_reason)
            self.set_task_state(task["task_id"], state, stop_reason=stop_reason, blocker=e.blocker)
            self.store.journal("launch-refused", task_id=task["task_id"], run_id=run_id,
                               detail={"stop_reason": e.stop_reason, "blocker": e.blocker, "task_state": state}, now=now)
            return TickReport("blocked", e.stop_reason, task_id=task["task_id"], run_id=run_id,
                              settled=recon["settled"], details={"blocker": e.blocker, "task_state": state})
        except Exception as e:
            budget.settle(self.store, run_id, state=RunState.FAILED, stop_reason=StopReason.LAUNCH_FAILED, consumed=0.0,
                          result={"error": str(e)}, now=now)
            self.set_task_state(task["task_id"], TaskState.QUEUED, stop_reason=StopReason.LAUNCH_FAILED, blocker=str(e))
            return TickReport("blocked", StopReason.LAUNCH_FAILED, task_id=task["task_id"], run_id=run_id,
                              settled=recon["settled"], details={"error": str(e)})
        with self.store.tx():
            self.store.conn.execute(
                "UPDATE runs SET state=?, native_session_id=?, native_run_handle=?, workspace=? WHERE run_id=?",
                (RunState.RUNNING, result.native_session_id, result.native_run_handle,
                 result.workspace or prepared.get("workspace"), run_id))
            self.store.conn.execute("UPDATE tasks SET state=?, updated_at=? WHERE task_id=?",
                                    (TaskState.RUNNING, utcnow_iso(now), task["task_id"]))
            self.store.journal("launched", task_id=task["task_id"], run_id=run_id,
                               detail={"handle": result.native_run_handle, "session": result.native_session_id}, now=now)
        return TickReport("launched", "launched", task_id=task["task_id"], run_id=run_id, settled=recon["settled"],
                          details={"reservation": reservation["reservation"], "attempt_no": reservation["attempt_no"],
                                   "model": reservation["model"], "effort": reservation["effort"]})

    @staticmethod
    def _refusal_target(stop_reason: str):
        """Where a task goes after an adapter refused to launch it: (task state, task stop reason)."""
        if stop_reason in (StopReason.AUTH_MISSING, StopReason.FUNDING_ROUTE_MISMATCH):
            return TaskState.BLOCKED_AUTH, stop_reason
        if stop_reason == StopReason.MODEL_UNRECOGNIZED:
            return TaskState.PARKED_DECISION, stop_reason
        if stop_reason == StopReason.OPERATOR_CONFIG_REFUSED:
            # One attempt is spent; the task then waits for the operator's fix instead of being re-queued and
            # refused on every tick until its attempts are gone.
            return TaskState.BLOCKED_PERMISSION, stop_reason
        return TaskState.QUEUED, StopReason.LAUNCH_FAILED

    def _propose_failed_tasks(self) -> List[str]:
        """Tasks whose completed run was charged but not proposed (`needs-reconcile`, `propose-failed`)."""
        return [r["task_id"] for r in self.store.rows(
            "SELECT task_id FROM tasks WHERE state=? AND stop_reason=? ORDER BY task_id",
            (TaskState.NEEDS_RECONCILE, StopReason.PROPOSE_FAILED))]

    def _plan(self) -> TickReport:
        """Dry run: say what a tick would do, by the tick's own dispatch rules in the tick's order. Reads only:
        no adapter and no GitHub call is made, so a run the ledger still records as running is reported as
        the writer even if its process has exited (a real tick polls it first and may settle it), and an open
        PR is reported as blocking dispatch (a real tick observes it first and may merge, repair or park)."""
        details: Dict[str, Any] = {"dry_run": True}
        uncertain = [dict(r) for r in self.store.rows("SELECT run_id, task_id, provider, state FROM runs WHERE state IN ('launching','unknown')")]
        if uncertain:
            return TickReport("would-block", StopReason.RECONCILE_PENDING, details={**details, "uncertain": uncertain})
        # The same writers as the tick: a repair run edits code like an implementation run.
        active = [dict(r) for r in self.store.rows("SELECT run_id, task_id, role FROM runs WHERE role IN ('implementation','readiness-fixer') "
                                                   "AND state IN ('reserved','launching','running')")]
        if len(active) >= int(self.policy.limits["active_writers"]):
            return TickReport("would-block", StopReason.WRITER_BUSY, details={**details, "active": active})
        if self.lifecycle is not None:
            # The tick's PR phase: PRs are serial, so nothing new launches while one is open.
            open_prs = int(self.store.row("SELECT COUNT(*) AS n FROM prs WHERE state='open'")["n"])
            if open_prs > 0:
                return TickReport("would-block", StopReason.PR_OPEN_BLOCKS_DISPATCH,
                                  details={**details, "open_prs": open_prs})
        stranded = self._propose_failed_tasks()
        if stranded:
            return TickReport("would-block", StopReason.PROPOSE_FAILED_BLOCKS_DISPATCH,
                              details={**details, "tasks": stranded})
        task = self._eligible_task()
        if task is None:
            return TickReport("would-idle", StopReason.EMPTY_QUEUE, details=details)
        if self.adapters.get(task["provider"]) is None:
            # The tick's own report when the picked task's provider has no registered adapter.
            return TickReport("would-block", StopReason.PROVIDER_DISABLED, task_id=task["task_id"],
                              details={**details, "error": f"no adapter registered for provider {task['provider']}"})
        pool = budget.pool_row(self.store, task["pool_id"])
        permitted, why = budget.allowance_decision(self.policy, budget.latest_allowance(self.store, task["provider"]),
                                                   float(pool["invocation_ceiling"]))
        if not permitted:
            return TickReport("would-block", why, task_id=task["task_id"], details=details)
        invocation = min(float(pool["invocation_ceiling"]), float(self.policy.limits["invocation_turn_ceiling"]))
        if invocation > budget.remaining(pool):
            return TickReport("would-block", StopReason.TASK_BUDGET_EXHAUSTED, task_id=task["task_id"],
                              details={**details, "remaining": budget.remaining(pool), "invocation": invocation})
        if int(pool["attempts_used"]) + 1 > int(pool["attempts_max"]):
            return TickReport("would-block", StopReason.ATTEMPTS_EXHAUSTED, task_id=task["task_id"], details=details)
        spec = self.policy.provider(task["provider"])
        return TickReport("would-launch", StopReason.DRY_RUN, task_id=task["task_id"],
                          details={**details, "provider": task["provider"], "model": spec["model"], "effort": spec["effort"],
                                   "reservation": invocation, "attempt_no": int(pool["attempts_used"]) + 1,
                                   "remaining_after": budget.remaining(pool) - invocation})

    # ----- status ----------------------------------------------------------------------------------

    def run_evidence(self, run) -> Dict[str, Any]:
        """Where the evidence of one run is on this host, read from the ledger row and the run directory
        alone (no adapter is asked): the run directory, its manifest, stdout and stderr when present, the
        transcript when the manifest lets it be located, the recorded workspace, and the evidence archive
        when one has been written. A path is None when the file is not there."""
        run_id = run["run_id"]
        run_dir = os.path.join(self.runs_dir, run_id)
        def present(name: str) -> Optional[str]:
            path = os.path.join(run_dir, name)
            return path if os.path.isfile(path) else None
        manifest_path = present("manifest.json")
        transcript: Optional[str] = None
        if manifest_path:
            try:
                with open(manifest_path, encoding="utf-8") as f:
                    manifest = json.load(f)
                from .adapters import locate_run_transcript   # a pure path helper; no adapter call
                transcript = locate_run_transcript(manifest, run["native_session_id"])
            except (OSError, ValueError, ImportError):
                transcript = None
        archive = os.path.join(self.evidence_dir, f"{run_id}.tar.gz")
        workspace = run["workspace"]
        return {"run_dir": run_dir if os.path.isdir(run_dir) else None, "manifest": manifest_path,
                "stdout": present("stdout.json"), "stderr": present("stderr.log"), "transcript": transcript,
                "workspace": workspace, "workspace_present": bool(workspace and os.path.isdir(workspace)),
                "archive": archive if os.path.isfile(archive) else None}

    def status(self) -> Dict[str, Any]:
        tasks = []
        for t in self.store.rows("SELECT * FROM tasks ORDER BY created_at, task_id"):
            pool = self.store.row("SELECT * FROM budget_pools WHERE pool_id=?", (t["pool_id"],))
            # Latest by insertion order (rowid), as the dispatcher does: two runs reserved in the same second
            # must not be ordered by their random run ids.
            run = self.store.row("SELECT * FROM runs WHERE task_id=? ORDER BY reserved_at DESC, rowid DESC LIMIT 1", (t["task_id"],))
            pr = self.store.row("SELECT * FROM prs WHERE task_id=? ORDER BY updated_at DESC, rowid DESC LIMIT 1", (t["task_id"],))
            tasks.append({
                "task_id": t["task_id"], "card": t["card_number"], "workstream": t["workstream"], "owner": t["owner_agent"],
                "provider": t["provider"], "state": t["state"], "stop_reason": t["stop_reason"], "blocker": t["blocker"],
                "next_eligible_at": t["next_eligible_at"], "pr": pr["pr_number"] if pr else None,
                "pr_record": {"number": pr["pr_number"], "state": pr["state"], "outcome": pr["outcome"], "branch": pr["branch"],
                              "base_sha": pr["base_sha"], "head_sha": pr["head_sha"], "merge_sha": pr["merge_sha"],
                              "repair_rounds_used": pr["repair_rounds_used"], "url": pr["url"]} if pr else None,
                "base_sha": t["base_sha"], "branch": t["branch"],
                "funding_pool": run["funding_pool"] if run else self.policy.providers.get(t["provider"], {}).get("funding_pool"),
                "pool": {"id": pool["pool_id"], "unit": pool["unit"], "ceiling": pool["task_ceiling"],
                         "consumed": pool["consumed"], "reserved": pool["reserved"], "overshoot": pool["overshoot"],
                         "remaining": budget.remaining(pool), "attempts_used": pool["attempts_used"],
                         "attempts_max": pool["attempts_max"],
                         # The attempts `budget.reserve` still allows: it caps the pool's max by the policy's.
                         "attempts_remaining": max(0, min(int(pool["attempts_max"]),
                                                          int(self.policy.limits["max_attempts_per_task"]))
                                                   - int(pool["attempts_used"])),
                         # What one reservation needs (`budget.reserve`), so a reader can tell the attempts the
                         # budget still permits from the attempts left (`metrics.permitted_attempts`).
                         "invocation_ceiling": min(float(pool["invocation_ceiling"]),
                                                   float(self.policy.limits["invocation_turn_ceiling"]))},
                "last_run": {"run_id": run["run_id"], "state": run["state"], "stop_reason": run["stop_reason"],
                             "model": run["model"], "effort": run["effort"], "session": run["native_session_id"],
                             "handle": run["native_run_handle"], "workspace": run["workspace"], "role": run["role"],
                             "pr_number": run["pr_number"]} if run else None,
                "evidence": self.run_evidence(run) if run else None,
                "transfers": {"used": self.transfers_used(t["pool_id"]),
                              "max": int(self.policy.limits["provider_transfers_per_pr"])},
                "last_event": t["last_event"], "last_event_at": t["last_event_at"],
            })
        lease = self.store.lease_row()
        usage = [dict(r) for r in self.store.rows("SELECT provider, kind, unit, freshness, value, total, observed_at FROM "
                                                   "usage_observations WHERE id IN (SELECT MAX(id) FROM usage_observations "
                                                   "GROUP BY provider, kind) ORDER BY provider, kind")]
        return {
            "controller_id": self.store.meta("controller_id"), "state_path": self.store.path,
            "policy": {"version": self.policy.version, "sha256": self.policy.sha256, "source": self.policy.source_path,
                       "paid_inference_enabled": self.policy.funding["paid_inference_enabled"]},
            "paused": self.paused(), "disabled_providers": json.loads(self.store.control("disabled_providers") or "[]"),
            "lease": dict(lease) if lease else None, "tasks": tasks, "usage": usage,
            "outbox_pending": int(self.store.row("SELECT COUNT(*) AS n FROM outbox WHERE state IN ('pending','sending')")["n"]),
            "runs_dir": self.runs_dir, "evidence_dir": self.evidence_dir,
        }
