"""The GitHub side of the PR lifecycle: open or adopt the task's PR, read the required checks for ONE
head commit, decide green / red / pending, build the compact handoff a repair run receives, merge on
green, and record every transition in the `prs` table and the journal.

Checks are observed BY HEAD SHA. `GhClient.check_runs(repo, sha)` reads the check runs of that commit
only (`GET repos/{repo}/commits/{sha}/check-runs`), never the PR's aggregate status, so a green run on
an earlier head can never satisfy a changed head; the merge passes `--match-head-commit <head>` so gh
itself refuses when the head moved between observation and merge. `evaluate_checks` is a pure
function over the runs it is handed; the caller is responsible for fetching them for the right sha.

What was observed versus assumed. The `gh` argv shapes in `GhCli` and the JSON they return (`gh pr
list/view --json` field names, the check-runs API object with `output.title` / `output.summary`,
`gh api repos/R/branches/B --jq .commit.sha`) follow the lane contract and the GitHub REST
documentation; nothing here was exercised against a live `gh` in this build. Those are the ONLY
shapes this module parses: any other output is a `GhError`. `FakeGh` is the in-memory client the
tests and the dry-run path use; it models the two refusals GitHub makes that the controller relies
on (a second OPEN PR for the same head branch, and a merge whose expected head is not the current
head).
"""
from __future__ import annotations

import json
import os
import sqlite3
import subprocess
import tempfile
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Sequence

from . import readiness
from .store import Store, utcnow_iso

REQUIRED_CHECKS_DEFAULT = ("cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary")
GREEN_CONCLUSIONS = ("success", "skipped")
RED_CONCLUSIONS = ("failure", "cancelled", "timed_out", "action_required", "stale", "startup_failure")

PR_JSON_FIELDS = "number,state,headRefOid,baseRefOid,headRefName,baseRefName,mergeCommit,url,title"
CHECK_RUNS_JQ = ".check_runs[] | {name,status,conclusion,details_url,output:{title,summary}}"

HANDOFF_MAX_LINES = 60      # evidence lines per red check carried into a repair handoff
HANDOFF_MAX_CHARS = 4000    # evidence characters per red check
TRUNCATED_MARKER = "[... truncated]"
GENERATED_LINE = "🤖 Generated with [Claude Code](https://claude.com/claude-code)"


# ---------------------------------------------------------------------------------------------------
# Data
# ---------------------------------------------------------------------------------------------------

@dataclass
class PrView:
    """What `gh pr view N --json <PR_JSON_FIELDS>` (or one element of `gh pr list --json`) gives."""
    number: int
    state: str                      # "OPEN" | "MERGED" | "CLOSED"
    head_sha: str
    base_sha: Optional[str]
    head_branch: str
    base_branch: str
    merge_sha: Optional[str]
    url: str
    title: str = ""


@dataclass
class CheckRun:
    """One element of the check-runs API `check_runs[]`, reduced to what the verdict needs."""
    name: str
    status: str                     # "queued" | "in_progress" | "completed"
    conclusion: Optional[str]
    title: str = ""
    summary: str = ""
    details_url: Optional[str] = None


@dataclass
class CheckFailure:
    name: str
    conclusion: str
    identity: str                   # readiness.failure_identity("check:<name>", evidence lines)
    evidence: str
    details_url: Optional[str]


@dataclass
class CheckVerdict:
    head_sha: str
    verdict: str                    # "green" | "red" | "pending"
    green: List[str] = field(default_factory=list)
    red: List[CheckFailure] = field(default_factory=list)
    pending: List[str] = field(default_factory=list)


class GhError(Exception):
    """`gh` failed for a reason other than "not found"; the message is gh's stderr (or what was unparsable)."""


class MergeRefused(Exception):
    """gh refused the merge (head moved, checks not green, permissions); the message is gh's stderr."""


# ---------------------------------------------------------------------------------------------------
# Client protocol and the gh implementation
# ---------------------------------------------------------------------------------------------------

