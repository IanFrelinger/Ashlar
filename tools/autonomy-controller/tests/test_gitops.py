"""The git side of the PR lifecycle, proven against REAL git: a bare repository in a temp directory
(whose name contains a space) stands in for GitHub, reached over a file:// URL. The host's git
config is kept out with GIT_CONFIG_GLOBAL pointing at an empty file and GIT_CONFIG_NOSYSTEM=1, so a
host-wide user.name, autocrlf or credential helper cannot make a test pass or fail. The module is
skipped, not failed, where git is not installed."""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
from unittest import mock

from ashlar_controller.gitops import (CONTROLLER_AUTHOR_EMAIL, CONTROLLER_AUTHOR_NAME, GitError, StaleHead,
                                      changed_files, checkout_pr_head, commit_and_push, git, head_sha,
                                      prepare_workspace)


def _run(argv: List[str], cwd: Optional[str], env: Dict[str, str]) -> str:
    """Fixture plumbing that does not go through the module under test, so a mutation of gitops.py
    breaks the assertion it is aimed at and not the scaffolding around it."""
    completed = subprocess.run(["git", *argv], cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", env=env,
                               timeout=120)
    if completed.returncode != 0:
        raise AssertionError(f"fixture git {argv} failed: {completed.stderr}")
    return completed.stdout.strip()


class RecordingRunner:
    """Stands in for subprocess.run; records argv and keyword arguments and answers with canned results."""

    def __init__(self, results: Optional[List[Any]] = None):
        self.calls: List[Tuple[List[str], Dict[str, Any]]] = []
        self.results = list(results or [])

    def __call__(self, argv, **kw):
        self.calls.append((list(argv), kw))
        if self.results:
            r = self.results.pop(0)
            if isinstance(r, BaseException):
                raise r
            return r
        return subprocess.CompletedProcess(argv, 0, stdout="", stderr="")


