"""Command line for the controller.

    controller.py --state <db> --policy <json> <command> ...

The state path and the policy path may also come from ASHLAR_CONTROLLER_STATE and
ASHLAR_CONTROLLER_POLICY. Exit codes: 0 ok; 1 other error; 2 usage; 3 state missing or corrupt;
4 policy refused; 5 writer lease not held; 6 budget refused.
"""
from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import uuid
from typing import Any, Dict, List, Optional

from . import budget, events, outbox
from .adapters.fake import FakeAdapter
from .controller import Controller
from .models import ControllerError, RunState, RunStatus, StopReason, TaskState
from .policy import Policy, load_policy
from .store import Store


def _print(obj: Any, as_json: bool) -> None:
    if as_json:
        print(json.dumps(obj, indent=2, sort_keys=True, default=str))
    elif isinstance(obj, dict):
        for k, v in obj.items():
            print(f"{k}: {v}")
    else:
        print(obj)


def _format_status(s: Dict[str, Any]) -> str:
    lines = [f"controller {s['controller_id']}  state {s['state_path']}",
             f"policy {s['policy']['version']} ({s['policy']['sha256'][:12]}) from {s['policy']['source']}; "
             f"paid inference {'ENABLED' if s['policy']['paid_inference_enabled'] else 'off'}",
             f"paused: {s['paused']['reason'] if s['paused'] else 'no'}   disabled providers: "
             f"{', '.join(s['disabled_providers']) or 'none'}   outbox pending: {s['outbox_pending']}"]
    if s["lease"]:
        lz = s["lease"]
        lines.append(f"lease: {lz['holder_id']} on {lz['host']} pid {lz['pid']} heartbeat {lz['heartbeat_at']}"
                     f"{' (released)' if lz['released_at'] else ''}")
    else:
        lines.append("lease: none")
    if not s["tasks"]:
        lines.append("tasks: none (idle)")
    for t in s["tasks"]:
        p = t["pool"]
        lines.append(f"- {t['task_id']} card #{t['card']} [{t['workstream']}] owner {t['owner']} provider {t['provider']} "
                     f"pr {t['pr'] or '-'} state {t['state']}" + (f" ({t['stop_reason']})" if t["stop_reason"] else ""))
        lines.append(f"    budget {p['consumed']:g}+{p['reserved']:g} of {p['ceiling']:g} {p['unit']} "
                     f"(remaining {p['remaining']:g}, overshoot {p['overshoot']:g}); attempts {p['attempts_used']}/{p['attempts_max']} "
                     f"({p['attempts_remaining']} permitted); funding pool {t['funding_pool']}")
        if t["blocker"]:
            lines.append(f"    blocker: {t['blocker']}")
        if t["next_eligible_at"]:
            lines.append(f"    next eligible: {t['next_eligible_at']}")
        if t["last_run"]:
            r = t["last_run"]
            lines.append(f"    last run {r['run_id']} {r['state']} {r['stop_reason'] or ''} model {r['model']}/{r['effort']} "
                         f"session {r['session'] or '-'} workspace {r['workspace'] or '-'}")
    for u in s["usage"]:
        lines.append(f"usage {u['provider']} {u['kind']}: {u['value']} of {u['total']} {u['unit']} ({u['freshness']}, {u['observed_at']})")
    return "\n".join(lines)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="controller", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--state", default=os.environ.get("ASHLAR_CONTROLLER_STATE"), help="controller SQLite file")
    p.add_argument("--policy", default=os.environ.get("ASHLAR_CONTROLLER_POLICY"), help="operator policy JSON (outside every checkout)")
    p.add_argument("--holder", default=os.environ.get("ASHLAR_CONTROLLER_HOLDER"), help="writer lease holder id (default: host:pid)")
    p.add_argument("--json", action="store_true", help="machine-readable output")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("init", help="create a new, empty controller state (refuses to overwrite)")
    s.add_argument("--controller-id", default=None)

    sub.add_parser("status", help="owner, task, PR, state, remaining permitted attempts, funding pool, blocker")
    s = sub.add_parser("pause", help="stop launching before the next tick"); s.add_argument("--reason", required=True)
    sub.add_parser("resume", help="allow launches again")

    s = sub.add_parser("tick", help="one controller step: settle, reconcile, then at most one launch")
    s.add_argument("--dry-run", action="store_true", help="say what would happen; no writes, no adapter calls")
    s.add_argument("--adapter", choices=["fake"], default="fake", help="PR 1 ships only the fake adapter")

    s = sub.add_parser("task", help="task records"); ts = s.add_subparsers(dest="task_cmd", required=True)
    a = ts.add_parser("add")
    a.add_argument("--task-id", required=True); a.add_argument("--card", type=int); a.add_argument("--workstream", required=True)
    a.add_argument("--provider", required=True); a.add_argument("--title", default=""); a.add_argument("--handoff-ref")
    a.add_argument("--decision-ref", action="append", default=[]); a.add_argument("--parent"); a.add_argument("--priority", type=int, default=100)
    a.add_argument("--owner", default="claude"); a.add_argument("--base-sha")
    ts.add_parser("list")
    a = ts.add_parser("show"); a.add_argument("task_id")
    a = ts.add_parser("set-state"); a.add_argument("task_id"); a.add_argument("state"); a.add_argument("--reason"); a.add_argument("--blocker")

    s = sub.add_parser("event", help="ingest an external event once"); es = s.add_subparsers(dest="event_cmd", required=True)
    a = es.add_parser("ingest"); a.add_argument("--event-id", required=True); a.add_argument("--source", default="operator")
    a.add_argument("--kind", required=True); a.add_argument("--task-id"); a.add_argument("--payload", default="{}")

    s = sub.add_parser("run", help="run records"); rs = s.add_subparsers(dest="run_cmd", required=True)
    rs.add_parser("list")
    a = rs.add_parser("settle", help="record a run's outcome by hand (operator reconciliation)")
    a.add_argument("run_id"); a.add_argument("--stop-reason", required=True); a.add_argument("--consumed", type=float)
    a.add_argument("--failure-identity"); a.add_argument("--result", default="{}")
    a = rs.add_parser("cancel"); a.add_argument("run_id")

    s = sub.add_parser("lease", help="the writer lease"); ls = s.add_subparsers(dest="lease_cmd", required=True)
    ls.add_parser("show")
    a = ls.add_parser("break", help="operator confirmation that an expired holder is stopped")
    a.add_argument("--holder", required=True); a.add_argument("--confirm-stopped", action="store_true")
    a = ls.add_parser("release"); a.add_argument("--holder", required=True)

    s = sub.add_parser("outbox", help="milestone posts"); os_ = s.add_subparsers(dest="outbox_cmd", required=True)
    os_.add_parser("list")
    a = os_.add_parser("enqueue"); a.add_argument("--key", required=True); a.add_argument("--kind", required=True)
    a.add_argument("--target", required=True); a.add_argument("--slug", required=True); a.add_argument("--body-file", required=True)
    a = os_.add_parser("flush"); a.add_argument("--poster", choices=["fake", "gh"], default="fake"); a.add_argument("--repo", default="IanFrelinger/Ashlar")

    s = sub.add_parser("worker", help="worker checkouts (policy may never live inside one)"); ws = s.add_subparsers(dest="worker_cmd", required=True)
    a = ws.add_parser("register"); a.add_argument("--checkout", required=True)
    ws.add_parser("list")

    s = sub.add_parser("provider", help="enable or disable a provider for dispatch"); ps = s.add_subparsers(dest="provider_cmd", required=True)
    a = ps.add_parser("disable"); a.add_argument("name")
    a = ps.add_parser("enable"); a.add_argument("name")

    sub.add_parser("policy", help="show the validated active policy")
    return p