class GhClient:
    """The six calls the controller makes to GitHub. `GhCli` runs them through `gh`; `FakeGh` answers
    from memory. A plain base class rather than typing.Protocol so it reads the same on Python 3.8;
    the fake and the real client are interchangeable by duck typing."""

    def pr_create(self, repo: str, head_branch: str, base_branch: str, title: str, body: str) -> PrView:
        raise NotImplementedError

    def pr_for_branch(self, repo: str, head_branch: str) -> Optional[PrView]:
        raise NotImplementedError

    def pr_view(self, repo: str, number: int) -> PrView:
        raise NotImplementedError

    def check_runs(self, repo: str, sha: str) -> List[CheckRun]:
        raise NotImplementedError

    def pr_merge(self, repo: str, number: int, head_sha: str) -> None:
        raise NotImplementedError

    def branch_head(self, repo: str, branch: str) -> Optional[str]:
        raise NotImplementedError


def _pr_view_from_json(obj: Dict[str, Any]) -> PrView:
    """The `gh pr view/list --json` object. `mergeCommit` is `{"oid": ...}` or null."""
    try:
        merge = obj.get("mergeCommit")
        merge_sha = merge.get("oid") if isinstance(merge, dict) else None
        return PrView(number=int(obj["number"]), state=str(obj["state"]), head_sha=str(obj["headRefOid"]),
                      base_sha=obj.get("baseRefOid"), head_branch=str(obj["headRefName"]),
                      base_branch=str(obj["baseRefName"]), merge_sha=merge_sha, url=str(obj.get("url") or ""),
                      title=str(obj.get("title") or ""))
    except (KeyError, TypeError, ValueError, AttributeError) as e:
        raise GhError(f"unexpected gh pr JSON ({e}): {json.dumps(obj)[:500]}") from e


def _check_run_from_json(obj: Dict[str, Any]) -> CheckRun:
    """One line of the check-runs jq projection: {name,status,conclusion,details_url,output:{title,summary}}."""
    try:
        output = obj.get("output") or {}
        return CheckRun(name=str(obj["name"]), status=str(obj.get("status") or ""), conclusion=obj.get("conclusion"),
                        title=str(output.get("title") or ""), summary=str(output.get("summary") or ""),
                        details_url=obj.get("details_url"))
    except (KeyError, TypeError, AttributeError) as e:
        raise GhError(f"unexpected check-run JSON ({e}): {json.dumps(obj)[:500]}") from e


def _not_found(stderr: str) -> bool:
    s = (stderr or "").lower()
    return "404" in s or "not found" in s or "could not resolve" in s


