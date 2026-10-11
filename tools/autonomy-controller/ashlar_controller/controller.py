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
"""
from __future__ import annotations

import json
import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

from . import budget, events
from .adapters.base import ProviderAdapter
from .models import (BudgetExhausted, LaunchOutcomeUnknown, LaunchRequest, LeaseError, PolicyError, RunState,
                     StopReason, TaskState, UsageFreshness)
from .policy import Policy
from .store import Store, utcnow_iso


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
                 host: Optional[str] = None, pid: Optional[int] = None, clock=None):
        self.store = store
        self.policy = policy
        self.adapters = adapters
        self.holder_id = holder_id
        self.host = host
        self.pid = pid
        self.clock = clock or time.time
        self.policy_version_id: Optional[int] = None
        if not store.read_only:
            self.policy_version_id = store.record_policy(policy.version, policy.sha256, policy.source_path,
                                                         policy.to_json(), now=self.clock())

    # ----- tasks ---------------------------------------------------------------------------------

    def ensure_task(self, task_id: str, *, workstream: str, provider: str, card_number: Optional[int] = None,
                    title: str = "", handoff_ref: Optional[str] = None, decision_refs: Optional[List[str]] = None,
                    parent_task_id: Optional[str] = None, owner_agent: str = "claude", priority: int = 100,
                    base_sha: Optional[str] = None) -> bool:
        """Create the task and its pool if absent. A child task shares its parent's pool. Returns True
        when created."""
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
                "INSERT INTO tasks(task_id, card_number, workstream, title, handoff_ref, decision_refs, owner_agent, "
                "provider, parent_task_id, pool_id, state, priority, base_sha, created_at, updated_at) "
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (task_id, card_number, workstream, title, handoff_ref, json.dumps(decision_refs or []), owner_agent,
                 provider, parent_task_id, pool_id, TaskState.QUEUED, priority, base_sha, at, at))
            self.store.journal("task-created", task_id=task_id,
                               detail={"pool_id": pool_id, "parent": parent_task_id, "provider": provider}, now=now)
        return True

    def ingest_event(self, event_id: str, source: str, kind: str, task_id: Optional[str],
                     payload: Optional[Dict[str, Any]] = None) -> bool:
        """Record an event once. Returns False for a duplicate, which the caller must then ignore."""
        return events.ingest(self.store, event_id, source, kind, task_id, payload, now=self.clock())

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
        reason = status.stop_reason or (StopReason.COMPLETED if status.state == RunState.FINISHED else StopReason.TASK_FAILED)
        run_state = status.state if status.state in (RunState.FINISHED, RunState.FAILED, RunState.CANCELLED) else RunState.FAILED
        settled = budget.settle(self.store, run["run_id"], state=run_state, stop_reason=reason, consumed=consumed,
                                failure_identity=status.failure_identity, result=status.result, now=self.clock())
        idle = self.policy.idle
        blocker = status.result.get("blocker") if status.result else None
        if reason == StopReason.COMPLETED:
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
        else:  # task-failed and anything unrecognised
            identity = status.failure_identity or "unidentified-failure"
            allowed, used = budget.failure_retry_allowed(self.store, run["pool_id"], identity,
                                                         int(limits["same_failure_retries"]))
            if allowed:
                budget.bump_failure_retry(self.store, run["pool_id"], identity)
                self.set_task_state(task_id, TaskState.QUEUED, stop_reason=reason,
                                    blocker=f"retrying diagnosed failure {identity} ({used + 1} of {limits['same_failure_retries']})")
            else:
                self.set_task_state(task_id, TaskState.PARKED_FAILURE, stop_reason=StopReason.SAME_FAILURE_RETRY_EXHAUSTED,
                                    blocker=f"failure {identity} already retried {used} time(s); the same diagnosed "
                                            f"failure is not retried again on any cycle or provider")
        settled["task_id"] = task_id
        return settled

    def settle_and_reconcile(self) -> Dict[str, Any]:
        """Poll running runs, settle finished ones, and resolve uncertain launches. Returns the
        uncertainty that remains, if any."""
        out: Dict[str, Any] = {"settled": [], "uncertain": []}
        for run in self.store.rows("SELECT * FROM runs WHERE state IN ('launching','unknown') ORDER BY reserved_at"):
            adapter = self.adapters.get(run["provider"])
            if adapter is None:
                out["uncertain"].append({"run_id": run["run_id"], "why": f"no adapter for {run['provider']}"})
                continue
            found = adapter.reconcile(run["run_id"], run["native_run_handle"])
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
        for run in self.store.rows("SELECT * FROM runs WHERE state='running' ORDER BY reserved_at"):
            adapter = self.adapters.get(run["provider"])
            if adapter is None or not run["native_run_handle"]:
                continue
            status = adapter.poll(run["native_run_handle"])
            if status.state == RunState.RUNNING:
                continue
            out["settled"].append(self._apply_outcome(run, status))
        return out

    def cancel_run(self, run_id: str) -> Dict[str, Any]:
        run = self.store.row("SELECT * FROM runs WHERE run_id=?", (run_id,))
        if run is None:
            raise PolicyError(f"no run {run_id}")
        if run["state"] not in RunState.ACTIVE and run["state"] != RunState.UNKNOWN:
            return {"run_id": run_id, "state": run["state"], "cancelled": False}
        adapter = self.adapters.get(run["provider"])
        if adapter is not None and run["native_run_handle"]:
            adapter.cancel(run["native_run_handle"])
        from .models import RunStatus
        settled = self._apply_outcome(run, RunStatus(state=RunState.CANCELLED, stop_reason=StopReason.CANCELLED,
                                                     consumed_units=None, result={"workspace": run["workspace"]}))
        settled["cancelled"] = True
        return settled

    # ----- dispatch --------------------------------------------------------------------------------

    def _eligible_task(self):
        now = utcnow_iso(self.clock())
        for row in self.store.rows(
                "SELECT * FROM tasks WHERE state IN ('queued','waiting-quota','waiting-rate-limit','waiting-allowance') "
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

        active = self.store.rows("SELECT run_id, task_id FROM runs WHERE role='implementation' AND state IN "
                                 "('reserved','launching','running')")
        if len(active) >= int(self.policy.limits["active_writers"]):
            report.action, report.reason = "blocked", StopReason.WRITER_BUSY
            report.details = {"active": [dict(r) for r in active]}
            return report

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

        run_id = f"run-{uuid.uuid4().hex[:12]}"
        try:
            reservation = budget.reserve(self.store, run_id=run_id, task_id=task["task_id"], pool_id=task["pool_id"],
                                         provider=task["provider"], policy=self.policy,
                                         policy_version_id=self.policy_version_id, holder=self.holder_id, now=now,
                                         workspace=task["base_sha"] and None)
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

        request = LaunchRequest(run_id=run_id, task_id=task["task_id"], provider=task["provider"],
                                model=reservation["model"], effort=reservation["effort"], speed=reservation["speed"],
                                invocation_ceiling=int(reservation["reservation"]), workspace=None,
                                prompt=f"card #{task['card_number']}: {task['title']}" if task["card_number"] else task["title"])
        with self.store.tx():
            self.store.conn.execute("UPDATE runs SET state=?, launched_at=? WHERE run_id=?",
                                    (RunState.LAUNCHING, utcnow_iso(now), run_id))
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
        except Exception as e:
            budget.settle(self.store, run_id, state=RunState.FAILED, stop_reason=StopReason.LAUNCH_FAILED, consumed=0.0,
                          result={"error": str(e)}, now=now)
            self.set_task_state(task["task_id"], TaskState.QUEUED, stop_reason=StopReason.LAUNCH_FAILED, blocker=str(e))
            return TickReport("blocked", StopReason.LAUNCH_FAILED, task_id=task["task_id"], run_id=run_id,
                              settled=recon["settled"], details={"error": str(e)})
        with self.store.tx():
            self.store.conn.execute(
                "UPDATE runs SET state=?, native_session_id=?, native_run_handle=?, workspace=? WHERE run_id=?",
                (RunState.RUNNING, result.native_session_id, result.native_run_handle, result.workspace, run_id))
            self.store.conn.execute("UPDATE tasks SET state=?, updated_at=? WHERE task_id=?",
                                    (TaskState.RUNNING, utcnow_iso(now), task["task_id"]))
            self.store.journal("launched", task_id=task["task_id"], run_id=run_id,
                               detail={"handle": result.native_run_handle, "session": result.native_session_id}, now=now)
        return TickReport("launched", "launched", task_id=task["task_id"], run_id=run_id, settled=recon["settled"],
                          details={"reservation": reservation["reservation"], "attempt_no": reservation["attempt_no"],
                                   "model": reservation["model"], "effort": reservation["effort"]})

    def _plan(self) -> TickReport:
        """Dry run: say what a tick would do. Reads only; no adapter is touched."""
        details: Dict[str, Any] = {"dry_run": True}
        uncertain = [dict(r) for r in self.store.rows("SELECT run_id, task_id, provider, state FROM runs WHERE state IN ('launching','unknown')")]
        if uncertain:
            return TickReport("would-block", StopReason.RECONCILE_PENDING, details={**details, "uncertain": uncertain})
        active = [dict(r) for r in self.store.rows("SELECT run_id, task_id FROM runs WHERE role='implementation' AND state IN ('reserved','launching','running')")]
        if len(active) >= int(self.policy.limits["active_writers"]):
            return TickReport("would-block", StopReason.WRITER_BUSY, details={**details, "active": active})
        task = self._eligible_task()
        if task is None:
            return TickReport("would-idle", StopReason.EMPTY_QUEUE, details=details)
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

    def status(self) -> Dict[str, Any]:
        tasks = []
        for t in self.store.rows("SELECT * FROM tasks ORDER BY created_at, task_id"):
            pool = self.store.row("SELECT * FROM budget_pools WHERE pool_id=?", (t["pool_id"],))
            run = self.store.row("SELECT * FROM runs WHERE task_id=? ORDER BY reserved_at DESC, run_id DESC LIMIT 1", (t["task_id"],))
            pr = self.store.row("SELECT * FROM prs WHERE task_id=? ORDER BY updated_at DESC LIMIT 1", (t["task_id"],))
            tasks.append({
                "task_id": t["task_id"], "card": t["card_number"], "workstream": t["workstream"], "owner": t["owner_agent"],
                "provider": t["provider"], "state": t["state"], "stop_reason": t["stop_reason"], "blocker": t["blocker"],
                "next_eligible_at": t["next_eligible_at"], "pr": pr["pr_number"] if pr else None,
                "funding_pool": run["funding_pool"] if run else self.policy.providers.get(t["provider"], {}).get("funding_pool"),
                "pool": {"id": pool["pool_id"], "unit": pool["unit"], "ceiling": pool["task_ceiling"],
                         "consumed": pool["consumed"], "reserved": pool["reserved"], "overshoot": pool["overshoot"],
                         "remaining": budget.remaining(pool), "attempts_used": pool["attempts_used"],
                         "attempts_max": pool["attempts_max"],
                         "attempts_remaining": int(pool["attempts_max"]) - int(pool["attempts_used"])},
                "last_run": {"run_id": run["run_id"], "state": run["state"], "stop_reason": run["stop_reason"],
                             "model": run["model"], "effort": run["effort"], "session": run["native_session_id"],
                             "handle": run["native_run_handle"], "workspace": run["workspace"]} if run else None,
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
        }
