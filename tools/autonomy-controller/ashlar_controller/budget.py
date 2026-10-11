"""Budget pools, reservations and the four kinds of usage accounting.

A pool is cumulative per task and SURVIVES everything: restarts, replaced sessions, provider changes
and task splits (a child task shares its parent's pool, so splitting never resets an exhausted
budget). A reservation is written in the same transaction as the run row, so a crash between
"decided to launch" and "launched" leaves a reserved run that recovery can see.

Provider units are never converted to money. The four kinds in UsageKind are stored separately and
an `estimated_api_value` is only ever an estimate.
"""
from __future__ import annotations

import json
from typing import Any, Dict, Optional, Tuple

from .models import BudgetExhausted, PolicyError, RunState, StopReason, UsageFreshness, UsageKind, UsageObservation
from .policy import Policy
from .store import Store, utcnow_iso


def create_pool(store: Store, pool_id: str, unit: str, policy: Policy, policy_version_id: Optional[int],
                now: Optional[float] = None) -> None:
    with store.tx():
        store.conn.execute(
            "INSERT INTO budget_pools(pool_id, unit, task_ceiling, invocation_ceiling, attempts_max, "
            "policy_version_id, created_at) VALUES (?,?,?,?,?,?,?)",
            (pool_id, unit, float(policy.limits["task_turn_ceiling"]), float(policy.limits["invocation_turn_ceiling"]),
             int(policy.limits["max_attempts_per_task"]), policy_version_id, utcnow_iso(now)))


def pool_row(store: Store, pool_id: str):
    row = store.row("SELECT * FROM budget_pools WHERE pool_id=?", (pool_id,))
    if row is None:
        raise BudgetExhausted(f"no budget pool {pool_id}")
    return row


def remaining(row) -> float:
    return float(row["task_ceiling"]) - float(row["consumed"]) - float(row["reserved"])