def _open(args, read_only: bool = False) -> Store:
    if not args.state:
        raise ControllerError("--state (or ASHLAR_CONTROLLER_STATE) is required")
    return Store.open(args.state, read_only=read_only)


def _policy(args, store: Optional[Store]) -> Policy:
    if not args.policy:
        raise ControllerError("--policy (or ASHLAR_CONTROLLER_POLICY) is required")
    checkouts = store.worker_checkouts() if store is not None else []
    return load_policy(args.policy, checkouts)


def _controller(args, store: Store, policy: Policy) -> Controller:
    holder = args.holder or f"{socket.gethostname()}:{os.getpid()}"
    adapters = {"fake": FakeAdapter()}
    return Controller(store, policy, adapters, holder_id=holder)


def main(argv: Optional[List[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return _dispatch(args)
    except ControllerError as e:
        print(f"controller: {e}", file=sys.stderr)
        return e.exit_code


def _dispatch(args) -> int:
    if args.cmd == "init":
        if not args.state:
            raise ControllerError("--state is required")
        store = Store.init(args.state, args.controller_id or f"ctl-{uuid.uuid4().hex[:8]}")
        _print({"initialised": args.state, "controller_id": store.meta("controller_id")}, args.json)
        return 0

    if args.cmd == "policy":
        policy = _policy(args, None)
        _print(policy.to_json() if args.json else f"policy {policy.version} sha256 {policy.sha256} from {policy.source_path}\n"
               + json.dumps(policy.to_json(), indent=2, sort_keys=True), args.json)
        return 0

    read_only = args.cmd == "tick" and args.dry_run
    store = _open(args, read_only=read_only)

    if args.cmd == "lease":
        if args.lease_cmd == "show":
            row = store.lease_row(); _print(dict(row) if row else {"lease": None}, args.json); return 0
        if args.lease_cmd == "break":
            if not args.confirm_stopped:
                raise ControllerError("lease break needs --confirm-stopped: you are asserting that holder is no longer running")
            store.break_lease(args.holder, confirmed_by=os.environ.get("USER", "operator"))
            _print({"broken": args.holder}, args.json); return 0
        if args.lease_cmd == "release":
            store.release_lease(args.holder); _print({"released": args.holder}, args.json); return 0

    if args.cmd == "worker":
        if args.worker_cmd == "register":
            store.register_worker_checkout(args.checkout); _print({"registered": args.checkout}, args.json); return 0
        _print(store.worker_checkouts(), args.json); return 0

    if args.cmd == "outbox" and args.outbox_cmd in ("list", "enqueue", "flush"):
        if args.outbox_cmd == "list":
            _print([dict(r) for r in outbox.pending(store)], args.json); return 0
        if args.outbox_cmd == "enqueue":
            with open(args.body_file, encoding="utf-8") as f:
                body = f.read()
            new = outbox.enqueue(store, dedup_key=args.key, kind=args.kind, target=args.target, slug=args.slug, body=body)
            _print({"enqueued": new, "key": args.key}, args.json); return 0
        poster = outbox.FakePoster() if args.poster == "fake" else outbox.GhIssuePoster(args.repo)
        rec = outbox.reconcile(store, poster); counts = outbox.flush(store, poster)
        _print({"reconciled": rec, "flushed": counts}, args.json); return 0

    policy = _policy(args, store)
    ctl = _controller(args, store, policy)

    if args.cmd == "status":
        s = ctl.status(); _print(s if args.json else _format_status(s), args.json); return 0
    if args.cmd == "pause":
        ctl.pause(args.reason); _print({"paused": args.reason}, args.json); return 0
    if args.cmd == "resume":
        ctl.resume(); _print({"paused": False}, args.json); return 0
    if args.cmd == "tick":
        report = ctl.tick(dry_run=args.dry_run)
        _print(report.to_json() if args.json else f"{report.action}: {report.reason}"
               + (f" task {report.task_id}" if report.task_id else "") + (f" run {report.run_id}" if report.run_id else "")
               + (f"\n{json.dumps(report.details, sort_keys=True)}" if report.details else "")
               + ("".join(f"\nsettled {s}" for s in report.settled)), args.json)
        return 0
    if args.cmd == "provider":
        current = set(json.loads(store.control("disabled_providers") or "[]"))
        if args.provider_cmd == "disable":
            current.add(args.name)
        else:
            current.discard(args.name)
        store.set_control("disabled_providers", json.dumps(sorted(current)))
        _print({"disabled_providers": sorted(current)}, args.json); return 0
    if args.cmd == "task":
        if args.task_cmd == "add":
            created = ctl.ensure_task(args.task_id, workstream=args.workstream, provider=args.provider, card_number=args.card,
                                      title=args.title, handoff_ref=args.handoff_ref, decision_refs=args.decision_ref,
                                      parent_task_id=args.parent, owner_agent=args.owner, priority=args.priority, base_sha=args.base_sha)
            _print({"task_id": args.task_id, "created": created}, args.json); return 0
        if args.task_cmd == "list":
            _print([dict(r) for r in store.rows("SELECT task_id, card_number, workstream, provider, state, stop_reason, pool_id FROM tasks ORDER BY created_at")], args.json); return 0
        if args.task_cmd == "show":
            row = store.row("SELECT * FROM tasks WHERE task_id=?", (args.task_id,))
            if row is None:
                raise ControllerError(f"no task {args.task_id}")
            _print(dict(row), args.json); return 0
        if args.task_cmd == "set-state":
            allowed = {v for k, v in vars(TaskState).items() if k.isupper() and isinstance(v, str)}
            if args.state not in allowed:
                raise ControllerError(f"unknown task state {args.state}; one of {sorted(allowed)}")
            ctl.set_task_state(args.task_id, args.state, stop_reason=args.reason, blocker=args.blocker)
            _print({"task_id": args.task_id, "state": args.state}, args.json); return 0
    if args.cmd == "event":
        new = ctl.ingest_event(args.event_id, args.source, args.kind, args.task_id, json.loads(args.payload))
        _print({"event_id": args.event_id, "new": new, "duplicate": not new}, args.json); return 0
    if args.cmd == "run":
        if args.run_cmd == "list":
            _print([dict(r) for r in store.rows("SELECT run_id, task_id, attempt_no, provider, model, state, reservation, consumed, "
                                                  "stop_reason, native_session_id, native_run_handle, workspace FROM runs ORDER BY reserved_at")], args.json); return 0
        if args.run_cmd == "settle":
            run = store.row("SELECT * FROM runs WHERE run_id=?", (args.run_id,))
            if run is None:
                raise ControllerError(f"no run {args.run_id}")
            state = RunState.FINISHED if args.stop_reason == StopReason.COMPLETED else (
                RunState.CANCELLED if args.stop_reason == StopReason.CANCELLED else RunState.FAILED)
            out = ctl._apply_outcome(run, RunStatus(state=state, stop_reason=args.stop_reason, consumed_units=args.consumed,
                                                     failure_identity=args.failure_identity, result=json.loads(args.result)))
            _print(out, args.json); return 0
        if args.run_cmd == "cancel":
            _print(ctl.cancel_run(args.run_id), args.json); return 0
    raise ControllerError(f"unhandled command {args.cmd}")
