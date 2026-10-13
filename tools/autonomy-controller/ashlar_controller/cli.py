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
from .controller import WRITER_ROLES, Controller
from .models import BRIEF_MAX_CHARS, ControllerError, RunState, RunStatus, StopReason, TaskState
from .policy import Policy, load_policy
from .store import Store


ADAPTER_CHOICES = ["fake", "claude", "codex", "cursor"]


def _brief(text: str) -> str:
    """`task add --brief`: a usage error (exit 2) when it is longer than BRIEF_MAX_CHARS, before the state is
    opened, so nothing is written. Controller.ensure_task refuses it too, for a caller that is not this CLI."""
    if len(text) > BRIEF_MAX_CHARS:
        raise argparse.ArgumentTypeError(f"{len(text)} characters; a brief is at most {BRIEF_MAX_CHARS} "
                                         f"(it is the PR title and the commit subject)")
    return text


def _print(obj: Any, as_json: bool) -> None:
    if as_json:
        print(json.dumps(obj, indent=2, sort_keys=True, default=str))
    elif isinstance(obj, dict):
        for k, v in obj.items():
            print(f"{k}: {v}")
    else:
        print(obj)


def _format_status(s: Dict[str, Any]) -> str:
    from .metrics import permitted_attempts   # the report's figure, so `status` and `report` agree
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
        if t.get("pr_record"):
            pr = t["pr_record"]
            lines.append(f"    PR #{pr['number']} {pr['state']}{' ' + pr['outcome'] if pr['outcome'] else ''} branch {pr['branch'] or '-'} "
                         f"base {(pr['base_sha'] or '-')[:12]} head {(pr['head_sha'] or '-')[:12]} "
                         f"merge {(pr['merge_sha'] or '-')[:12]} repair rounds {pr['repair_rounds_used']}")
        lines.append(f"    budget {p['consumed']:g}+{p['reserved']:g} of {p['ceiling']:g} {p['unit']} "
                     f"(remaining {p['remaining']:g}, overshoot {p['overshoot']:g}); attempts {p['attempts_used']}/{p['attempts_max']} "
                     f"({permitted_attempts(p)} permitted); funding pool {t['funding_pool']}")
        if t["blocker"]:
            lines.append(f"    blocker: {t['blocker']}")
        if t["next_eligible_at"]:
            lines.append(f"    next eligible: {t['next_eligible_at']}")
        if t["last_run"]:
            r = t["last_run"]
            lines.append(f"    last run {r['run_id']} {r['state']} {r['stop_reason'] or ''} model {r['model']}/{r['effort']} "
                         f"session {r['session'] or '-'} workspace {r['workspace'] or '-'}")
        if t.get("evidence"):
            e = t["evidence"]
            lines.append("    evidence: " + "  ".join(f"{k} {e.get(k) or '-'}" for k in
                                                     ("run_dir", "manifest", "stdout", "stderr", "transcript", "archive")))
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

    s = sub.add_parser("tick", help="one controller step: settle, reconcile, observe open PRs, then at most one launch")
    s.add_argument("--dry-run", action="store_true", help="say what would happen; no writes, no adapter calls")
    s.add_argument("--adapter", choices=ADAPTER_CHOICES, action="append", default=None,
                   help="register a provider adapter; repeatable (e.g. --adapter claude --adapter codex). fake is always "
                        "registered. claude, codex, cursor: the installed CLIs, runs under <state dir>/runs")
    s.add_argument("--gh", choices=["none", "cli"], default="none",
                   help="none (default): no PR lifecycle, a completed run is done. cli: the PR lifecycle through "
                        "the gh executable (clone, push, open, observe by head SHA, repair, merge)")
    s.add_argument("--workspaces", default=None, help="where worker clones live (default <state dir>/workspaces)")

    s = sub.add_parser("pr", help="PR records (PR 3)"); prs_ = s.add_subparsers(dest="pr_cmd", required=True)
    prs_.add_parser("list")
    a = prs_.add_parser("show"); a.add_argument("number", type=int)
    a = prs_.add_parser("observe", help="observe one open PR by its current head now (merge, repair or park as it demands)")
    a.add_argument("number", type=int); a.add_argument("--adapter", choices=ADAPTER_CHOICES, action="append", default=None)
    a.add_argument("--gh", choices=["cli"], default="cli"); a.add_argument("--workspaces", default=None)

    s = sub.add_parser("readiness", help="readiness gates: run one, or apply a saved report to a task")
    rd = s.add_subparsers(dest="readiness_cmd", required=True)
    a = rd.add_parser("run", help="run one gate command in a workspace, print the report, optionally apply it to a task")
    a.add_argument("--gate", required=True, choices=["repo-gates", "readiness-local", "container-tests"])
    a.add_argument("--workspace", required=True); a.add_argument("--task-id")
    a = rd.add_parser("evaluate", help="apply a saved gate report (the JSON `readiness run` prints) to a task")
    a.add_argument("--task-id", required=True); a.add_argument("--report", required=True)

    s = sub.add_parser("task", help="task records"); ts = s.add_subparsers(dest="task_cmd", required=True)
    a = ts.add_parser("add")
    a.add_argument("--task-id", required=True); a.add_argument("--card", type=int); a.add_argument("--workstream", required=True)
    a.add_argument("--provider", required=True); a.add_argument("--title", default="", help="the worker's instruction")
    a.add_argument("--brief", type=_brief, help=f"a short title, at most {BRIEF_MAX_CHARS} characters: the PR title and the "
                                                "commit subject of the task's PR (default: the title)")
    a.add_argument("--handoff-ref")
    a.add_argument("--decision-ref", action="append", default=[]); a.add_argument("--parent"); a.add_argument("--priority", type=int, default=100)
    a.add_argument("--owner", default="claude"); a.add_argument("--base-sha")
    ts.add_parser("list")
    a = ts.add_parser("show"); a.add_argument("task_id")
    a = ts.add_parser("set-state"); a.add_argument("task_id"); a.add_argument("new_state", metavar="state"); a.add_argument("--reason"); a.add_argument("--blocker")
    a = ts.add_parser("transfer", help="move a task to another provider (bounded by limits.provider_transfers_per_pr); "
                                       "its PR, attempts and budget pool are kept; a running writer is stopped first")
    a.add_argument("task_id"); a.add_argument("--to", required=True, dest="to_provider"); a.add_argument("--reason")
    a.add_argument("--adapter", choices=ADAPTER_CHOICES, action="append", default=None,
                   help="the adapter of the provider whose running writer must be stopped")
    a.add_argument("--gh", choices=["none", "cli"], default="none",
                   help="cli: a previous writer that has already completed is committed, pushed and proposed before "
                        "the move, as `tick --gh cli` would. none (default): such a transfer is refused "
                        "(transfer-needs-pr-lifecycle) and nothing changes")
    a.add_argument("--workspaces", default=None, help="where worker clones live (default <state dir>/workspaces)")

    s = sub.add_parser("event", help="ingest an external event once"); es = s.add_subparsers(dest="event_cmd", required=True)
    a = es.add_parser("ingest"); a.add_argument("--event-id", required=True); a.add_argument("--source", default="operator")
    a.add_argument("--kind", required=True); a.add_argument("--task-id"); a.add_argument("--payload", default="{}")
    a.add_argument("--gh", choices=["none", "cli"], default="none",
                   help="cli: a NEW PR event also triggers one observation of the task's open PR")
    a.add_argument("--adapter", choices=ADAPTER_CHOICES, action="append", default=None); a.add_argument("--workspaces", default=None)

    s = sub.add_parser("run", help="run records"); rs = s.add_subparsers(dest="run_cmd", required=True)
    rs.add_parser("list")
    a = rs.add_parser("settle", help="record an unsettled run's outcome by hand (operator reconciliation; no PR "
                                     "lifecycle, so `completed` is refused for an implementation or repair run)")
    a.add_argument("run_id"); a.add_argument("--stop-reason", required=True); a.add_argument("--consumed", type=float)
    a.add_argument("--failure-identity"); a.add_argument("--result", default="{}")
    a = rs.add_parser("cancel", help="stop a run through its provider's adapter and settle it; the workspace is kept")
    a.add_argument("run_id")
    a.add_argument("--adapter", choices=ADAPTER_CHOICES, action="append", default=None,
                   help="the adapter of the run's provider, which stops its process; repeatable. A launching, "
                        "running or unknown run whose provider's adapter is not registered is refused")

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

    # PR 4: operator controls and evidence retention. None of these calls a model.
    s = sub.add_parser("backup", help="copy the ledger (SQLite online backup) to a new file; refuses to overwrite")
    s.add_argument("dest")
    s = sub.add_parser("restore", help="replace the --state file with a backup; refuses an unknown schema and, without "
                                       "--replace, an existing state file")
    s.add_argument("source"); s.add_argument("--replace", action="store_true")
    s = sub.add_parser("evidence", help="per-run evidence archives under <state dir>/evidence"); ev = s.add_subparsers(dest="evidence_cmd", required=True)
    a = ev.add_parser("archive", help="tar the run directory (manifest, stdout, stderr, transcript) with the run's ledger rows")
    a.add_argument("run_id"); a.add_argument("--out", default=None, help="archive directory (default <state dir>/evidence)")
    a = ev.add_parser("list"); a.add_argument("--out", default=None)
    sub.add_parser("metrics", help="counts and sums from the ledger: runs by stop reason, PRs by outcome, usage by kind")
    sub.add_parser("report", help="the LLM-free status report (Markdown) from the ledger: status plus metrics")
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