class GhCli(GhClient):
    """GhClient over the `gh` executable. One subprocess per call, injectable runner, timeouts, never a
    shell. The argv of each method is exactly the shape the lane contract documents; nothing else is
    parsed. `gh` is assumed authenticated: an auth failure surfaces as GhError with gh's stderr.
    `branch_head` returns None only for a not-found answer (404 / "Not Found" / "Could not resolve");
    any other failure is a GhError, so an auth or network fault is never read as "branch absent"."""

    def __init__(self, gh: str = "gh", runner: Callable[..., Any] = subprocess.run, timeout: int = 120):
        self.gh = gh
        self.runner = runner
        self.timeout = timeout

    def _run(self, args: Sequence[str], timeout: Optional[int] = None) -> Any:
        argv = [self.gh, *args]
        try:
            return self.runner(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
                               errors="replace", timeout=timeout or self.timeout)
        except subprocess.TimeoutExpired as e:
            raise GhError(f"timed out after {timeout or self.timeout}s: {' '.join(argv)}") from e
        except OSError as e:
            raise GhError(f"could not run {' '.join(argv)}: {e}") from e

    def _run_ok(self, args: Sequence[str], timeout: Optional[int] = None) -> str:
        completed = self._run(args, timeout)
        if completed.returncode != 0:
            raise GhError((completed.stderr or "").strip() or f"gh exited {completed.returncode}: {' '.join(args)}")
        return completed.stdout or ""

    def pr_create(self, repo: str, head_branch: str, base_branch: str, title: str, body: str) -> PrView:
        fd, body_path = tempfile.mkstemp(prefix="ashlar-pr-body-", suffix=".md")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as fh:
                fh.write(body)
            self._run_ok(["pr", "create", "--repo", repo, "--head", head_branch, "--base", base_branch,
                          "--title", title, "--body-file", body_path])
        finally:
            try:
                os.unlink(body_path)
            except OSError:
                pass
        view = self.pr_for_branch(repo, head_branch)
        if view is None:
            raise GhError(f"gh pr create succeeded but no open PR is listed for head {head_branch!r} in {repo}")
        return view

    def pr_for_branch(self, repo: str, head_branch: str) -> Optional[PrView]:
        out = self._run_ok(["pr", "list", "--repo", repo, "--head", head_branch, "--state", "open",
                            "--json", PR_JSON_FIELDS])
        try:
            items = json.loads(out or "[]")
        except ValueError as e:
            raise GhError(f"gh pr list returned unparsable JSON: {out[:500]}") from e
        if not isinstance(items, list):
            raise GhError(f"gh pr list did not return a list: {out[:500]}")
        if not items:
            return None
        return _pr_view_from_json(items[0])

    def pr_view(self, repo: str, number: int) -> PrView:
        out = self._run_ok(["pr", "view", str(number), "--repo", repo, "--json", PR_JSON_FIELDS])
        try:
            obj = json.loads(out)
        except ValueError as e:
            raise GhError(f"gh pr view returned unparsable JSON: {out[:500]}") from e
        if not isinstance(obj, dict):
            raise GhError(f"gh pr view did not return an object: {out[:500]}")
        return _pr_view_from_json(obj)

    def check_runs(self, repo: str, sha: str) -> List[CheckRun]:
        out = self._run_ok(["api", "--paginate", f"repos/{repo}/commits/{sha}/check-runs", "--jq", CHECK_RUNS_JQ])
        runs: List[CheckRun] = []
        for line in out.replace("\r\n", "\n").split("\n"):
            if not line.strip():
                continue
            try:
                obj = json.loads(line)
            except ValueError as e:
                raise GhError(f"check-runs line is not JSON: {line[:300]}") from e
            if not isinstance(obj, dict):
                raise GhError(f"check-runs line is not an object: {line[:300]}")
            runs.append(_check_run_from_json(obj))
        return runs

    def pr_merge(self, repo: str, number: int, head_sha: str) -> None:
        completed = self._run(["pr", "merge", str(number), "--repo", repo, "--squash", "--match-head-commit", head_sha])
        if completed.returncode != 0:
            raise MergeRefused((completed.stderr or "").strip() or f"gh pr merge exited {completed.returncode}")

    def branch_head(self, repo: str, branch: str) -> Optional[str]:
        completed = self._run(["api", f"repos/{repo}/branches/{branch}", "--jq", ".commit.sha"])
        if completed.returncode != 0:
            if _not_found(completed.stderr):
                return None
            raise GhError((completed.stderr or "").strip() or f"gh api exited {completed.returncode}")
        sha = (completed.stdout or "").strip()
        return sha or None


# ---------------------------------------------------------------------------------------------------
# In-memory client for tests and dry runs
# ---------------------------------------------------------------------------------------------------

