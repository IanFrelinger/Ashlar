# PR 3 (budgeted-autonomy phase C): lane spec

Branch base: `feat/budgeted-autonomy-pr-lifecycle` @ `7b69b7493` (master `f236bd4ad` + one shared commit:
schema 2, the policy `pr` section, the new TaskState/StopReason names, LaunchRequest fields). Your worktree may
sit at master: run `git fetch origin && git reset --hard 7b69b7493` FIRST and check `git log -1` shows
"schema 2 and the policy pr section (PR 3 base)".

Package: `tools/autonomy-controller/` (Python 3.8+ stdlib only; no third-party imports). Run the suite from the
repo root with `bash tests/scripts/autonomy-controller.test.sh` (218 tests, `OK (skipped=1)` on this Windows host).
Every test must pass. Never run `dotnet`. Never push. Never touch `controller.py`, `cli.py`, `README.md`,
`CHANGELOG.md` or `tests/scripts/autonomy-controller.test.sh` (lane C owns them). Commit on your worktree branch
with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Report the commit SHA(s) when done.

## Design facts both lanes rely on

- The worker (a headless `claude -p` under `acceptEdits`) can only EDIT files in its workspace; it cannot run git
  or gh. The CONTROLLER prepares the workspace (a clone of the base SHA on a fresh branch), commits and pushes
  after the run, opens the PR, observes checks, launches repairs, and merges. Lane B is the git side, lane A the
  GitHub side; lane C (the integrator) wires them into `Controller.tick`.
- Checks are observed BY HEAD SHA: the required checks are read for the PR's current head commit only
  (`GET repos/{repo}/commits/{sha}/check-runs`), never from the PR's aggregate status, so a green run on an
  earlier head can never satisfy a changed head. The merge also passes `--match-head-commit <head>`.
- Required checks come from `policy.pr["required_checks"]` (the five on master). Green = conclusion `success`
  or `skipped`; red = `failure`, `cancelled`, `timed_out`, `action_required`, `stale`, `startup_failure`;
  anything else (no run yet, `queued`, `in_progress`, `neutral`) = pending. A required check with NO run for
  the head is pending, not green.
- A failure identity for a red check is `readiness.failure_identity(gate=f"check:{name}", evidence_lines)` over
  the check-run's `output.title` and `output.summary` lines (plus the name and conclusion), so the same failure
  on two heads has the same identity.
- Store rows: `prs(pr_number PK, task_id, base_sha, head_sha, merge_sha, state, outcome, repair_rounds_used,
  premium_escalations_used, provider_transfers_used, branch, url, title, created_at, updated_at)`.
  `state` is `open` | `merged` | `closed`; `outcome` is NULL while open, then `merged` or `closed-unmerged`.
  Use `store.tx()` for writes and `store.journal(kind, task_id=, detail=)` for the record.

## Lane A: `ashlar_controller/prs.py` + `tests/test_prs.py`

