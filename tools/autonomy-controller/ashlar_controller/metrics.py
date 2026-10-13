"""Counts from the ledger and the operator's report. LLM-free: pure SQL over the state file, no adapter,
no policy, no network.

`collect(store)` reads every table the controller writes and returns one dict whose keys are always
present, so a caller can index it without guarding. The four usage kinds stay separate: the estimated
API value is reported under `usage` as an estimate and is never added to `runs.consumed_total` or to
any turns figure (README, "Budget accounting").

`render_report(status, metrics)` turns `Controller.status()` and `collect()` into Markdown. It uses
`.get` everywhere, so a status dict written by an older controller (no `pr_record`, no `evidence`)
still renders.
"""
from __future__ import annotations

import json
import math
from typing import Any, Dict, List, Optional

from .store import Store, utcnow_iso

ACTIVE_RUN_STATES = ("reserved", "launching", "running")
ESTIMATE_CAVEAT = "estimated_api_value is an estimate of API list value, not a charge"
ESTIMATED_API_VALUE_KIND = "estimated_api_value"


# ---------------------------------------------------------------------------------------------------
# collect
# ---------------------------------------------------------------------------------------------------

def _count_by(store: Store, table: str, column: str, where: str = "") -> Dict[str, int]:
    sql = f"SELECT COALESCE({column}, '') AS k, COUNT(*) AS n FROM {table}"
    if where:
        sql += f" WHERE {where}"
    sql += " GROUP BY k ORDER BY k"
    return {str(r["k"]): int(r["n"]) for r in store.rows(sql)}


def _scalar(store: Store, sql: str, params: tuple = (), default: float = 0.0) -> float:
    row = store.row(sql, params)
    if row is None or row[0] is None:
        return default
    return float(row[0])


def _int(store: Store, sql: str, params: tuple = ()) -> int:
    return int(_scalar(store, sql, params, 0.0))


def collect(store: Store, now: Optional[float] = None) -> Dict[str, Any]:
    """Every key below is present on every ledger, including an empty one."""
    runs_consumed = _scalar(store, "SELECT COALESCE(SUM(consumed), 0) FROM runs WHERE settled_at IS NOT NULL")
    placeholders = ",".join("?" for _ in ACTIVE_RUN_STATES)
    reserved_active = _scalar(
        store, f"SELECT COALESCE(SUM(reservation), 0) FROM runs WHERE state IN ({placeholders})", ACTIVE_RUN_STATES)
    overshoot = _scalar(store, "SELECT COALESCE(SUM(overshoot), 0) FROM budget_pools")
    attempts_used = _int(store, "SELECT COALESCE(SUM(attempts_used), 0) FROM budget_pools")

    retries_row = store.row("SELECT COUNT(*) AS identities, COALESCE(SUM(count), 0) AS total "
                            "FROM failure_retries WHERE count > 0")

    latest_usage = [dict(r) for r in store.rows(
        "SELECT provider, kind, unit, freshness, value, total, observed_at FROM usage_observations "
        "WHERE id IN (SELECT MAX(id) FROM usage_observations GROUP BY provider, kind) ORDER BY provider, kind")]
    estimated_by_provider = {
        str(r["provider"]): float(r["s"]) for r in store.rows(
            "SELECT provider, COALESCE(SUM(delta), 0) AS s FROM usage_observations WHERE kind=? "
            "GROUP BY provider ORDER BY provider", (ESTIMATED_API_VALUE_KIND,))}

    events_row = store.row("SELECT COUNT(*) AS total, COUNT(handled_at) AS handled FROM events")

    paused = store.control("paused")
    disabled_raw = store.control("disabled_providers")
    try:
        disabled = list(json.loads(disabled_raw)) if disabled_raw else []
    except ValueError:
        disabled = []

    return {
        "generated_at": utcnow_iso(now),
        "controller_id": store.meta("controller_id"),
        "schema_version": store.meta("schema_version"),
        "tasks": {
            "total": _int(store, "SELECT COUNT(*) FROM tasks"),
            "by_state": _count_by(store, "tasks", "state"),
        },
        "runs": {
            "total": _int(store, "SELECT COUNT(*) FROM runs"),
            "by_state": _count_by(store, "runs", "state"),
            "by_stop_reason": _count_by(store, "runs", "stop_reason", "stop_reason IS NOT NULL"),
            "by_role": _count_by(store, "runs", "role"),
            "consumed_total": runs_consumed,
            "reserved_active_total": reserved_active,
            "overshoot_total": overshoot,
            "attempts_used_total": attempts_used,
        },
        "prs": {
            "total": _int(store, "SELECT COUNT(*) FROM prs"),
            "open": _int(store, "SELECT COUNT(*) FROM prs WHERE state='open'"),
            "merged": _int(store, "SELECT COUNT(*) FROM prs WHERE outcome='merged'"),
            "closed_unmerged": _int(store, "SELECT COUNT(*) FROM prs WHERE outcome='closed-unmerged'"),
            "repair_rounds_total": _int(store, "SELECT COALESCE(SUM(repair_rounds_used), 0) FROM prs"),
            "by_outcome": _count_by(store, "prs", "outcome", "outcome IS NOT NULL"),
        },
        "transfers": {
            "total": _int(store, "SELECT COUNT(*) FROM journal WHERE kind='provider-transferred'"),
            "refused": _int(store, "SELECT COUNT(*) FROM journal WHERE kind='transfer-refused'"),
        },
        "retries": {
            "identities": int(retries_row["identities"]) if retries_row else 0,
            "total": int(retries_row["total"]) if retries_row else 0,
        },
        "usage": {
            "latest": latest_usage,
            "estimated_api_value_total_by_provider": estimated_by_provider,
        },
        "events": {
            "total": int(events_row["total"]) if events_row else 0,
            "handled": int(events_row["handled"]) if events_row else 0,
        },
        "journal": {
            "total": _int(store, "SELECT COUNT(*) FROM journal"),
            "by_kind": _count_by(store, "journal", "kind"),
        },
        "controls": {
            "paused": bool(paused),
            "disabled_providers": disabled,
        },
    }