class FakeGh(GhClient):
    """GhClient over dictionaries. Counts every call. `prs` maps number -> PrView, `checks` maps a
    commit sha -> its check runs (in order of creation, so the last run of a name is the latest),
    `branches` maps a branch name -> its tip sha.

    Refusals modelled after GitHub: `pr_create` refuses a second OPEN PR for the same head branch
    (GhError); `pr_merge` refuses when `head_sha` is not the PR's current head or the PR is not OPEN
    (MergeRefused), and merges nothing in that case."""

    def __init__(self) -> None:
        self.prs: Dict[int, PrView] = {}
        self.checks: Dict[str, List[CheckRun]] = {}
        self.branches: Dict[str, str] = {}
        self.create_count = 0
        self.for_branch_count = 0
        self.view_count = 0
        self.check_runs_count = 0
        self.merge_count = 0
        self.branch_head_count = 0
        self._next_number = 1

    # -- helpers for tests --------------------------------------------------------------------

    def set_checks(self, sha: str, conclusions: Dict[str, Optional[str]]) -> None:
        """Replace the runs of `sha`: a conclusion string is a completed run with that conclusion;
        None is a run still in progress (status in_progress, no conclusion)."""
        runs: List[CheckRun] = []
        for name, conclusion in conclusions.items():
            if conclusion is None:
                runs.append(CheckRun(name=name, status="in_progress", conclusion=None))
            else:
                runs.append(CheckRun(name=name, status="completed", conclusion=conclusion,
                                     title=f"{name} {conclusion}", summary=""))
        self.checks[sha] = runs

    def set_in_progress(self, sha: str, names: Sequence[str]) -> None:
        """Append an in-progress run for each name (a re-run on the same head, not yet finished)."""
        self.checks.setdefault(sha, []).extend(CheckRun(name=n, status="in_progress", conclusion=None) for n in names)

    def push_head(self, number: int, new_sha: str) -> None:
        pr = self.prs[number]
        pr.head_sha = new_sha
        self.branches[pr.head_branch] = new_sha

    def close(self, number: int) -> None:
        pr = self.prs[number]
        pr.state = "CLOSED"
        pr.merge_sha = None

    def merge_externally(self, number: int, merge_sha: str) -> None:
        pr = self.prs[number]
        pr.state = "MERGED"
        pr.merge_sha = merge_sha

    # -- GhClient -----------------------------------------------------------------------------

    def pr_create(self, repo: str, head_branch: str, base_branch: str, title: str, body: str) -> PrView:
        self.create_count += 1
        for pr in self.prs.values():
            if pr.head_branch == head_branch and pr.state == "OPEN":
                raise GhError(f"a pull request for branch {head_branch!r} into branch {base_branch!r} "
                              f"already exists: {pr.url}")
        number = self._next_number
        self._next_number += 1
        view = PrView(number=number, state="OPEN", head_sha=self.branches.get(head_branch, f"head-{number}"),
                      base_sha=self.branches.get(base_branch), head_branch=head_branch, base_branch=base_branch,
                      merge_sha=None, url=f"https://github.com/{repo}/pull/{number}", title=title)
        self.prs[number] = view
        return view

    def pr_for_branch(self, repo: str, head_branch: str) -> Optional[PrView]:
        self.for_branch_count += 1
        for pr in self.prs.values():
            if pr.head_branch == head_branch and pr.state == "OPEN":
                return pr
        return None

    def pr_view(self, repo: str, number: int) -> PrView:
        self.view_count += 1
        try:
            return self.prs[number]
        except KeyError:
            raise GhError(f"Could not resolve to a PullRequest with the number of {number}.")

    def check_runs(self, repo: str, sha: str) -> List[CheckRun]:
        self.check_runs_count += 1
        return list(self.checks.get(sha, []))

    def pr_merge(self, repo: str, number: int, head_sha: str) -> None:
        self.merge_count += 1
        pr = self.prs.get(number)
        if pr is None:
            raise MergeRefused(f"Could not resolve to a PullRequest with the number of {number}.")
        if pr.state != "OPEN":
            raise MergeRefused(f"Pull request #{number} is not open (state {pr.state})")
        if pr.head_sha != head_sha:
            raise MergeRefused(f"Head branch was modified. Review and try the merge again. "
                               f"(expected {head_sha}, head is {pr.head_sha})")
        pr.state = "MERGED"
        pr.merge_sha = f"merge-{number}-{head_sha[:7]}"

    def branch_head(self, repo: str, branch: str) -> Optional[str]:
        self.branch_head_count += 1
        return self.branches.get(branch)


# ---------------------------------------------------------------------------------------------------
# Verdict over one head's check runs
# ---------------------------------------------------------------------------------------------------

