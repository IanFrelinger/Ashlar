"""The git side of the PR lifecycle: the controller prepares a workspace, commits what the worker
edited and pushes it. The worker (a headless `claude -p` under `acceptEdits`) only edits files; it
never runs git, so every clone, branch, commit and push in this package happens here, through one
`git()` entry point that never opens a shell and never waits on a credential prompt.

Observed on git 2.55 for Windows (2026-10-11), and tests/test_gitops.py proves it against real git on
a local bare repository: `git clone --no-checkout --reference <dir> file:///...` writes
`.git/objects/info/alternates`; `git status --porcelain -z` prints `XY path\\0` and, for a rename,
`XY new\\0old\\0`; `git fetch origin <sha>` of a commit that no advertised ref reaches needs
`uploadpack.allowAnySHA1InWant` on the server (GitHub enables it; a bare test remote does not).
Assumed, not observed: a push GitHub rejects (non-fast-forward, permission) exits non-zero with the
reason on stderr, which `GitError` carries.
"""
from __future__ import annotations

import os
import subprocess
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence

CONTROLLER_AUTHOR_NAME = "ashlar-controller"
CONTROLLER_AUTHOR_EMAIL = "ashlar-controller@users.noreply.github.com"

# Inherited from the controller's own process these would point every clone at the WRONG repository
# (the Ashlar checkout the controller runs from, or a hook's repo), so the child never sees them.
_DROPPED_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES")


class GitError(Exception):
    """A git invocation failed. The message carries the command, the exit code and stderr."""


class StaleHead(GitError):
    """The branch tip is not the expected head: the PR moved between observation and repair."""


def _child_env(env: Optional[Mapping[str, str]]) -> Dict[str, str]:
    source = dict(env if env is not None else os.environ)
    for key in _DROPPED_ENV:
        source.pop(key, None)
    # A missing credential must FAIL, not hang the controller on a hidden prompt.
    source["GIT_TERMINAL_PROMPT"] = "0"
    return source


def git(args: Sequence[str], cwd: Optional[str], runner: Callable[..., Any] = subprocess.run,
        timeout: int = 600, env: Optional[Mapping[str, str]] = None) -> str:
    """One git invocation: `git <args>` in `cwd` (None = the process cwd), stdout stripped. Raises
    GitError on a non-zero exit, a timeout or an unrunnable executable, with the command and stderr in
    the message. argv is a list, so no argument ever passes through a shell."""
    argv = ["git", *args]
    shown = " ".join(argv)
    try:
        completed = runner(argv, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                           timeout=timeout, env=_child_env(env))
    except subprocess.TimeoutExpired as e:
        raise GitError(f"{shown} (cwd={cwd}): timed out after {timeout}s") from e
    except OSError as e:
        raise GitError(f"{shown} (cwd={cwd}): could not run: {e}") from e
    if completed.returncode != 0:
        stderr = (completed.stderr or "").strip()
        raise GitError(f"{shown} (cwd={cwd}) exited {completed.returncode}: {stderr}")
    return (completed.stdout or "").strip()


def head_sha(path: str, runner: Callable[..., Any] = subprocess.run) -> str:
    """The full sha of HEAD in the clone at `path`."""
    return git(["rev-parse", "--verify", "HEAD"], path, runner=runner)


def changed_files(path: str, runner: Callable[..., Any] = subprocess.run) -> List[str]:
    """Paths `git status --porcelain` reports in the clone at `path`, staged or not, in git's order; a
    rename contributes its new path. NUL-separated (-z) so a path with a space or a quote survives."""
    out = git(["status", "--porcelain", "-z", "--untracked-files=all"], path, runner=runner)
    entries = out.split("\0")
    paths: List[str] = []
    i = 0
    while i < len(entries):
        entry = entries[i]
        i += 1
        # Each entry is `XY<space>path`. `git()` strips stdout, so when the FIRST entry's X is a space
        # (unstaged change: " M a.txt") that space is gone and the entry reads `Y<space>path`.
        if len(entry) >= 4 and entry[2] == " ":
            status, rel = entry[:2], entry[3:]
        elif len(entry) >= 3 and entry[1] == " ":
            status, rel = " " + entry[0], entry[2:]
        else:
            continue
        paths.append(rel)
        if "R" in status or "C" in status:
            i += 1  # the original path follows as its own NUL-terminated entry
    return paths