# ---------------------------------------------------------------------------------------------------
# render_report
# ---------------------------------------------------------------------------------------------------

def _short(sha: Any, n: int = 12) -> str:
    return str(sha)[:n] if sha else "-"


def _num(v: Any) -> str:
    if v is None:
        return "-"
    try:
        f = float(v)
    except (TypeError, ValueError):
        return str(v)
    return f"{f:g}"


def _table(headers: List[str], rows: List[List[Any]]) -> List[str]:
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"]
    if not rows:
        out.append("| " + " | ".join(["(none)"] + [""] * (len(headers) - 1)) + " |")
    for r in rows:
        out.append("| " + " | ".join("" if c is None else str(c) for c in r) + " |")
    return out


def permitted_attempts(pool: Dict[str, Any]) -> Optional[int]:
    """The attempts the budget still permits: min(attempts left, floor(remaining / invocation ceiling)),
    where remaining is ceiling - consumed - reserved and the invocation ceiling is the one a reservation
    needs (`budget.reserve` refuses below it). The live run of 2026-10-13 printed "2/3 (1 permitted)" for a
    pool with 10 turns left when an invocation needed 12: the attempt left could never be reserved.

    `pool` is a `Controller.status()` pool. A status written before the invocation ceiling was in it
    gives the attempts left alone; None when not even the attempts are known."""
    left = pool.get("attempts_remaining")
    if left is None:
        used, amax = pool.get("attempts_used"), pool.get("attempts_max")
        if used is None or amax is None:
            return None
        left = int(amax) - int(used)
    left = int(left)
    remaining = pool.get("remaining")
    if remaining is None and all(pool.get(k) is not None for k in ("ceiling", "consumed", "reserved")):
        remaining = float(pool["ceiling"]) - float(pool["consumed"]) - float(pool["reserved"])
    invocation = pool.get("invocation_ceiling")
    if remaining is None or invocation is None or float(invocation) <= 0:
        return left
    return max(0, min(left, int(math.floor(float(remaining) / float(invocation)))))


def _render_task(t: Dict[str, Any]) -> List[str]:
    lines = [f"### {t.get('task_id', '?')}", ""]
    state = t.get("state") or "-"
    stop = t.get("stop_reason") or "-"
    lines.append(f"- State: {state} (stop reason: {stop})")
    lines.append(f"- Blocker: {t.get('blocker') or '-'}")
    pool = t.get("pool") or {}
    used = pool.get("attempts_used")
    amax = pool.get("attempts_max")
    lines.append(f"- Attempts: {_num(used)}/{_num(amax)} ({_num(permitted_attempts(pool))} permitted)")
    lines.append(f"- Budget: {_num(pool.get('consumed'))}+{_num(pool.get('reserved'))} of "
                 f"{_num(pool.get('ceiling'))} {pool.get('unit') or ''}".rstrip())
    lines.append(f"- Funding pool: {t.get('funding_pool') or '-'}")
    lines.append(f"- Provider: {t.get('provider') or '-'}")
    tr = t.get("transfers")
    if tr:
        lines.append(f"- Provider transfers: {_num(tr.get('used'))}/{_num(tr.get('max'))}")
    pr = t.get("pr_record")
    if pr:
        state = pr.get("state") or "-"
        outcome = pr.get("outcome")
        # A merged PR's state and outcome are both "merged"; the word is printed once (the pilot's report
        # read "PR #777 merged merged"). An outcome that differs from the state (closed / closed-unmerged)
        # is printed after it.
        label = state if outcome == state else f"{state} {outcome or '-'}"
        lines.append(f"- PR #{pr.get('number')} {label} "
                     f"{_short(pr.get('head_sha'))} {_short(pr.get('merge_sha'))} rounds {_num(pr.get('repair_rounds_used'))}")
    elif t.get("pr") is not None:
        lines.append(f"- PR #{t.get('pr')}")
    run = t.get("last_run")
    if run:
        lines.append(f"- Last run: {run.get('run_id') or '-'} {run.get('state') or '-'} {run.get('stop_reason') or '-'} "
                     f"{run.get('model') or '-'}/{run.get('effort') or '-'} session {run.get('session') or '-'}")
    evidence = t.get("evidence")
    if evidence:
        lines.append("- Evidence:")
        for key in sorted(evidence):
            lines.append(f"  - {key}: {evidence[key] if evidence[key] is not None else '-'}")
    lines.append("")
    return lines