```python
REQUIRED_CHECKS_DEFAULT = ("cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary")
GREEN_CONCLUSIONS = ("success", "skipped")
RED_CONCLUSIONS = ("failure", "cancelled", "timed_out", "action_required", "stale", "startup_failure")

@dataclass
class PrView:            # what `gh pr view N --json number,state,headRefOid,baseRefOid,headRefName,baseRefName,mergeCommit,url,title` gives
    number: int; state: str              # "OPEN" | "MERGED" | "CLOSED"
    head_sha: str; base_sha: Optional[str]; head_branch: str; base_branch: str
    merge_sha: Optional[str]; url: str; title: str = ""

@dataclass
class CheckRun:          # one element of check-runs API `check_runs[]`
    name: str; status: str; conclusion: Optional[str]; title: str = ""; summary: str = ""; details_url: Optional[str] = None

@dataclass
class CheckFailure:
    name: str; conclusion: str; identity: str; evidence: str; details_url: Optional[str]

@dataclass
class CheckVerdict:
    head_sha: str; verdict: str          # "green" | "red" | "pending"
    green: List[str]; red: List[CheckFailure]; pending: List[str]

class MergeRefused(Exception): ...      # gh refused the merge (head moved, checks, permissions); message = gh stderr

class GhClient(Protocol):
    def pr_create(self, repo: str, head_branch: str, base_branch: str, title: str, body: str) -> PrView: ...
    def pr_for_branch(self, repo: str, head_branch: str) -> Optional[PrView]: ...   # the OPEN PR for that head branch, else None
    def pr_view(self, repo: str, number: int) -> PrView: ...
    def check_runs(self, repo: str, sha: str) -> List[CheckRun]: ...               # for THAT commit only
    def pr_merge(self, repo: str, number: int, head_sha: str) -> None: ...         # squash; --match-head-commit head_sha; raises MergeRefused
    def branch_head(self, repo: str, branch: str) -> Optional[str]: ...            # remote branch tip, or None when absent

class GhCli:    # GhClient over the `gh` executable (subprocess, injectable runner, timeouts), argv exactly:
    # pr_create:    gh pr create --repo R --head B --base BASE --title T --body-file <tmp>   -> then pr_for_branch to get the PrView
    # pr_for_branch: gh pr list --repo R --head B --state open --json number,state,headRefOid,baseRefOid,headRefName,baseRefName,mergeCommit,url,title
    # pr_view:      gh pr view N --repo R --json <same fields>
    # check_runs:   gh api --paginate repos/R/commits/SHA/check-runs --jq '.check_runs[] | {name,status,conclusion,details_url,output:{title,summary}}'   (one JSON object per line)
    # pr_merge:     gh pr merge N --repo R --squash --match-head-commit SHA
    # branch_head:  gh api repos/R/branches/B --jq .commit.sha   (exit non-zero / 404 -> None)
    # Parse ONLY these shapes. A runner that fails for a reason other than "not found" raises a GhError(stderr) subclass of Exception.

class FakeGh:   # in-memory GhClient for tests, counts every call (create_count, merge_count, ...):
    # .prs: Dict[int, PrView]; .checks: Dict[str, List[CheckRun]]; .branches: Dict[str, str]
    # helpers: set_checks(sha, {name: conclusion_or_None}) ; set_in_progress(sha, names); push_head(number, new_sha) (updates PrView.head_sha);
    #          close(number) (state CLOSED, merge_sha None); merge_externally(number, merge_sha) (state MERGED);
    #          pr_merge honours head_sha: a mismatch raises MergeRefused and merges nothing; a successful merge sets MERGED with merge_sha f"merge-{number}-{head_sha[:7]}"
    #          pr_create refuses a second OPEN PR for the same head branch (raises GhError) - GitHub does the same.

def evaluate_checks(required: Sequence[str], head_sha: str, runs: Sequence[CheckRun]) -> CheckVerdict
    # Only runs are for `head_sha` (caller fetched them by sha). For each required name take the LATEST run of that
    # name (last in the list). Red wins over pending; pending wins over green. A name with no run is pending.
    # The identity of a red check: readiness.failure_identity(f"check:{name}", [f"{name} {conclusion}", title, *summary lines])

def compact_handoff(pr_row, verdict: CheckVerdict, round_no: int, rounds_max: int, base_branch: str, workspace: str) -> str
    # The ENTIRE context a repair run receives. Contains: PR number and url, branch, base branch and base sha,
    # head sha, round "N of M", then per red check: name, conclusion, identity, details_url, evidence (bounded:
    # at most 60 lines / 4000 chars per check, truncated with a "[... truncated]" marker), then the instruction
    # (fix the root cause in the workspace, do not weaken/skip/delete the failing check, do not touch unrelated
    # files, do not run git; the controller commits and pushes). It must NEVER include a transcript, a session id,
    # a run result text or a previous prompt: the function takes none of those as input, which is the proof.

def open_or_reuse_pr(store, gh: GhClient, repo: str, task_id: str, head_branch: str, base_branch: str,
                     base_sha: Optional[str], title: str, body: str, now: Optional[float] = None) -> sqlite3.Row
    # Idempotent in this order: (1) an existing `prs` row for task_id with state 'open' -> return it (zero gh calls);
    # (2) gh.pr_for_branch finds an OPEN PR for head_branch -> adopt it: insert the row, journal "pr-adopted";
    # (3) gh.pr_create -> insert the row (state 'open', head_sha from the PrView, branch, url, title), journal "pr-opened".
    # Returns the prs row. Never creates a second row for the task or a second PR for the branch.

def record_head(store, number: int, head_sha: str, now=None) -> bool          # True when it changed; journal "pr-head" on change
def record_merged(store, number: int, merge_sha: str, now=None) -> None       # state 'merged', outcome 'merged', merge_sha; journal "pr-merged"
def record_closed(store, number: int, now=None) -> None                       # state 'closed', outcome 'closed-unmerged'; journal "pr-closed-unmerged"
def bump_repair_round(store, number: int, now=None) -> int                    # returns the new repair_rounds_used
def open_pr_for_task(store, task_id: str) -> Optional[sqlite3.Row]            # the task's prs row with state 'open'
def latest_merge_sha(store) -> Optional[str]                                   # newest merged PR's merge_sha (ORDER BY updated_at DESC, rowid DESC)
def pr_body(task_row, base_sha: str, head_sha: str) -> str                    # the PR body the controller writes (task id, card, workstream, base sha, "opened by the budgeted autonomy controller", ends with the line "🤖 Generated with [Claude Code](https://claude.com/claude-code)")
```

