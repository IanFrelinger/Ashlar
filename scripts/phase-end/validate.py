#!/usr/bin/env python3
"""Phase-end validation of the whole project: deterministic facts, read in a minute or two.

Run by `/handoff` at the end of every phase, before the handoff is written (.claude/commands/handoff.md),
and again by `/start-phase` to see what changed since the handoff. Its JSON is also the input the
`project-audit` workflow (.claude/workflows/project-audit.js) reads as established facts.

    python scripts/phase-end/validate.py [--repo OWNER/NAME] [--workstream W] [--phase P]
        [--json OUT.json] [--markdown OUT.md] [--no-fetch] [--skip-local] [--with-repo-gates]
        [--stale-days N]

Each check reports one status:
  pass   the check ran and found nothing wrong
  warn   the check ran and found something the handoff must name (not a merge blocker on its own)
  fail   the check ran and found something broken: a required check red on master's head, a missing
         referenced path, a stale knowledge graph, a red repo gate
  error  the check could not run (no `gh`, no network, no working bash, unexpected output). Never a pass.
  info   a fact with no judgement attached

Exit: 0 when no check is `fail` or `error`; 1 otherwise; 2 on a usage error. A non-zero exit is a finding
for the handoff, not a reason to skip the audit. Needs git, bash and an authenticated `gh`; never runs dotnet.
On Windows, `bash` on PATH may be WSL's launcher, which cannot run the repo's scripts; Git Bash is found
next to git, or named with ASHLAR_BASH.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

SCHEMA_VERSION = 1
DEFAULT_REPO = "IanFrelinger/Ashlar"
READINESS_WORKFLOW = "full-platform-readiness-gate.yml"
VERDICT_PREFIX = "readiness-verdict-for-sha:"
STATUSES = ("pass", "warn", "fail", "error", "info")
GREEN_CONCLUSIONS = ("success", "skipped", "neutral")
RED_CONCLUSIONS = ("failure", "cancelled", "timed_out", "action_required", "startup_failure", "stale")
STRANDED_LIST_LIMIT = 25

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

RunResult = Tuple[int, str, str]
Runner = Callable[..., RunResult]   # runner(argv, input=None) -> (exit code, stdout, stderr)


def default_runner(argv: Sequence[str], input: Optional[str] = None, timeout: int = 900) -> RunResult:
    """Run a command from the repository root. A missing executable or a timeout comes back as exit 127
    or 124 with the reason on stderr, never raised: a check that cannot run must say so."""
    try:
        p = subprocess.run(list(argv), cwd=ROOT, capture_output=True, timeout=timeout, input=input,
                           encoding="utf-8", errors="replace",
                           stdin=None if input is not None else subprocess.DEVNULL)
        return p.returncode, p.stdout, p.stderr
    except FileNotFoundError as e:
        return 127, "", f"not found: {e}"
    except subprocess.TimeoutExpired:
        return 124, "", f"timed out after {timeout}s: {' '.join(argv)}"


def find_bash(env: Optional[Dict[str, str]] = None, which: Callable[[str], Optional[str]] = shutil.which,
              isfile: Callable[[str], bool] = os.path.isfile, windows: Optional[bool] = None) -> str:
    """The bash to run the repo's scripts with. ASHLAR_BASH wins. Off Windows, `bash`. On Windows the
    System32 `bash.exe` is WSL's launcher (observed 2026-10-12: 'execvpe(/bin/bash) failed'), so Git Bash
    is preferred: the `bin\\bash.exe` of the Git install that `git` on PATH belongs to."""
    env = os.environ if env is None else env
    if env.get("ASHLAR_BASH"):
        return env["ASHLAR_BASH"]
    windows = (os.name == "nt") if windows is None else windows
    if not windows:
        return "bash"
    git = which("git")
    if git:
        root = os.path.dirname(os.path.dirname(os.path.abspath(git)))   # <Git>\cmd\git.exe -> <Git>
        for cand in (os.path.join(root, "bin", "bash.exe"), os.path.join(root, "usr", "bin", "bash.exe")):
            if isfile(cand):
                return cand
    found = which("bash")
    if found and "system32" not in found.lower():
        return found
    return "bash"


def check(cid: str, title: str, status: str, detail: str, evidence: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    if status not in STATUSES:
        raise ValueError(f"unknown status {status}")
    return {"id": cid, "title": title, "status": status, "detail": detail, "evidence": evidence or {}}


def normalize_subject(subject: str) -> str:
    """A commit subject without the ' (#123)' suffixes a squash merge appends, case- and space-folded."""
    s = subject.strip()
    while True:
        t = re.sub(r"\s*\(#\d+\)\s*$", "", s)
        if t == s:
            break
        s = t
    return re.sub(r"\s+", " ", s).lower()


def _iso_epoch(iso: str) -> float:
    return time.mktime(time.strptime(iso[:19], "%Y-%m-%dT%H:%M:%S")) - time.timezone


class Validator:
    def __init__(self, runner: Runner, repo: str, python: str, bash: str, now: float, stale_days: int = 3):
        self.run = runner
        self.repo = repo
        self.python = python
        self.bash = bash
        self.now = now
        self.stale_days = stale_days
        self.master_sha: Optional[str] = None
        self.bash_ok: Optional[bool] = None

    # ----- helpers ---------------------------------------------------------------------------------

    def gh_json(self, args: Sequence[str]) -> Tuple[Optional[Any], Optional[str]]:
        rc, out, err = self.run(["gh"] + list(args))
        if rc != 0:
            return None, (err.strip() or out.strip() or f"gh exited {rc}")[:300]
        try:
            return (json.loads(out) if out.strip() else None), None
        except ValueError as e:
            return None, f"gh printed something that is not JSON: {e}"

    def probe_bash(self) -> bool:
        if self.bash_ok is None:
            rc, out, _ = self.run([self.bash, "-c", "echo bash-ok"])
            self.bash_ok = rc == 0 and out.strip() == "bash-ok"
        return self.bash_ok

    # ----- git -------------------------------------------------------------------------------------

    def master_head(self, fetch: bool) -> Dict[str, Any]:
        title = "Master's head"
        if fetch:
            rc, _, err = self.run(["git", "fetch", "-q", "origin"])
            if rc != 0:
                return check("master-head", title, "error", f"git fetch origin failed: {err.strip()[:300]}")
        rc, out, err = self.run(["git", "rev-parse", "origin/master"])
        sha = out.strip()
        if rc != 0 or not re.fullmatch(r"[0-9a-f]{40}", sha):
            return check("master-head", title, "error", f"git rev-parse origin/master failed: {err.strip()[:300]}")
        self.master_sha = sha
        return check("master-head", title, "info", f"origin/master is {sha[:12]}", {"sha": sha})

    def unreleased(self) -> Dict[str, Any]:
        title = "Commits since the last release tag"
        rc, out, err = self.run(["git", "describe", "--tags", "--abbrev=0", "--match", "v*", "origin/master"])
        tag = out.strip()
        if rc != 0 or not tag:
            return check("unreleased", title, "error", f"no v* tag reachable from master: {err.strip()[:200]}")
        rc, out, err = self.run(["git", "rev-list", "--count", f"{tag}..origin/master"])
        if rc != 0 or not out.strip().isdigit():
            return check("unreleased", title, "error", (err or out).strip()[:200])
        n = int(out.strip())
        version = None
        try:
            with open(os.path.join(ROOT, "VERSION"), encoding="utf-8") as f:
                version = f.read().strip()
        except OSError:
            pass
        return check("unreleased", title, "info", f"{n} commit(s) on master since {tag}; VERSION is {version or 'unreadable'}",
                     {"tag": tag, "commits": n, "version": version})

    def _landed(self, branch: str, master_patch_ids: set, master_subjects: set) -> Optional[bool]:
        """True when the branch's work is safe elsewhere: a remote-tracking branch contains its tip, every file it
        changed since its merge base is identical on master, its whole diff has the patch id of a master commit
        (a squash merge), or its tip's subject is a master commit's subject (a squash merge whose combined diff
        differs). None when git could not tell."""
        rc, remotes, _ = self.run(["git", "branch", "-r", "--contains", branch, "--format=%(refname:short)"])
        if rc == 0 and remotes.strip():
            return True
        rc, subject, _ = self.run(["git", "log", "-1", "--format=%s", branch])
        if rc == 0 and normalize_subject(subject) in master_subjects:
            return True
        rc, base, _ = self.run(["git", "merge-base", "origin/master", branch])
        if rc != 0 or not base.strip():
            return None
        base = base.strip()
        rc, files, _ = self.run(["git", "diff", "--name-only", base, branch])
        if rc != 0:
            return None
        files = [f for f in files.splitlines() if f.strip()]
        if not files:
            return True
        rc, _, _ = self.run(["git", "diff", "--quiet", "origin/master", branch, "--"] + files)
        if rc == 0:
            return True
        rc, diff, _ = self.run(["git", "diff", base, branch])
        if rc == 0 and diff.strip():
            rc, pid, _ = self.run(["git", "patch-id", "--stable"], input=diff)
            if rc == 0 and pid.split()[:1] and pid.split()[0] in master_patch_ids:
                return True
        return False

    def stranded_branches(self) -> Dict[str, Any]:
        title = "Local work on no remote"
        rc, out, err = self.run(["git", "for-each-ref", "--format=%(refname:short)\t%(upstream:short)\t%(upstream:track)",
                                 "refs/heads"])
        if rc != 0:
            return check("stranded-branches", title, "error", err.strip()[:200])
        candidates = []
        for line in out.splitlines():
            if not line.strip():
                continue
            name, upstream, track = (line.split("\t") + ["", ""])[:3]
            if name.strip() and (not upstream.strip() or "gone" in track):
                candidates.append(name.strip())
        if not candidates:
            return check("stranded-branches", title, "pass", "every local branch tracks a remote branch that exists")
        rc, out, _ = self.run(["git", "branch", "--format=%(refname:short)", "--no-merged", "origin/master"])
        if rc != 0:
            return check("stranded-branches", title, "error", "git branch --no-merged failed")
        unmerged = {b.strip() for b in out.splitlines() if b.strip()}
        to_test = [b for b in sorted(set(candidates)) if b in unmerged]
        master_ids: set = set()
        master_subjects: set = set()
        if to_test:
            rc, log, _ = self.run(["git", "log", "-p", "--no-merges", "--since=180.days", "origin/master"])
            if rc == 0 and log.strip():
                rc, pids, _ = self.run(["git", "patch-id", "--stable"], input=log)
                if rc == 0:
                    master_ids = {l.split()[0] for l in pids.splitlines() if l.split()}
            rc, subjects, _ = self.run(["git", "log", "--format=%s", "--since=180.days", "origin/master"])
            if rc == 0:
                master_subjects = {normalize_subject(s) for s in subjects.splitlines() if s.strip()}
        stranded, unknown = [], []
        for b in to_test:
            landed = self._landed(b, master_ids, master_subjects)
            if landed is None:
                unknown.append(b)
            elif not landed:
                rc, ahead, _ = self.run(["git", "rev-list", "--count", f"origin/master..{b}"])
                rc2, date, _ = self.run(["git", "log", "-1", "--format=%cs", b])
                stranded.append({"branch": b, "ahead": int(ahead.strip()) if rc == 0 and ahead.strip().isdigit() else None,
                                 "tip_date": date.strip() if rc2 == 0 else None})
        ev = {"candidates": len(set(candidates)), "stranded": stranded[:STRANDED_LIST_LIMIT],
              "stranded_total": len(stranded), "undetermined": unknown}
        if stranded:
            names = ", ".join(s["branch"] for s in stranded[:8]) + (" ..." if len(stranded) > 8 else "")
            return check("stranded-branches", title, "warn",
                         f"{len(stranded)} local branch(es) hold work that is on no remote and not on master: {names}", ev)
        if unknown:
            return check("stranded-branches", title, "error", f"could not compare {len(unknown)} branch(es) with master", ev)
        return check("stranded-branches", title, "pass",
                     f"{len(set(candidates))} local branch(es) without a live upstream; master already holds all their work", ev)

    # ----- GitHub ----------------------------------------------------------------------------------

    def required_checks(self) -> Dict[str, Any]:
        title = "Required checks on master's head"
        if not self.master_sha:
            return check("required-checks", title, "error", "master's head is unknown")
        prot, err = self.gh_json(["api", f"repos/{self.repo}/branches/master/protection"])
        if err:
            return check("required-checks", title, "error", f"branch protection unreadable: {err}")
        required = ((prot or {}).get("required_status_checks") or {}).get("contexts") or []
        if not required:
            return check("required-checks", title, "error", "branch protection names no required checks")
        # One JSON object per line (--jq), so --paginate cannot split a document across pages.
        rc, out, e2 = self.run(["gh", "api", "--paginate", f"repos/{self.repo}/commits/{self.master_sha}/check-runs",
                                "--jq", ".check_runs[] | {name, status, conclusion}"])
        if rc != 0:
            return check("required-checks", title, "error", f"check runs unreadable: {(e2 or out).strip()[:300]}")
        try:
            runs = [json.loads(l) for l in out.splitlines() if l.strip()]
        except ValueError as e:
            return check("required-checks", title, "error", f"check runs are not JSON lines: {e}")
        latest: Dict[str, Dict[str, Any]] = {}
        for r in runs:
            latest.setdefault(r.get("name"), r)   # the API lists the newest run of a name first
        red, pending, green, absent = [], [], [], []
        for name in required:
            r = latest.get(name)
            if r is None:
                absent.append(name)
            elif r.get("status") != "completed":
                pending.append(name)
            elif r.get("conclusion") in GREEN_CONCLUSIONS:
                green.append(name)
            else:
                red.append(f"{name} ({r.get('conclusion')})")
        ev = {"required": required, "green": green, "red": red, "pending": pending, "not_reported": absent}
        if red:
            return check("required-checks", title, "fail", f"red on {self.master_sha[:12]}: {', '.join(red)}", ev)
        parts = [f"{len(green)} of {len(required)} green on {self.master_sha[:12]}"]
        if pending:
            parts.append(f"pending: {', '.join(pending)}")
        if absent:
            parts.append(f"not reported on the push (PR-only or path-filtered): {', '.join(absent)}")
        return check("required-checks", title, "warn" if pending else "pass", "; ".join(parts), ev)

    def master_workflows(self) -> Dict[str, Any]:
        """The newest master run of EVERY workflow, asked per workflow. A path-filtered or dispatch-only gate can
        stay red for weeks behind hundreds of green runs of other workflows; a single run list misses it."""
        title = "Newest master run of every workflow"
        wfs, err = self.gh_json(["workflow", "list", "--all", "--limit", "500", "--json", "id,name,path,state"])
        if err:
            return check("master-workflows", title, "error", err)
        # GitHub keeps listing a workflow after its file is deleted, and lists Dependabot's built-in workflow
        # (dynamic/...). Only workflows whose file is on master count; a deleted one's last red run is history.
        rc, out, e = self.run(["git", "ls-tree", "--name-only", "origin/master", ".github/workflows/"])
        if rc != 0:
            return check("master-workflows", title, "error", f"git ls-tree failed: {(e or out).strip()[:200]}")
        on_master = {line.strip() for line in out.splitlines() if line.strip()}
        gone = sorted(f"{w.get('name')} ({w.get('path')})" for w in (wfs or []) if w.get("path") not in on_master)
        wfs = [w for w in (wfs or []) if w.get("path") in on_master]
        red, never, disabled, errors = [], [], [], []
        for wf in sorted(wfs, key=lambda w: w.get("name") or ""):
            if wf.get("state") and wf.get("state") != "active":
                disabled.append(wf.get("name"))
            runs, e = self.gh_json(["run", "list", "--workflow", str(wf.get("id")), "--branch", "master", "--limit", "1",
                                    "--json", "conclusion,status,createdAt,headSha,event,databaseId"])
            if e:
                errors.append(f"{wf.get('name')}: {e}")
                continue
            if not runs:
                never.append(wf.get("name"))
                continue
            r = runs[0]
            if r.get("status") == "completed" and r.get("conclusion") in RED_CONCLUSIONS:
                red.append({"workflow": wf.get("name"), "path": wf.get("path"), "conclusion": r.get("conclusion"),
                            "created": (r.get("createdAt") or "")[:10], "event": r.get("event"), "run": r.get("databaseId")})
        ev = {"workflows": len(wfs), "red": red, "never_on_master": never, "not_active": disabled, "errors": errors,
              "listed_but_not_on_master": gone}
        if errors:
            return check("master-workflows", title, "error", f"{len(errors)} workflow(s) unreadable: {errors[0]}", ev)
        if red:
            names = ", ".join(f"{x['workflow']} ({x['conclusion']}, {x['created']})" for x in red)
            return check("master-workflows", title, "warn",
                         f"{len(red)} of the {len(wfs)} workflow(s) on master end red there: {names}", ev)
        return check("master-workflows", title, "pass",
                     f"the newest master run of every workflow that has one is green ({len(wfs)} workflows on master, "
                     f"{len(never)} never run there)", ev)

    def readiness_verdict(self, sha: str) -> Tuple[str, str]:
        """('verified' | 'refused' | 'undetermined' | 'error', the script's verdict line)."""
        if not self.probe_bash():
            return "error", f"no working bash ({self.bash}); set ASHLAR_BASH"
        rc, out, err = self.run([self.bash, "scripts/release/readiness-verdict-for-sha.sh", sha])
        lines = [l.strip() for l in (out + "\n" + err).splitlines() if l.strip().startswith(VERDICT_PREFIX)]
        if not lines:
            tail = (err or out).strip().splitlines()[-1:] or [f"exit {rc}"]
            return "error", f"no verdict line (exit {rc}): {tail[0][:200]}"
        line = lines[-1]
        if rc == 0 and " VERIFIED" in line:
            return "verified", line
        if rc == 1:
            return "refused", line
        if rc == 2:
            return "undetermined", line
        return "error", f"unexpected exit {rc}: {line}"

    def readiness_head(self) -> Dict[str, Any]:
        title = "Readiness verdict for master's head"
        if not self.master_sha:
            return check("readiness-head", title, "error", "master's head is unknown")
        kind, line = self.readiness_verdict(self.master_sha)
        status = {"verified": "pass", "refused": "warn", "undetermined": "warn"}.get(kind, "error")
        return check("readiness-head", title, status, line, {"verdict": kind})

    def readiness_last_verified(self) -> Dict[str, Any]:
        title = "Last verified readiness commit"
        runs, err = self.gh_json(["run", "list", "--workflow", READINESS_WORKFLOW, "--branch", "master", "--limit", "20",
                                  "--json", "headSha,conclusion,event,createdAt"])
        if err:
            return check("readiness-last-verified", title, "error", err)
        seen: List[str] = []
        for r in runs or []:
            sha = r.get("headSha")
            if not sha or sha in seen or r.get("conclusion") != "success":
                continue
            seen.append(sha)
            kind, line = self.readiness_verdict(sha)
            if kind == "error":
                return check("readiness-last-verified", title, "error", line)
            if kind == "verified":
                rc, out, _ = self.run(["git", "rev-list", "--count", f"{sha}..origin/master"])
                behind = int(out.strip()) if rc == 0 and out.strip().isdigit() else None
                return check("readiness-last-verified", title, "info",
                             f"{sha[:12]} is VERIFIED ({r.get('event')} run, {(r.get('createdAt') or '')[:10]}); "
                             f"{behind if behind is not None else '?'} commit(s) on master since",
                             {"sha": sha, "commits_since": behind})
        return check("readiness-last-verified", title, "warn",
                     f"none of the last {len(seen)} successful readiness run(s) on master reads VERIFIED", {"checked": seen})

    def open_prs(self) -> Dict[str, Any]:
        title = "Open pull requests"
        prs, err = self.gh_json(["pr", "list", "--state", "open", "--limit", "200",
                                 "--json", "number,title,mergeStateStatus,updatedAt,isDraft,headRefName"])
        if err:
            return check("open-prs", title, "error", err)
        prs = prs or []
        by_state: Dict[str, int] = {}
        stale = []
        for p in prs:
            st = p.get("mergeStateStatus") or "UNKNOWN"
            by_state[st] = by_state.get(st, 0) + 1
            if p.get("updatedAt") and (self.now - _iso_epoch(p["updatedAt"])) / 86400.0 > self.stale_days:
                stale.append(p.get("number"))
        ev = {"total": len(prs), "by_merge_state": by_state, "stale": sorted(stale), "stale_days": self.stale_days}
        if not prs:
            return check("open-prs", title, "pass", "no open pull requests", ev)
        detail = f"{len(prs)} open ({', '.join(f'{k} {v}' for k, v in sorted(by_state.items()))})"
        if stale:
            detail += (f"; {len(stale)} not updated for more than {self.stale_days} day(s): "
                       + ", ".join(f"#{n}" for n in sorted(stale)))
            return check("open-prs", title, "warn", detail, ev)
        return check("open-prs", title, "info", detail, ev)

    def open_issues(self) -> Dict[str, Any]:
        title = "Open issues"
        counts: Dict[str, int] = {}
        for label in ("bug", "agent-task", None):
            args = ["issue", "list", "--state", "open", "--limit", "1000", "--json", "number"]
            if label:
                args += ["--label", label]
            data, err = self.gh_json(args)
            if err:
                return check("open-issues", title, "error", err)
            counts[label or "all"] = len(data or [])
        return check("open-issues", title, "info",
                     f"{counts['all']} open: {counts['bug']} labelled bug, {counts['agent-task']} agent-task card(s)", counts)

    def repo_security(self) -> Dict[str, Any]:
        title = "Repository security settings"
        data, err = self.gh_json(["api", f"repos/{self.repo}"])
        if err:
            return check("repo-security", title, "error", err)
        visibility = (data or {}).get("visibility")
        sa = (data or {}).get("security_and_analysis") or {}
        off = sorted(k for k, v in sa.items() if isinstance(v, dict) and v.get("status") == "disabled")
        ev = {"visibility": visibility, "disabled": off}
        if visibility == "public" and ("secret_scanning" in off or "secret_scanning_push_protection" in off):
            return check("repo-security", title, "warn", f"public repository with {', '.join(off)} disabled", ev)
        return check("repo-security", title, "info", f"{visibility}; disabled: {', '.join(off) or 'none'}", ev)

    # ----- local checks ------------------------------------------------------------------------------

    def local(self, cid: str, title: str, argv: Sequence[str], needs_bash: bool = False) -> Dict[str, Any]:
        if needs_bash and not self.probe_bash():
            return check(cid, title, "error", f"no working bash ({self.bash}); set ASHLAR_BASH")
        rc, out, err = self.run(argv)
        tail = "\n".join((out + err).strip().splitlines()[-6:])
        if rc == 0:
            return check(cid, title, "pass", tail.splitlines()[-1] if tail else "exit 0", {"exit": rc})
        if rc in (124, 127):
            return check(cid, title, "error", tail or f"exit {rc}", {"exit": rc})
        return check(cid, title, "fail", tail or f"exit {rc}", {"exit": rc})

    # ----- everything ------------------------------------------------------------------------------

    def collect(self, fetch: bool = True, skip_local: bool = False, with_repo_gates: bool = False) -> List[Dict[str, Any]]:
        checks = [self.master_head(fetch)]
        checks += [self.required_checks(), self.master_workflows(), self.readiness_head(), self.readiness_last_verified(),
                   self.open_prs(), self.open_issues(), self.repo_security(), self.unreleased(), self.stranded_branches()]
        if not skip_local:
            checks.append(self.local("docs-paths", "Paths referenced in the docs exist",
                                     [self.bash, "scripts/check-docs-phantom-paths.sh"], needs_bash=True))
            checks.append(self.local("knowledge-graph", "The knowledge graph is current",
                                     [self.python, "scripts/knowledge-graph/verify-knowledge-graph-current.py"]))
        if with_repo_gates:
            checks.append(self.local("repo-gates", "Every discovered repo gate passes",
                                     [self.bash, "scripts/ci/run-repo-gates.sh"], needs_bash=True))
        return checks


def summarize(checks: List[Dict[str, Any]]) -> Dict[str, int]:
    out = {s: 0 for s in STATUSES}
    for c in checks:
        out[c["status"]] += 1
    return out


def exit_code(checks: List[Dict[str, Any]]) -> int:
    return 1 if any(c["status"] in ("fail", "error") for c in checks) else 0


def report(checks: List[Dict[str, Any]], repo: str, workstream: Optional[str], phase: Optional[str],
           master_sha: Optional[str], now: float) -> Dict[str, Any]:
    return {"schema_version": SCHEMA_VERSION, "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(now)),
            "repo": repo, "workstream": workstream, "phase": phase, "master_sha": master_sha,
            "summary": summarize(checks), "exit_code": exit_code(checks), "checks": checks}


def render_markdown(rep: Dict[str, Any]) -> str:
    s = rep["summary"]
    head = (f"Phase-end validation on master `{(rep.get('master_sha') or 'unknown')[:12]}`, "
            f"{rep['generated_at']}: {s['pass']} pass, {s['warn']} warn, {s['fail']} fail, {s['error']} error, "
            f"{s['info']} info.")
    rows = ["| Check | Status | Detail |", "|---|---|---|"]
    for c in rep["checks"]:
        detail = c["detail"].replace("|", "\\|").replace("\n", " ")
        rows.append(f"| {c['title']} | {c['status']} | {detail} |")
    return head + "\n\n" + "\n".join(rows) + "\n"


def main(argv: Optional[List[str]] = None, runner: Runner = default_runner, now: Optional[float] = None,
         bash: Optional[str] = None) -> int:
    ap = argparse.ArgumentParser(description="Phase-end validation of the whole project.")
    ap.add_argument("--repo", default=DEFAULT_REPO)
    ap.add_argument("--workstream")
    ap.add_argument("--phase")
    ap.add_argument("--json", dest="json_out")
    ap.add_argument("--markdown", dest="md_out")
    ap.add_argument("--no-fetch", action="store_true", help="do not run git fetch origin first")
    ap.add_argument("--skip-local", action="store_true", help="skip the docs-path and knowledge-graph checks")
    ap.add_argument("--with-repo-gates", action="store_true", help="also run scripts/ci/run-repo-gates.sh (slow)")
    ap.add_argument("--stale-days", type=int, default=3)
    args = ap.parse_args(argv)
    now = time.time() if now is None else now
    v = Validator(runner, args.repo, sys.executable or "python3", bash or find_bash(), now, args.stale_days)
    checks = v.collect(fetch=not args.no_fetch, skip_local=args.skip_local, with_repo_gates=args.with_repo_gates)
    rep = report(checks, args.repo, args.workstream, args.phase, v.master_sha, now)
    md = render_markdown(rep)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8", newline="\n") as f:
            json.dump(rep, f, indent=2, sort_keys=True)
            f.write("\n")
    if args.md_out:
        with open(args.md_out, "w", encoding="utf-8", newline="\n") as f:
            f.write(md)
    # The checks quote other scripts' output (a "✓"); a Windows console's code page cannot always encode it.
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except (ValueError, OSError):
            pass
    sys.stdout.write(md)
    return rep["exit_code"]


if __name__ == "__main__":
    sys.exit(main())