def reserve(store: Store, *, run_id: str, task_id: str, pool_id: str, provider: str, policy: Policy,
            policy_version_id: Optional[int], holder: str, now: Optional[float] = None,
            role: str = "implementation", workspace: Optional[str] = None) -> Dict[str, Any]:
    """Atomically reserve one invocation's worth of budget and record the run as RESERVED.

    Refuses when the pool's remaining budget cannot cover the invocation ceiling, when the pool's
    attempts are used up, when the provider is disabled, or when the provider's pool is paid spend
    and paid inference is off. The limits in force are frozen onto the run (limits_json), so no later
    policy edit changes what this run may do.
    """
    spec = policy.provider(provider)
    if not spec.get("enabled", False):
        raise PolicyError(f"{StopReason.PROVIDER_DISABLED}: provider {provider} is disabled by policy {policy.version}")
    if spec["pool_kind"] == UsageKind.PAID_SPEND and not policy.funding["paid_inference_enabled"]:
        raise PolicyError(f"{StopReason.PAID_ROUTE_DISABLED}: provider {provider} funds from paid spend and "
                          f"funding.paid_inference_enabled is false")
    with store.tx():
        row = store.conn.execute("SELECT * FROM budget_pools WHERE pool_id=?", (pool_id,)).fetchone()
        if row is None:
            raise BudgetExhausted(f"no budget pool {pool_id}")
        # The pool's own ceilings were frozen when it was created; the policy's current ceiling can
        # only tighten it. A worker-side edit that raised the policy ceiling is therefore ignored.
        invocation = min(float(row["invocation_ceiling"]), float(policy.limits["invocation_turn_ceiling"]))
        attempts_max = min(int(row["attempts_max"]), int(policy.limits["max_attempts_per_task"]))
        if int(row["attempts_used"]) + 1 > attempts_max:
            raise BudgetExhausted(
                f"{StopReason.ATTEMPTS_EXHAUSTED}: pool {pool_id} has used {row['attempts_used']} of "
                f"{attempts_max} attempts")
        left = remaining(row)
        if invocation > left:
            raise BudgetExhausted(
                f"{StopReason.TASK_BUDGET_EXHAUSTED}: pool {pool_id} has {left:g} {row['unit']} left "
                f"(ceiling {row['task_ceiling']:g}, consumed {row['consumed']:g}, reserved {row['reserved']:g}); "
                f"an invocation needs {invocation:g}")
        attempt_no = int(row["attempts_used"]) + 1
        limits = {"invocation_ceiling": invocation, "task_ceiling": float(row["task_ceiling"]),
                  "attempts_max": attempts_max, "same_failure_retries": int(policy.limits["same_failure_retries"]),
                  "repair_rounds": int(policy.limits["repair_rounds"]),
                  "premium_escalations_per_pr": int(policy.limits["premium_escalations_per_pr"]),
                  "provider_transfers_per_pr": int(policy.limits["provider_transfers_per_pr"]),
                  "policy_version": policy.version, "policy_sha256": policy.sha256}
        store.conn.execute(
            "INSERT INTO runs(run_id, task_id, pool_id, attempt_no, role, provider, model, effort, speed, "
            "funding_pool, policy_version_id, limits_json, workspace, state, reservation, owner_holder, reserved_at) "
            "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            (run_id, task_id, pool_id, attempt_no, role, provider, spec["model"], spec["effort"], spec["speed"],
             spec["funding_pool"], policy_version_id, json.dumps(limits, sort_keys=True), workspace,
             RunState.RESERVED, invocation, holder, utcnow_iso(now)))
        store.conn.execute(
            "UPDATE budget_pools SET reserved = reserved + ?, attempts_used = attempts_used + 1 WHERE pool_id=?",
            (invocation, pool_id))
        store.conn.execute("UPDATE tasks SET state='reserved', updated_at=? WHERE task_id=?",
                           (utcnow_iso(now), task_id))
        store.journal("reserved", task_id=task_id, run_id=run_id,
                      detail={"reservation": invocation, "attempt_no": attempt_no, "provider": provider}, now=now)
    return {"run_id": run_id, "attempt_no": attempt_no, "reservation": invocation, "limits": limits,
            "model": spec["model"], "effort": spec["effort"], "speed": spec["speed"]}


def settle(store: Store, run_id: str, *, state: str, stop_reason: str, consumed: Optional[float],
           failure_identity: Optional[str] = None, result: Optional[Dict[str, Any]] = None,
           now: Optional[float] = None) -> Dict[str, Any]:
    """Close a run: release its reservation and charge what it consumed.

    Unknown consumption is charged at the full reservation (the conservative reading). Consumption
    above the reservation is charged in full and recorded as overshoot, because a provider's
    invocation ceiling is an estimate that can be exceeded.
    """
    with store.tx():
        run = store.conn.execute("SELECT * FROM runs WHERE run_id=?", (run_id,)).fetchone()
        if run is None:
            raise BudgetExhausted(f"no run {run_id}")
        if run["settled_at"] is not None:
            return {"run_id": run_id, "already_settled": True}
        reservation = float(run["reservation"])
        charged = reservation if consumed is None else max(0.0, float(consumed))
        overshoot = max(0.0, charged - reservation)
        at = utcnow_iso(now)
        store.conn.execute(
            "UPDATE runs SET state=?, stop_reason=?, consumed=?, failure_identity=?, result_json=?, "
            "finished_at=COALESCE(finished_at, ?), settled_at=? WHERE run_id=?",
            (state, stop_reason, charged, failure_identity, json.dumps(result or {}, sort_keys=True), at, at, run_id))
        store.conn.execute(
            "UPDATE budget_pools SET reserved = MAX(0, reserved - ?), consumed = consumed + ?, "
            "overshoot = overshoot + ? WHERE pool_id=?",
            (reservation, charged, overshoot, run["pool_id"]))
        store.journal("settled", task_id=run["task_id"], run_id=run_id,
                      detail={"state": state, "stop_reason": stop_reason, "charged": charged,
                              "overshoot": overshoot, "consumed_reported": consumed}, now=now)
    return {"run_id": run_id, "charged": charged, "overshoot": overshoot, "stop_reason": stop_reason}