def adapter_names(args) -> List[str]:
    """The adapters a command registers: fake always, plus every `--adapter` named, in order, once each."""
    names: List[str] = ["fake"]
    for n in (getattr(args, "adapter", None) or []):
        if n not in names:
            names.append(n)
    return names


def build_adapters(names: List[str], runs_dir: str) -> Dict[str, Any]:
    """One adapter per name. The live adapters are imported here, not at module level, so the fake adapter and
    every record command work without them being importable; constructing one runs nothing."""
    adapters: Dict[str, Any] = {}
    for n in names:
        if n == "fake":
            adapters[n] = FakeAdapter()
        elif n == "claude":
            from .adapters.claude import ClaudeAdapter
            adapters[n] = ClaudeAdapter(runs_dir=runs_dir)
        elif n == "codex":
            from .adapters.codex import CodexAdapter
            adapters[n] = CodexAdapter(runs_dir)
        elif n == "cursor":
            from .adapters.cursor import CursorAdapter
            adapters[n] = CursorAdapter(runs_dir)
        else:
            raise ControllerError(f"unknown adapter {n}; one of {ADAPTER_CHOICES}")
    return adapters


def _controller(args, store: Store, policy: Policy) -> Controller:
    holder = args.holder or f"{socket.gethostname()}:{os.getpid()}"
    runs_dir = os.path.join(os.path.dirname(os.path.abspath(args.state)), "runs")
    adapters = build_adapters(adapter_names(args), runs_dir)
    gh = None
    if getattr(args, "gh", "none") == "cli":
        from .prs import GhCli
        gh = GhCli()
    return Controller(store, policy, adapters, holder_id=holder, gh=gh, workspaces_dir=getattr(args, "workspaces", None))