def _require_fresh_path(path: str) -> None:
    if os.path.exists(path):
        if not os.path.isdir(path):
            raise GitError(f"workspace path exists and is not a directory: {path}")
        if os.listdir(path):
            raise GitError(f"workspace path is not empty: {path}")


def _clone(path: str, remote_url: str, reference: Optional[str], runner: Callable[..., Any]) -> None:
    _require_fresh_path(path)
    args = ["clone", "-q", "--no-checkout"]
    if reference and os.path.isdir(reference):
        args += ["--reference", reference]
    args += [remote_url, path]
    git(args, None, runner=runner)


def _set_identity(path: str, name: str, email: str, runner: Callable[..., Any]) -> None:
    git(["config", "user.name", name], path, runner=runner)
    git(["config", "user.email", email], path, runner=runner)


def _has_commit(path: str, sha: str, runner: Callable[..., Any]) -> bool:
    try:
        git(["cat-file", "-e", f"{sha}^{{commit}}"], path, runner=runner)
    except GitError:
        return False
    return True


def prepare_workspace(path: str, remote_url: str, base_sha: str, branch: str, *, reference: Optional[str] = None,
                      author_name: str = CONTROLLER_AUTHOR_NAME, author_email: str = CONTROLLER_AUTHOR_EMAIL,
                      runner: Callable[..., Any] = subprocess.run) -> str:
    """A fresh clone of `remote_url` at `path` (which must not exist, or be an empty directory) with a
    NEW local branch `branch` checked out at `base_sha`, and the controller's identity in the clone's
    config so the commit that follows is attributed to it. `--reference <reference>` is used when that
    directory exists (object borrowing; a missing reference is ignored, not an error). Fetches
    `base_sha` from origin when the clone does not have it. Returns the HEAD sha, which equals
    `base_sha`. Pushes nothing."""
    _clone(path, remote_url, reference, runner)
    if not _has_commit(path, base_sha, runner):
        git(["fetch", "-q", "origin", base_sha], path, runner=runner)
    git(["checkout", "-q", "-b", branch, base_sha], path, runner=runner)
    _set_identity(path, author_name, author_email, runner)
    head = head_sha(path, runner=runner)
    want = git(["rev-parse", "--verify", f"{base_sha}^{{commit}}"], path, runner=runner)
    if head != want:
        raise GitError(f"workspace HEAD {head} is not the base {want} after checkout of {branch}")
    return head


def checkout_pr_head(path: str, remote_url: str, branch: str, expected_head: str, *,
                     reference: Optional[str] = None, author_name: str = CONTROLLER_AUTHOR_NAME,
                     author_email: str = CONTROLLER_AUTHOR_EMAIL,
                     runner: Callable[..., Any] = subprocess.run) -> str:
    """A fresh clone with `branch` checked out at `origin/<branch>` for a repair run. When HEAD is not
    `expected_head` (the sha the controller observed the checks for) raises StaleHead: the PR moved,
    and the caller re-observes instead of repairing a head nobody checked. Sets the controller's
    identity. Returns HEAD."""
    _clone(path, remote_url, reference, runner)
    git(["checkout", "-q", "-B", branch, f"origin/{branch}"], path, runner=runner)
    _set_identity(path, author_name, author_email, runner)
    head = head_sha(path, runner=runner)
    if head != expected_head:
        raise StaleHead(f"{branch} is at {head}, expected {expected_head}: the PR head moved; "
                        f"re-observe before repairing")
    return head


def commit_and_push(path: str, branch: str, message: str, *,
                    runner: Callable[..., Any] = subprocess.run) -> Optional[str]:
    """Stage everything the worker changed in the clone at `path`; when nothing changed return None
    (no commit, no push: there is nothing to propose). Otherwise commit with `message` as ONE argv
    element and push `branch` to origin, returning the new HEAD sha. A rejected push raises GitError."""
    git(["add", "-A"], path, runner=runner)
    if not git(["status", "--porcelain"], path, runner=runner):
        return None
    git(["commit", "-q", "-m", message], path, runner=runner)
    git(["push", "-q", "-u", "origin", branch], path, runner=runner)
    return head_sha(path, runner=runner)