def failure_retry_allowed(store: Store, pool_id: str, failure_identity: str, limit: int) -> Tuple[bool, int]:
    row = store.row("SELECT count FROM failure_retries WHERE pool_id=? AND failure_identity=?",
                    (pool_id, failure_identity))
    used = 0 if row is None else int(row["count"])
    return used < limit, used


def bump_failure_retry(store: Store, pool_id: str, failure_identity: str) -> int:
    with store.tx():
        store.conn.execute(
            "INSERT INTO failure_retries(pool_id, failure_identity, count) VALUES (?,?,1) "
            "ON CONFLICT(pool_id, failure_identity) DO UPDATE SET count = count + 1",
            (pool_id, failure_identity))
        row = store.conn.execute("SELECT count FROM failure_retries WHERE pool_id=? AND failure_identity=?",
                                 (pool_id, failure_identity)).fetchone()
    return int(row["count"])


def record_usage(store: Store, obs: UsageObservation, run_id: Optional[str], now: Optional[float] = None) -> Optional[float]:
    """Store an observation and return the DELTA against the previous cumulative value of the same
    provider, session and kind. A provider's session cost may include earlier runs; only the delta is
    this run's. A first observation's delta is its value; a decrease (a reset) counts as zero."""
    delta: Optional[float] = None
    with store.tx():
        if obs.value is not None and obs.freshness != UsageFreshness.UNAVAILABLE:
            prev = store.conn.execute(
                "SELECT value FROM usage_observations WHERE provider=? AND kind=? AND unit=? AND "
                "COALESCE(native_session_id,'')=COALESCE(?,'') AND value IS NOT NULL ORDER BY id DESC LIMIT 1",
                (obs.provider, obs.kind, obs.unit, obs.native_session_id)).fetchone()
            delta = float(obs.value) if prev is None else max(0.0, float(obs.value) - float(prev["value"]))
        store.conn.execute(
            "INSERT INTO usage_observations(run_id, provider, native_session_id, kind, unit, freshness, value, total, "
            "delta, observed_at, raw) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
            (run_id, obs.provider, obs.native_session_id, obs.kind, obs.unit, obs.freshness, obs.value, obs.total,
             delta, obs.observed_at or utcnow_iso(now), json.dumps(obs.raw, sort_keys=True)))
    return delta


def latest_allowance(store: Store, provider: str):
    return store.row(
        "SELECT * FROM usage_observations WHERE provider=? AND kind=? ORDER BY id DESC LIMIT 1",
        (provider, UsageKind.SUBSCRIPTION_ALLOWANCE))


def allowance_decision(policy: Policy, obs, invocation_ceiling: float) -> Tuple[bool, str]:
    """May we launch, given the latest subscription-allowance observation?

    Fresh and quantified: launch only if what remains after the interactive reserve covers one
    invocation. Stale or unavailable: the policy says whether to wait or to rely on task ceilings.
    """
    fraction = float(policy.funding["interactive_reserve_fraction"])
    if obs is None or obs["freshness"] == UsageFreshness.UNAVAILABLE or obs["value"] is None or obs["total"] is None:
        if policy.funding["unobservable_allowance"] == "wait":
            return False, StopReason.ALLOWANCE_UNOBSERVABLE
        return True, "allowance-unobservable:task-ceiling-only"
    if obs["freshness"] == UsageFreshness.STALE:
        if policy.funding["unobservable_allowance"] == "wait":
            return False, StopReason.ALLOWANCE_UNOBSERVABLE
        return True, "allowance-stale:task-ceiling-only"
    total = float(obs["total"])
    used = float(obs["value"])
    remaining_allowance = total - used
    reserve_units = fraction * total
    if remaining_allowance - reserve_units < invocation_ceiling:
        return False, StopReason.ALLOWANCE_RESERVE
    return True, "allowance-ok"