def render_report(status: Dict[str, Any], metrics: Dict[str, Any]) -> str:
    status = status or {}
    metrics = metrics or {}
    policy = status.get("policy") or {}
    paused = status.get("paused")
    if paused is None:
        paused = (metrics.get("controls") or {}).get("paused", False)
    disabled = status.get("disabled_providers")
    if disabled is None:
        disabled = (metrics.get("controls") or {}).get("disabled_providers") or []

    out: List[str] = []
    out.append(f"# Controller {status.get('controller_id') or metrics.get('controller_id') or '?'}")
    out.append("")
    out.append(f"- State: {status.get('state_path') or '-'}")
    out.append(f"- Policy: {policy.get('version') or '-'} ({_short(policy.get('sha256'))})")
    if isinstance(paused, dict):
        out.append(f"- Paused: yes ({paused.get('reason') or 'no reason recorded'})")
    else:
        out.append(f"- Paused: {'yes' if paused else 'no'}")
    out.append(f"- Disabled providers: {', '.join(str(d) for d in disabled) if disabled else 'none'}")
    out.append("")

    out.append("## Tasks")
    out.append("")
    tasks = status.get("tasks") or []
    if not tasks:
        out.append("(no tasks)")
        out.append("")
    for t in tasks:
        out.extend(_render_task(t or {}))

    out.append("## Metrics")
    out.append("")
    out.append(f"Generated at {metrics.get('generated_at') or '-'}")
    out.append("")
    runs = metrics.get("runs") or {}
    out.append("### Runs by stop reason")
    out.append("")
    out.extend(_table(["stop reason", "runs"], [[k, v] for k, v in sorted((runs.get("by_stop_reason") or {}).items())]))
    out.append("")
    out.append(f"Runs {_num(runs.get('total'))}; consumed {_num(runs.get('consumed_total'))}; "
               f"reserved (active) {_num(runs.get('reserved_active_total'))}; overshoot {_num(runs.get('overshoot_total'))}; "
               f"attempts used {_num(runs.get('attempts_used_total'))}")
    out.append("")

    prs = metrics.get("prs") or {}
    out.append("### PRs by outcome")
    out.append("")
    out.extend(_table(["outcome", "prs"], [[k, v] for k, v in sorted((prs.get("by_outcome") or {}).items())]))
    out.append("")
    out.append(f"PRs {_num(prs.get('total'))}; open {_num(prs.get('open'))}; merged {_num(prs.get('merged'))}; "
               f"closed unmerged {_num(prs.get('closed_unmerged'))}; repair rounds {_num(prs.get('repair_rounds_total'))}")
    out.append("")
    transfers = metrics.get("transfers") or {}
    out.append(f"Provider transfers {_num(transfers.get('total', 0))}; refused {_num(transfers.get('refused', 0))}")
    out.append("")

    usage = metrics.get("usage") or {}
    out.append("### Usage (latest per provider and kind)")
    out.append("")
    out.append(ESTIMATE_CAVEAT)
    out.append("")
    out.extend(_table(["provider", "kind", "unit", "freshness", "value", "total", "observed at"],
                      [[u.get("provider"), u.get("kind"), u.get("unit"), u.get("freshness"), _num(u.get("value")),
                        _num(u.get("total")), u.get("observed_at")] for u in (usage.get("latest") or [])]))
    est = usage.get("estimated_api_value_total_by_provider") or {}
    if est:
        out.append("")
        out.append("Estimated API value by provider (estimate, not a charge): "
                   + "; ".join(f"{p} {_num(v)}" for p, v in sorted(est.items())))
    out.append("")

    retries = metrics.get("retries") or {}
    out.append("### Retries")
    out.append("")
    out.append(f"Diagnosed failure identities retried: {_num(retries.get('identities'))}; retries total: "
               f"{_num(retries.get('total'))}")
    out.append("")

    journal = metrics.get("journal") or {}
    out.append("### Journal by kind")
    out.append("")
    out.extend(_table(["kind", "rows"], [[k, v] for k, v in sorted((journal.get("by_kind") or {}).items())]))
    out.append("")
    out.append(f"Journal rows {_num(journal.get('total'))}")
    out.append("")
    return "\n".join(out)