@unittest.skipUnless(shutil.which("git"), "git not installed")
class GitFixture(unittest.TestCase):
    """A bare `origin.git` seeded with one commit on master, in a temp dir with a space in its name."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="ashlar gitops "))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.assertIn(" ", str(self.tmp))
        empty_config = self.tmp / "empty-gitconfig"
        empty_config.write_text("", encoding="utf-8")
        isolation = {"GIT_CONFIG_GLOBAL": str(empty_config), "GIT_CONFIG_NOSYSTEM": "1",
                     "GIT_DIR": str(self.tmp / "WRONG-GIT-DIR"), "GIT_WORK_TREE": str(self.tmp / "WRONG-WORK-TREE")}
        # GIT_DIR / GIT_WORK_TREE are planted so a clone that inherited them would break: gitops.git
        # must drop them. The fixture's own git calls use the clean env below.
        patcher = mock.patch.dict(os.environ, isolation)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.env = {k: v for k, v in os.environ.items() if k not in ("GIT_DIR", "GIT_WORK_TREE")}
        self.env["GIT_TERMINAL_PROMPT"] = "0"

        self.bare = self.tmp / "origin.git"
        _run(["init", "-q", "--bare", "--initial-branch=master", str(self.bare)], None, self.env)
        self.remote_url = self.bare.as_uri()
        self.assertTrue(self.remote_url.startswith("file:///"))

        seed = self.tmp / "seed"
        _run(["init", "-q", "--initial-branch=master", str(seed)], None, self.env)
        _run(["config", "user.name", "Seed Author"], str(seed), self.env)
        _run(["config", "user.email", "seed@example.invalid"], str(seed), self.env)
        (seed / "README.md").write_text("# seed\n", encoding="utf-8")
        _run(["add", "README.md"], str(seed), self.env)
        _run(["commit", "-q", "-m", "seed: first commit"], str(seed), self.env)
        _run(["remote", "add", "origin", self.remote_url], str(seed), self.env)
        _run(["push", "-q", "origin", "master"], str(seed), self.env)
        self.seed = seed
        self.base_sha = _run(["rev-parse", "HEAD"], str(seed), self.env)
        self.assertEqual(len(self.base_sha), 40)

    # -- helpers --------------------------------------------------------------------------------------

    def ws(self, name: str = "work space") -> str:
        return str(self.tmp / name)

    def remote_tip(self, branch: str) -> Optional[str]:
        """The sha of `branch` in the bare repository, or None when the branch does not exist there."""
        completed = subprocess.run(["git", "rev-parse", "--verify", "-q", f"refs/heads/{branch}"], cwd=str(self.bare),
                                   stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                   env=self.env, timeout=60)
        if completed.returncode != 0:
            return None
        return completed.stdout.strip() or None

    def remote_log(self, sha: str, fmt: str) -> str:
        return _run(["log", "-1", f"--format={fmt}", sha], str(self.bare), self.env)

    def push_second_commit_from_seed(self, branch: str) -> str:
        """Another actor (a human, another agent) advances `branch` on the remote."""
        _run(["fetch", "-q", "origin", branch], str(self.seed), self.env)
        _run(["checkout", "-q", "-B", branch, "FETCH_HEAD"], str(self.seed), self.env)
        (self.seed / "OTHER.txt").write_text("someone else pushed\n", encoding="utf-8")
        _run(["add", "-A"], str(self.seed), self.env)
        _run(["commit", "-q", "-m", "other actor"], str(self.seed), self.env)
        _run(["push", "-q", "origin", branch], str(self.seed), self.env)
        return _run(["rev-parse", "HEAD"], str(self.seed), self.env)


class PrepareWorkspaceTests(GitFixture):

    def test_clones_base_sha_on_the_new_branch_and_pushes_nothing(self):
        path = self.ws()
        head = prepare_workspace(path, self.remote_url, self.base_sha, "ctl/t1")
        self.assertEqual(head, self.base_sha)
        self.assertEqual(_run(["rev-parse", "HEAD"], path, self.env), self.base_sha)
        self.assertEqual(_run(["branch", "--show-current"], path, self.env), "ctl/t1")
        self.assertTrue((Path(path) / "README.md").is_file(), "the branch is checked out, not bare")
        self.assertIsNone(self.remote_tip("ctl/t1"), "prepare_workspace must not push")

    def test_sets_the_controller_identity_in_the_clone_config(self):
        path = self.ws()
        prepare_workspace(path, self.remote_url, self.base_sha, "ctl/t1")
        self.assertEqual(_run(["config", "--local", "user.name"], path, self.env), CONTROLLER_AUTHOR_NAME)
        self.assertEqual(_run(["config", "--local", "user.email"], path, self.env), CONTROLLER_AUTHOR_EMAIL)
        self.assertEqual(CONTROLLER_AUTHOR_NAME, "ashlar-controller")

    def test_identity_is_overridable(self):
        path = self.ws()
        prepare_workspace(path, self.remote_url, self.base_sha, "ctl/t1", author_name="Other Bot",
                          author_email="other@example.invalid")
        self.assertEqual(_run(["config", "--local", "user.name"], path, self.env), "Other Bot")

    def test_refuses_a_non_empty_path(self):
        path = Path(self.ws())
        path.mkdir()
        (path / "leftover.txt").write_text("x", encoding="utf-8")
        with self.assertRaises(GitError) as cm:
            prepare_workspace(str(path), self.remote_url, self.base_sha, "ctl/t1")
        self.assertIn("not empty", str(cm.exception))
        self.assertFalse((path / ".git").exists(), "nothing was cloned into the occupied path")

    def test_refuses_a_path_that_is_a_file(self):
        path = Path(self.ws())
        path.write_text("x", encoding="utf-8")
        with self.assertRaises(GitError):
            prepare_workspace(str(path), self.remote_url, self.base_sha, "ctl/t1")

    def test_accepts_an_existing_empty_directory(self):
        path = Path(self.ws())
        path.mkdir()
        self.assertEqual(prepare_workspace(str(path), self.remote_url, self.base_sha, "ctl/t1"), self.base_sha)

    def test_uses_reference_when_it_exists(self):
        path = self.ws()
        prepare_workspace(path, self.remote_url, self.base_sha, "ctl/t1", reference=str(self.seed))
        alternates = Path(path) / ".git" / "objects" / "info" / "alternates"
        self.assertTrue(alternates.is_file(), "a --reference clone records the borrowed object store")
        self.assertIn("seed", alternates.read_text(encoding="utf-8"))

    def test_ignores_a_reference_that_does_not_exist(self):
        path = self.ws()
        prepare_workspace(path, self.remote_url, self.base_sha, "ctl/t1", reference=str(self.tmp / "no such dir"))
        self.assertFalse((Path(path) / ".git" / "objects" / "info" / "alternates").exists())

    def test_fetches_a_base_sha_the_clone_does_not_have(self):
        # A commit reachable only from a ref `git clone` does not fetch (refs/pull/*), like a PR head
        # the controller observed but no branch points at. The server must allow a want by sha, as
        # GitHub does; the bare fixture is configured the same way.
        _run(["config", "uploadpack.allowAnySHA1InWant", "true"], str(self.bare), self.env)
        _run(["checkout", "-q", "-b", "scratch"], str(self.seed), self.env)
        (self.seed / "hidden.txt").write_text("only on a pull ref\n", encoding="utf-8")
        _run(["add", "-A"], str(self.seed), self.env)
        _run(["commit", "-q", "-m", "hidden commit"], str(self.seed), self.env)
        hidden = _run(["rev-parse", "HEAD"], str(self.seed), self.env)
        _run(["push", "-q", "origin", f"HEAD:refs/pull/1/head"], str(self.seed), self.env)
        _run(["checkout", "-q", "master"], str(self.seed), self.env)
        self.assertIsNone(self.remote_tip("scratch"))

        path = self.ws()
        self.assertEqual(prepare_workspace(path, self.remote_url, hidden, "ctl/t2"), hidden)
        self.assertTrue((Path(path) / "hidden.txt").is_file())

    def test_unknown_base_sha_raises_giterror(self):
        with self.assertRaises(GitError):
            prepare_workspace(self.ws(), self.remote_url, "0123456789abcdef0123456789abcdef01234567", "ctl/t1")

    def test_existing_branch_name_is_refused(self):
        # `-b` creates a NEW branch; the clone already has master.
        with self.assertRaises(GitError):
            prepare_workspace(self.ws(), self.remote_url, self.base_sha, "master")


class CommitAndPushTests(GitFixture):

    def setUp(self) -> None:
        super().setUp()
        self.path = self.ws()
        prepare_workspace(self.path, self.remote_url, self.base_sha, "ctl/t1")

    def edit(self, rel: str, text: str) -> None:
        p = Path(self.path) / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def test_no_edits_returns_none_and_pushes_nothing(self):
        self.assertIsNone(commit_and_push(self.path, "ctl/t1", "ctl: nothing"))
        self.assertIsNone(self.remote_tip("ctl/t1"))
        self.assertEqual(_run(["rev-parse", "HEAD"], self.path, self.env), self.base_sha, "no commit was made")

    def test_edit_is_committed_by_the_controller_and_pushed(self):
        self.edit("src/new file.txt", "edited by the worker\n")
        sha = commit_and_push(self.path, "ctl/t1", "ctl: worker edit")
        self.assertIsNotNone(sha)
        self.assertEqual(len(sha), 40)
        self.assertNotEqual(sha, self.base_sha)
        self.assertEqual(self.remote_tip("ctl/t1"), sha)
        self.assertEqual(self.remote_log(sha, "%an"), "ashlar-controller")
        self.assertEqual(self.remote_log(sha, "%ae"), CONTROLLER_AUTHOR_EMAIL)
        self.assertEqual(self.remote_log(sha, "%P"), self.base_sha, "one commit on top of the base")
        self.assertEqual(_run(["rev-parse", "--abbrev-ref", "@{upstream}"], self.path, self.env), "origin/ctl/t1")

    def test_message_is_one_argument_with_quotes_and_newlines_intact(self):
        self.edit("a.txt", "a\n")
        message = 'ctl: fix "quoted" $HOME `thing`\n\nBody line.\n\nCo-Authored-By: X <x@example.invalid>'
        sha = commit_and_push(self.path, "ctl/t1", message)
        self.assertEqual(self.remote_log(sha, "%B").strip(), message.strip())

    # Kills: dropping the `reset --soft <start_sha>` fold (pre-flight finding C9: a worker that commits its own
    # edit leaves a clean tree, the run reads as no-change-to-propose, and its commit is stranded).
    def test_a_worker_commit_is_folded_into_one_controller_commit(self):
        self.edit("w.txt", "the worker's edit\n")
        _run(["add", "-A"], self.path, self.env)
        _run(["-c", "user.name=worker", "-c", "user.email=worker@example.invalid", "commit", "-q", "-m", "worker"],
             self.path, self.env)
        self.assertIsNone(commit_and_push(self.path, "ctl/t1", "ctl: not folded"), "without start_sha: stranded")
        sha = commit_and_push(self.path, "ctl/t1", "ctl: folded", start_sha=self.base_sha)
        self.assertIsNotNone(sha)
        self.assertEqual(self.remote_tip("ctl/t1"), sha)
        self.assertEqual(self.remote_log(sha, "%an"), "ashlar-controller")
        self.assertEqual(self.remote_log(sha, "%P"), self.base_sha, "one commit on top of the base")
        self.assertEqual(self.remote_log(sha, "%s"), "ctl: folded")
        self.assertEqual(_run(["show", f"{sha}:w.txt"], self.path, self.env), "the worker's edit")

    # Kills: a fold that commits when the worker's commits add up to no change.
    def test_worker_commits_that_change_nothing_are_still_nothing_to_propose(self):
        self.edit("w.txt", "x\n")
        _run(["add", "-A"], self.path, self.env)
        _run(["-c", "user.name=w", "-c", "user.email=w@example.invalid", "commit", "-q", "-m", "add"], self.path, self.env)
        _run(["rm", "-q", "w.txt"], self.path, self.env)
        _run(["-c", "user.name=w", "-c", "user.email=w@example.invalid", "commit", "-q", "-m", "rm"], self.path, self.env)
        self.assertIsNone(commit_and_push(self.path, "ctl/t1", "ctl: nothing", start_sha=self.base_sha))
        self.assertIsNone(self.remote_tip("ctl/t1"))

    def test_second_push_advances_the_remote_tip(self):
        self.edit("a.txt", "a\n")
        first = commit_and_push(self.path, "ctl/t1", "ctl: first")
        self.edit("a.txt", "a and more\n")
        self.assertEqual(changed_files(self.path), ["a.txt"], "the edit is visible before the commit")
        second = commit_and_push(self.path, "ctl/t1", "ctl: second")
        self.assertNotEqual(first, second)
        self.assertEqual(self.remote_tip("ctl/t1"), second)
        self.assertEqual(self.remote_log(second, "%P"), first)
        self.assertEqual(changed_files(self.path), [], "nothing is left over after the commit")

    def test_changed_files_lists_new_modified_deleted_and_renamed_paths(self):
        self.edit("README.md", "# changed\n")
        self.edit("dir with space/file name.txt", "x\n")
        self.assertEqual(sorted(changed_files(self.path)), ["README.md", "dir with space/file name.txt"])
        # Staged rename and deletion read the same way; the rename contributes its NEW path only.
        _run(["add", "-A"], self.path, self.env)
        _run(["mv", "dir with space/file name.txt", "renamed.txt"], self.path, self.env)
        _run(["rm", "-q", "-f", "README.md"], self.path, self.env)
        files = changed_files(self.path)
        self.assertIn("renamed.txt", files)
        self.assertIn("README.md", files)
        self.assertNotIn("dir with space/file name.txt", files)
        self.assertEqual(len(files), 2)

    def test_rejected_push_raises_giterror(self):
        self.edit("a.txt", "a\n")
        commit_and_push(self.path, "ctl/t1", "ctl: first")
        self.push_second_commit_from_seed("ctl/t1")           # the remote moves on under us
        self.edit("a.txt", "a2\n")
        with self.assertRaises(GitError) as cm:
            commit_and_push(self.path, "ctl/t1", "ctl: second")
        self.assertIn("push", str(cm.exception))
        self.assertIn("rejected", str(cm.exception).lower())

    def test_head_sha_matches_rev_parse(self):
        self.assertEqual(head_sha(self.path), _run(["rev-parse", "HEAD"], self.path, self.env))


class CheckoutPrHeadTests(GitFixture):

    def setUp(self) -> None:
        super().setUp()
        ws1 = self.ws("first")
        prepare_workspace(ws1, self.remote_url, self.base_sha, "ctl/t1")
        (Path(ws1) / "a.txt").write_text("a\n", encoding="utf-8")
        self.pr_head = commit_and_push(ws1, "ctl/t1", "ctl: first")
        self.assertEqual(self.remote_tip("ctl/t1"), self.pr_head)

    def test_right_head_is_checked_out_on_the_branch(self):
        path = self.ws("repair")
        self.assertEqual(checkout_pr_head(path, self.remote_url, "ctl/t1", self.pr_head), self.pr_head)
        self.assertEqual(_run(["rev-parse", "HEAD"], path, self.env), self.pr_head)
        self.assertEqual(_run(["branch", "--show-current"], path, self.env), "ctl/t1")
        self.assertTrue((Path(path) / "a.txt").is_file())
        self.assertEqual(_run(["config", "--local", "user.name"], path, self.env), CONTROLLER_AUTHOR_NAME)

    def test_stale_expected_head_raises_and_nothing_is_pushed(self):
        moved = self.push_second_commit_from_seed("ctl/t1")
        path = self.ws("repair")
        with self.assertRaises(StaleHead) as cm:
            checkout_pr_head(path, self.remote_url, "ctl/t1", self.pr_head)
        self.assertIn(self.pr_head, str(cm.exception))
        self.assertIn(moved, str(cm.exception))
        self.assertIsInstance(cm.exception, GitError)
        self.assertEqual(self.remote_tip("ctl/t1"), moved, "the remote is untouched")
        self.assertEqual(self.remote_log(moved, "%an"), "Seed Author")

    def test_a_repair_commit_lands_on_top_of_the_pr_head(self):
        path = self.ws("repair")
        checkout_pr_head(path, self.remote_url, "ctl/t1", self.pr_head)
        (Path(path) / "a.txt").write_text("repaired\n", encoding="utf-8")
        sha = commit_and_push(path, "ctl/t1", "ctl: repair round 1")
        self.assertEqual(self.remote_tip("ctl/t1"), sha)
        self.assertEqual(self.remote_log(sha, "%P"), self.pr_head)

    def test_missing_branch_raises_giterror(self):
        with self.assertRaises(GitError):
            checkout_pr_head(self.ws("repair"), self.remote_url, "ctl/does-not-exist", self.pr_head)


class GitInvocationTests(GitFixture):

    def test_child_env_sets_terminal_prompt_off_and_drops_git_dir(self):
        runner = RecordingRunner()
        with mock.patch.dict(os.environ, {"GIT_DIR": "/nope", "GIT_WORK_TREE": "/nope", "KEEP_ME": "yes"}):
            git(["status"], "/some/cwd", runner=runner, timeout=7)
        self.assertEqual(len(runner.calls), 1)
        argv, kw = runner.calls[0]
        self.assertEqual(argv, ["git", "status"])
        self.assertEqual(kw["cwd"], "/some/cwd")
        self.assertEqual(kw["timeout"], 7)
        self.assertEqual(kw["env"]["GIT_TERMINAL_PROMPT"], "0")
        self.assertNotIn("GIT_DIR", kw["env"])
        self.assertNotIn("GIT_WORK_TREE", kw["env"])
        self.assertEqual(kw["env"]["KEEP_ME"], "yes")
        self.assertEqual(kw["stdin"], subprocess.DEVNULL)

    def test_explicit_env_is_used_instead_of_the_process_env(self):
        runner = RecordingRunner()
        git(["status"], None, runner=runner, env={"ONLY": "this", "GIT_DIR": "x"})
        self.assertEqual(runner.calls[0][1]["env"], {"ONLY": "this", "GIT_TERMINAL_PROMPT": "0"})

    def test_nonzero_exit_raises_giterror_with_command_and_stderr(self):
        with self.assertRaises(GitError) as cm:
            git(["rev-parse", "--verify", "refs/heads/no-such-branch"], str(self.seed))
        text = str(cm.exception)
        self.assertIn("rev-parse --verify refs/heads/no-such-branch", text)
        self.assertIn("fatal", text.lower())

    def test_stdout_is_stripped(self):
        self.assertEqual(git(["rev-parse", "HEAD"], str(self.seed)), self.base_sha)

    def test_runner_oserror_and_timeout_become_giterror(self):
        with self.assertRaises(GitError) as cm:
            git(["status"], None, runner=RecordingRunner([FileNotFoundError("no git")]))
        self.assertIn("could not run", str(cm.exception))
        with self.assertRaises(GitError) as cm:
            git(["status"], None, runner=RecordingRunner([subprocess.TimeoutExpired(["git"], 3)]), timeout=3)
        self.assertIn("timed out after 3s", str(cm.exception))

    def test_commit_message_is_one_argv_element(self):
        runner = RecordingRunner([
            subprocess.CompletedProcess([], 0, stdout="", stderr=""),                 # add -A
            subprocess.CompletedProcess([], 0, stdout=" M a.txt\n", stderr=""),       # status --porcelain
            subprocess.CompletedProcess([], 0, stdout="", stderr=""),                 # commit
            subprocess.CompletedProcess([], 0, stdout="", stderr=""),                 # push
            subprocess.CompletedProcess([], 0, stdout="abc123\n", stderr=""),         # rev-parse HEAD
        ])
        message = "ctl: a message; with $(dangerous) `parts` && more\n\nBody"
        self.assertEqual(commit_and_push("/ws", "ctl/t1", message, runner=runner), "abc123")
        argvs = [c[0] for c in runner.calls]
        self.assertEqual(argvs[0], ["git", "add", "-A"])
        self.assertEqual(argvs[1], ["git", "status", "--porcelain"])
        self.assertEqual(argvs[2], ["git", "commit", "-q", "-m", message])
        self.assertEqual(argvs[3], ["git", "push", "-q", "-u", "origin", "ctl/t1"])
        self.assertTrue(all(c[1]["cwd"] == "/ws" for c in runner.calls))

    def test_clean_tree_makes_no_commit_and_no_push_calls(self):
        runner = RecordingRunner([
            subprocess.CompletedProcess([], 0, stdout="", stderr=""),   # add -A
            subprocess.CompletedProcess([], 0, stdout="\n", stderr=""),  # status --porcelain: empty
        ])
        self.assertIsNone(commit_and_push("/ws", "ctl/t1", "m", runner=runner))
        self.assertEqual([c[0][1] for c in runner.calls], ["add", "status"])


if __name__ == "__main__":
    unittest.main()