class _GateFailure:
    def __init__(self, gate: str, identity: str, evidence: str):
        self.gate, self.identity, self.evidence = gate, identity, evidence


class _GateReport:
    """A saved gate report read back from JSON, in the shape `Controller.evaluate_readiness` reads
    (the same fields as readiness.GateReport)."""

    def __init__(self, raw: Dict[str, Any]):
        self.source = raw.get("source")
        self.commit = raw.get("commit")
        self.passed = list(raw.get("passed") or [])
        self.raw = raw.get("raw") or {}
        self.failures = [_GateFailure(str(f["gate"]), str(f["identity"]), str(f.get("evidence", "")))
                         for f in (raw.get("failures") or [])]


def _report_to_json(report) -> Dict[str, Any]:
    return {"source": getattr(report, "source", None), "commit": getattr(report, "commit", None),
            "failures": [{"gate": f.gate, "identity": f.identity, "evidence": f.evidence} for f in report.failures],
            "passed": list(report.passed), "raw": getattr(report, "raw", {})}


def _utf8_when_redirected() -> None:
    """A redirected stdout or stderr (a file or a pipe, not a terminal) is written as UTF-8. Python otherwise
    encodes it in the locale's legacy code page (cp1252 on this Windows host), and one character outside it in
    a blocker (model or CLI text: an arrow, a check mark, U+FFFD) made `report > file` fail with
    UnicodeEncodeError and leave an empty file. A terminal keeps the console's own encoding. A lone surrogate,
    which no encoding can write, is escaped rather than raised."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)   # TextIOWrapper, Python 3.7+
        if reconfigure is None:
            continue
        try:
            if stream.isatty():
                continue
            reconfigure(encoding="utf-8", errors="backslashreplace")
        except (OSError, ValueError):
            continue


def main(argv: Optional[List[str]] = None) -> int:
    _utf8_when_redirected()
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

    if args.cmd == "restore":
        # Before the state is opened: a restore replaces the file, and `_open` would refuse a missing one.
        from . import evidence
        if not args.state:
            raise ControllerError("--state (or ASHLAR_CONTROLLER_STATE) is required")
        _print(evidence.restore(args.source, args.state, replace=args.replace), args.json)
        return 0

    read_only = args.cmd == "tick" and args.dry_run
    store = _open(args, read_only=read_only)

    if args.cmd == "backup":
        from . import evidence
        _print(evidence.backup(store, args.dest), args.json); return 0
    if args.cmd == "evidence":
        from . import evidence
        state_dir = os.path.dirname(os.path.abspath(args.state))
        out_dir = args.out or os.path.join(state_dir, "evidence")
        if args.evidence_cmd == "archive":
            _print(evidence.archive_run(store, args.run_id, out_dir, os.path.join(state_dir, "runs")), args.json); return 0
        _print(evidence.list_archives(out_dir), args.json); return 0
    if args.cmd == "metrics":
        from . import metrics
        _print(metrics.collect(store), args.json); return 0

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
    if args.cmd == "report":
        from . import metrics
        s = ctl.status(); m = metrics.collect(store)
        text = metrics.render_report(s, m)
        _print({"status": s, "metrics": m, "markdown": text} if args.json else text, args.json); return 0
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
    if args.cmd == "readiness":
        if args.readiness_cmd == "run":
            from . import readiness   # the gate runner and parsers; imported only on this path
            try:
                report = readiness.run_gate(args.gate, args.workspace)
            except readiness.GateInfrastructureError as e:
                raise ControllerError(f"gate {args.gate} could not run (infrastructure, not a verdict): {e}")
            out: Dict[str, Any] = {"report": _report_to_json(report)}
            if args.task_id:
                out["evaluation"] = ctl.evaluate_readiness(args.task_id, report)
            _print(out, args.json); return 0
        if args.readiness_cmd == "evaluate":
            with open(args.report, encoding="utf-8") as f:
                raw = json.load(f)
            report = _GateReport(raw.get("report", raw))
            _print(ctl.evaluate_readiness(args.task_id, report), args.json); return 0
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
                                      title=args.title, brief=args.brief, handoff_ref=args.handoff_ref, decision_refs=args.decision_ref,
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
            # dest is new_state: a positional named "state" would overwrite the global --state path.
            if args.new_state not in allowed:
                raise ControllerError(f"unknown task state {args.new_state}; one of {sorted(allowed)}")
            ctl.set_task_state(args.task_id, args.new_state, stop_reason=args.reason, blocker=args.blocker)
            _print({"task_id": args.task_id, "state": args.new_state}, args.json); return 0
        if args.task_cmd == "transfer":
            _print(ctl.transfer_task(args.task_id, args.to_provider, reason=args.reason), args.json); return 0
    if args.cmd == "event":
        out = ctl.handle_event(args.event_id, args.source, args.kind, args.task_id, json.loads(args.payload))
        _print(out, args.json); return 0
    if args.cmd == "pr":
        if args.pr_cmd == "list":
            _print([dict(r) for r in store.rows("SELECT pr_number, task_id, state, outcome, branch, base_sha, head_sha, merge_sha, "
                                                  "repair_rounds_used, url FROM prs ORDER BY created_at, pr_number")], args.json); return 0
        row = store.row("SELECT * FROM prs WHERE pr_number=?", (args.number,))
        if row is None:
            raise ControllerError(f"no PR record {args.number}")
        if args.pr_cmd == "show":
            _print(dict(row), args.json); return 0
        if args.pr_cmd == "observe":
            if ctl.lifecycle is None:
                raise ControllerError("pr observe needs --gh cli")
            if row["state"] != "open":
                raise ControllerError(f"PR {args.number} is {row['state']} ({row['outcome']}); nothing to observe")
            task = store.row("SELECT * FROM tasks WHERE task_id=?", (row["task_id"],))
            acted = ctl.lifecycle.observe(row, task, launch_allowed=ctl.lifecycle.task_may_launch(task))
            _print(acted or {"action": "waiting", "pr_number": args.number}, args.json); return 0
    if args.cmd == "run":
        if args.run_cmd == "list":
            _print([dict(r) for r in store.rows("SELECT run_id, task_id, attempt_no, provider, model, state, reservation, consumed, "
                                                  "stop_reason, native_session_id, native_run_handle, workspace FROM runs ORDER BY reserved_at")], args.json); return 0
        if args.run_cmd == "settle":
            run = store.row("SELECT * FROM runs WHERE run_id=?", (args.run_id,))
            if run is None:
                raise ControllerError(f"no run {args.run_id}")
            if run["settled_at"] is not None:
                charged = f", charged {run['consumed']:g}" if run["consumed"] is not None else ""
                raise ControllerError(f"run {args.run_id} is already settled ({run['state']}, stop reason "
                                      f"{run['stop_reason']}{charged}); nothing was recorded. A settled run never moves "
                                      f"its task again: to move task {run['task_id']}, use `task set-state "
                                      f"{run['task_id']} <state> --reason <text>`")
            if args.stop_reason == StopReason.COMPLETED and run["role"] in WRITER_ROLES:
                raise ControllerError(f"`run settle --stop-reason completed` of {run['role']} run {args.run_id} is refused: "
                                      f"`run settle` has no PR lifecycle, so task {run['task_id']} would be marked done "
                                      f"with nothing committed, pushed or proposed. Tick with `--adapter "
                                      f"{run['provider']} --gh cli` instead: it polls the run and proposes it")
            state = RunState.FINISHED if args.stop_reason == StopReason.COMPLETED else (
                RunState.CANCELLED if args.stop_reason == StopReason.CANCELLED else RunState.FAILED)
            out = ctl._apply_outcome(run, RunStatus(state=state, stop_reason=args.stop_reason, consumed_units=args.consumed,
                                                     failure_identity=args.failure_identity, result=json.loads(args.result)))
            _print(out, args.json); return 0
        if args.run_cmd == "cancel":
            _print(ctl.cancel_run(args.run_id), args.json); return 0
    raise ControllerError(f"unhandled command {args.cmd}")