def check_failure(run: CheckRun) -> CheckFailure:
    """The failure identity of a red run: `readiness.failure_identity` over the name and conclusion,
    the output title and the summary lines, so the same failure on two heads has the same identity
    and a different summary (another failing set) a different one."""
    conclusion = run.conclusion or ""
    lines = [f"{run.name} {conclusion}", run.title or "", *(run.summary or "").replace("\r\n", "\n").split("\n")]
    identity = readiness.failure_identity(f"check:{run.name}", lines)
    return CheckFailure(name=run.name, conclusion=conclusion, identity=identity,
                        evidence="\n".join(l for l in lines if l.strip()), details_url=run.details_url)


def evaluate_checks(required: Sequence[str], head_sha: str, runs: Sequence[CheckRun]) -> CheckVerdict:
    """Green / red / pending for `head_sha` from the runs the caller fetched FOR THAT SHA.

    For each required name the LATEST run of that name (last in the list) decides. A completed run
    with a GREEN conclusion is green, with a RED conclusion red; anything else (no run, queued,
    in_progress, neutral, unknown) is pending. Red wins over pending; pending wins over green. Runs
    for names that are not required are ignored: they are not what the merge waits for."""
    latest: Dict[str, CheckRun] = {}
    for run in runs:
        if run.name in required:
            latest[run.name] = run
    verdict = CheckVerdict(head_sha=head_sha, verdict="green")
    for name in required:
        run = latest.get(name)
        if run is None or run.status != "completed":
            verdict.pending.append(name)
        elif run.conclusion in GREEN_CONCLUSIONS:
            verdict.green.append(name)
        elif run.conclusion in RED_CONCLUSIONS:
            verdict.red.append(check_failure(run))
        else:
            verdict.pending.append(name)
    if verdict.red:
        verdict.verdict = "red"
    elif verdict.pending:
        verdict.verdict = "pending"
    return verdict


# ---------------------------------------------------------------------------------------------------
# The compact handoff: the ENTIRE context a repair run receives
# ---------------------------------------------------------------------------------------------------

def _bounded(text: str, max_lines: int = HANDOFF_MAX_LINES, max_chars: int = HANDOFF_MAX_CHARS) -> str:
    lines = text.replace("\r\n", "\n").split("\n")
    truncated = False
    if len(lines) > max_lines:
        lines = lines[:max_lines]
        truncated = True
    out = "\n".join(lines)
    if len(out) > max_chars:
        out = out[:max_chars]
        truncated = True
    if truncated:
        out = out.rstrip() + "\n" + TRUNCATED_MARKER
    return out


def compact_handoff(pr_row: Any, verdict: CheckVerdict, round_no: int, rounds_max: int, base_branch: str,
                    workspace: str) -> str:
    """The whole context a repair run gets: PR facts, the round, and per red check its name,
    conclusion, identity, details url and bounded evidence, then the instruction. It takes no
    transcript, session id, run result or previous prompt as input, so none can leak into it: fresh
    context on every round is a property of the signature, not of discipline."""
    out: List[str] = []
    out.append(f"# Repair round {round_no} of {rounds_max}: PR #{pr_row['pr_number']}")
    out.append("")
    out.append(f"- PR: #{pr_row['pr_number']} {pr_row['url'] or ''}".rstrip())
    out.append(f"- Branch: {pr_row['branch']}")
    out.append(f"- Base branch: {base_branch} (base sha {pr_row['base_sha']})")
    out.append(f"- Head sha: {verdict.head_sha}")
    out.append(f"- Workspace: {workspace}")
    out.append(f"- Verdict: {verdict.verdict}; green {list(verdict.green)}; pending {list(verdict.pending)}")
    out.append("")
    out.append("## Failing required checks")
    if not verdict.red:
        out.append("(none red; this handoff was built from a non-red verdict)")
    for failure in verdict.red:
        out.append("")
        out.append(f"### {failure.name}: {failure.conclusion}")
        out.append(f"- Identity: {failure.identity}")
        out.append(f"- Details: {failure.details_url or '(no details url)'}")
        out.append("- Evidence:")
        out.append("```")
        out.append(_bounded(failure.evidence))
        out.append("```")
    out.append("")
    out.append("## Instructions")
    out.append(f"Fix the ROOT CAUSE of each failing check above in the workspace ({workspace}).")
    out.append("Do not weaken, skip, disable or delete the failing check or its test.")
    out.append("Do not touch files unrelated to the failure.")
    out.append("Do not run git: the controller commits and pushes your edits and re-observes the checks.")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------------------------------------------