Tests (`tests/test_prs.py`, use `ControllerCase` from `tests/helpers.py` for a store; FakeGh for GitHub), each
behaviour must be observable red by a one-line mutation of `prs.py`:
- evaluate_checks: all five success -> green; one failure -> red with the identity; a missing run -> pending even
  when the other four are green; an in_progress run -> pending; a red beats a pending; skipped counts green; a
  run for a name that is not required is ignored; latest run of a name wins; identity equal across two heads
  with the same summary and different for different summaries.
- "Old green cannot satisfy a changed head": FakeGh with checks green on sha A, PR head pushed to sha B with no
  runs -> evaluate on B is pending, and `pr_merge(number, A)` on the FakeGh raises MergeRefused (head is B).
- open_or_reuse_pr: three calls -> one row, one `pr_create`; a pre-existing OPEN PR on GitHub for the branch is
  adopted with zero creates; a second task cannot reuse the first task's row.
- record_* and the journal kinds; latest_merge_sha ordering; bump_repair_round increments.
- compact_handoff: contains the identities and evidence; truncates long evidence; does not contain a string
  passed nowhere (e.g. assert "TRANSCRIPT" not in text when the fake run result carries it, which you show by
  building the handoff from only pr_row + verdict).
- GhCli argv: with a recording fake runner, assert the exact argv for each method and the parsing of the
  documented JSON shapes (one check-runs object per line; `gh pr view` JSON; branch 404 -> None; merge non-zero
  -> MergeRefused with stderr).

## Lane B: `ashlar_controller/gitops.py` + `tests/test_gitops.py`

```python
class GitError(Exception): ...          # message carries the command and stderr
class StaleHead(GitError): ...          # the branch tip is not the expected head

def git(args: Sequence[str], cwd: Optional[str], runner=subprocess.run, timeout: int = 600, env=None) -> str
    # one git invocation; stdout stripped; GitError on non-zero exit. The child env drops GIT_DIR/GIT_WORK_TREE
    # and sets GIT_TERMINAL_PROMPT=0 so a missing credential fails instead of hanging.

def prepare_workspace(path: str, remote_url: str, base_sha: str, branch: str, *, reference: Optional[str] = None,
                      author_name: str = "ashlar-controller", author_email: str = "ashlar-controller@users.noreply.github.com",
                      runner=subprocess.run) -> str
    # Fresh clone of remote_url into `path` (path must not exist or must be an empty dir; otherwise GitError),
    # `--reference <reference>` when given and it exists, checkout a NEW local branch `branch` at `base_sha`
    # (git fetch origin <base_sha> first if the clone does not have it), set user.name/user.email in the clone's
    # config, and return the HEAD sha (== base_sha). No push.

def checkout_pr_head(path: str, remote_url: str, branch: str, expected_head: str, *, reference=None, runner=subprocess.run) -> str
    # Fresh clone, `git checkout -B <branch> origin/<branch>`; if HEAD != expected_head raise StaleHead (the PR
    # moved between observation and repair: the caller re-observes). Sets the same identity. Returns HEAD.

def commit_and_push(path: str, branch: str, message: str, *, runner=subprocess.run) -> Optional[str]
    # `git add -A`; if `git status --porcelain` is empty -> return None (nothing to propose; no commit, no push);
    # else `git commit -q -m <message>` (message is passed as ONE argument, never through a shell) and
    # `git push -u origin <branch>`; return the new HEAD sha. A rejected push raises GitError.

def head_sha(path: str, runner=subprocess.run) -> str
def changed_files(path: str, runner=subprocess.run) -> List[str]     # porcelain paths (staged or not), for the journal
```

Tests (`tests/test_gitops.py`) run REAL git against a local bare repository created in a temp dir (`git init
--bare`, seeded by a throwaway clone with one commit on `master`; use `file://` URLs; set `GIT_CONFIG_GLOBAL` to an
empty file in the env you pass so the host's config does not leak). Skip the whole module with
`unittest.skipUnless(shutil.which("git"), "git not installed")`. Cover, each observable red by a one-line mutation:
- prepare_workspace clones at the base sha on the new branch (HEAD == base_sha, `git branch --show-current` == branch), refuses a non-empty path, uses `--reference` when given (assert `.git/objects/info/alternates` exists).
- commit_and_push with no edits returns None and the remote branch does not exist; with an edit returns a sha, the remote tip equals it, and the commit author is ashlar-controller.
- a second commit_and_push after a second edit advances the remote tip; `changed_files` lists the edited path before the commit.
- checkout_pr_head at the right head returns it; at a stale expected head raises StaleHead and nothing is pushed.
- `git` sets GIT_TERMINAL_PROMPT=0 (assert on a recording runner) and raises GitError with the stderr on failure.
- Windows: paths with spaces work (temp dir with a space in the name).