# The `prs` table
# ---------------------------------------------------------------------------------------------------

def _insert_pr_row(store: Store, task_id: str, view: PrView, base_sha: Optional[str], at: str) -> None:
    store.conn.execute(
        "INSERT INTO prs(pr_number, task_id, base_sha, head_sha, merge_sha, state, outcome, branch, url, title, "
        "created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
        (view.number, task_id, base_sha if base_sha is not None else view.base_sha, view.head_sha, None, "open", None,
         view.head_branch, view.url, view.title, at, at))


def open_pr_for_task(store: Store, task_id: str) -> Optional[sqlite3.Row]:
    """The task's `prs` row with state 'open', if any."""
    return store.row("SELECT * FROM prs WHERE task_id=? AND state='open' ORDER BY rowid DESC LIMIT 1", (task_id,))


def pr_row(store: Store, number: int) -> Optional[sqlite3.Row]:
    return store.row("SELECT * FROM prs WHERE pr_number=?", (number,))


def open_or_reuse_pr(store: Store, gh: GhClient, repo: str, task_id: str, head_branch: str, base_branch: str,
                     base_sha: Optional[str], title: str, body: str, now: Optional[float] = None) -> sqlite3.Row:
    """Idempotent, in this order: (1) an existing open `prs` row for the task is returned with zero gh
    calls; (2) an OPEN PR on GitHub for `head_branch` is adopted (row inserted, journal `pr-adopted`);
    (3) `gh.pr_create`, row inserted, journal `pr-opened`. Never a second row for the task, never a
    second PR for the branch: a crash between the create and the insert is healed by (2) next tick."""
    existing = open_pr_for_task(store, task_id)
    if existing is not None:
        return existing
    at = utcnow_iso(now)
    view = gh.pr_for_branch(repo, head_branch)
    if view is not None:
        kind = "pr-adopted"
    else:
        view = gh.pr_create(repo, head_branch, base_branch, title, body)
        kind = "pr-opened"
    with store.tx():
        _insert_pr_row(store, task_id, view, base_sha, at)
        store.journal(kind, task_id=task_id, now=now,
                      detail={"pr_number": view.number, "url": view.url, "branch": view.head_branch,
                              "head_sha": view.head_sha, "base_sha": base_sha or view.base_sha})
    row = pr_row(store, view.number)
    assert row is not None
    return row


def record_head(store: Store, number: int, head_sha: str, now: Optional[float] = None) -> bool:
    """Record the PR's current head. True when it changed (journal `pr-head`); False when it is the
    same sha, which writes nothing."""
    row = pr_row(store, number)
    if row is None:
        raise KeyError(f"no prs row for #{number}")
    if row["head_sha"] == head_sha:
        return False
    with store.tx():
        store.conn.execute("UPDATE prs SET head_sha=?, updated_at=? WHERE pr_number=?",
                           (head_sha, utcnow_iso(now), number))
        store.journal("pr-head", task_id=row["task_id"], now=now,
                      detail={"pr_number": number, "from": row["head_sha"], "to": head_sha})
    return True


def record_merged(store: Store, number: int, merge_sha: str, now: Optional[float] = None) -> None:
    """state 'merged', outcome 'merged', merge_sha recorded; journal `pr-merged`."""
    row = pr_row(store, number)
    if row is None:
        raise KeyError(f"no prs row for #{number}")
    with store.tx():
        store.conn.execute("UPDATE prs SET state='merged', outcome='merged', merge_sha=?, updated_at=? WHERE pr_number=?",
                           (merge_sha, utcnow_iso(now), number))
        store.journal("pr-merged", task_id=row["task_id"], now=now,
                      detail={"pr_number": number, "merge_sha": merge_sha, "head_sha": row["head_sha"]})


def record_closed(store: Store, number: int, now: Optional[float] = None) -> None:
    """state 'closed', outcome 'closed-unmerged'; journal `pr-closed-unmerged`. Never a success."""
    row = pr_row(store, number)
    if row is None:
        raise KeyError(f"no prs row for #{number}")
    with store.tx():
        store.conn.execute("UPDATE prs SET state='closed', outcome='closed-unmerged', updated_at=? WHERE pr_number=?",
                           (utcnow_iso(now), number))
        store.journal("pr-closed-unmerged", task_id=row["task_id"], now=now,
                      detail={"pr_number": number, "head_sha": row["head_sha"]})


def bump_repair_round(store: Store, number: int, now: Optional[float] = None) -> int:
    """Count one repair round against the PR; returns the new repair_rounds_used."""
    with store.tx():
        cur = store.conn.execute(
            "UPDATE prs SET repair_rounds_used = repair_rounds_used + 1, updated_at=? WHERE pr_number=?",
            (utcnow_iso(now), number))
        if cur.rowcount != 1:
            raise KeyError(f"no prs row for #{number}")
        row = store.conn.execute("SELECT repair_rounds_used FROM prs WHERE pr_number=?", (number,)).fetchone()
    return int(row["repair_rounds_used"])


def latest_merge_sha(store: Store) -> Optional[str]:
    """The newest merged PR's merge_sha (ORDER BY updated_at DESC, rowid DESC), or None."""
    row = store.row("SELECT merge_sha FROM prs WHERE state='merged' AND merge_sha IS NOT NULL "
                    "ORDER BY updated_at DESC, rowid DESC LIMIT 1")
    return None if row is None else row["merge_sha"]


def attribution_trailers(run_row: Any) -> str:
    """The commit trailers that name the worker which edited the workspace: its provider and model, from the
    run row. Only a Claude run carries a Claude co-author trailer; a Codex or Cursor run names itself and no
    co-author, so GitHub never credits Claude with another provider's work."""
    lines = [f"Worker: {run_row['provider']} {run_row['model']}"]
    if run_row["provider"] == "claude":
        lines.append("Co-Authored-By: Claude <noreply@anthropic.com>")
    return "\n".join(lines)


def pr_body(task_row: Any, base_sha: str, head_sha: str, run_row: Any = None) -> str:
    """The PR body the controller writes: task id, card, workstream, base and head sha, the worker (provider,
    model and run, when the run is given), who opened it, and a generated-by line last: the Claude Code line
    for a Claude run (or when no run is given), the worker's provider otherwise."""
    card = task_row["card_number"]
    lines = [
        f"## {task_row['title'] or task_row['task_id']}",
        "",
        f"- Task: `{task_row['task_id']}`",
        f"- Card: {'#' + str(card) if card is not None else '(none)'}",
        f"- Workstream: {task_row['workstream']}",
        f"- Base sha: `{base_sha}`",
        f"- Head sha: `{head_sha}`",
    ]
    if run_row is not None:
        lines.append(f"- Worker: `{run_row['provider']}` (`{run_row['model']}`), run `{run_row['run_id']}`")
    handoff_ref = task_row["handoff_ref"] if "handoff_ref" in task_row.keys() else None
    if handoff_ref:
        lines.append(f"- Handoff: {handoff_ref}")
    generated = (GENERATED_LINE if run_row is None or run_row["provider"] == "claude"
                 else f"Generated by the budgeted autonomy controller's `{run_row['provider']}` worker.")
    lines += [
        "",
        "This pull request was opened by the budgeted autonomy controller. The worker edited the workspace; "
        "the controller committed, pushed and opened the PR, and merges only when every required check is "
        "green on the current head.",
        "",
        generated,
    ]
    return "\n".join(lines) + "\n"
