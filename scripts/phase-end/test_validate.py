"""Unit tests for scripts/phase-end/validate.py. No test touches git, gh, bash or the network: every command
goes through FakeRunner, which answers from a table keyed by the exact argv and fails loudly otherwise.

Run: python -m unittest discover -s scripts/phase-end -p 'test_*.py'
(tests/scripts/phase-end-validate.test.sh runs this in the required shell-lint check, with a floor.)"""
import contextlib
import io
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import validate as v  # noqa: E402

SHA = "a" * 40
OLD = "b" * 40
REPO = "o/r"
NOW = 1791806400.0          # 2026-10-12T12:00:00Z, pinned (checked with `date -u -d ... +%s`), not computed
BASH = "/fake/bash"


class FakeRunner:
    def __init__(self, table=None):
        self.table = dict(table or {})
        self.calls = []

    def add(self, argv, rc=0, out="", err=""):
        self.table[tuple(argv)] = (rc, out, err)

    def __call__(self, argv, input=None):
        self.calls.append((tuple(argv), input))
        key = tuple(argv)
        if key not in self.table:
            return 127, "", f"FakeRunner has no answer for {key}"
        return self.table[key]


def verdict_line(sha, word):
    return f"readiness-verdict-for-sha: {sha} {word}\n"


class Base(unittest.TestCase):
    def setUp(self):
        self.r = FakeRunner()
        self.r.add([BASH, "-c", "echo bash-ok"], 0, "bash-ok\n")
        self.v = v.Validator(self.r, REPO, "py", BASH, NOW, stale_days=3)
        self.v.master_sha = SHA

    def gh(self, args, data=None, rc=0, err=""):
        self.r.add(["gh"] + list(args), rc, json.dumps(data) if data is not None else "", err)


class Time(unittest.TestCase):
    # Kills: the mktime-minus-timezone conversion, an hour off whenever the host is in daylight saving time.
    def test_iso_stamps_are_read_as_utc_in_summer_and_winter(self):
        self.assertEqual(v._iso_epoch("2026-07-01T12:00:00Z"), 1782907200.0)
        self.assertEqual(v._iso_epoch("2026-10-12T12:00:00Z"), NOW)


class RequiredChecks(Base):
    PROT = ["api", f"repos/{REPO}/branches/master/protection"]
    RUNS = ["gh", "api", "--paginate", f"repos/{REPO}/commits/{SHA}/check-runs", "--jq", ".check_runs[] | {name, status, conclusion}"]
    STATUS = ["api", f"repos/{REPO}/commits/{SHA}/status"]

    def setUp(self):
        super().setUp()
        self.gh(self.STATUS, {"statuses": []})

    def runs(self, *rows):
        self.r.add(self.RUNS, 0, "".join(json.dumps(x) + "\n" for x in rows))

    def protect(self, *names):
        self.gh(self.PROT, {"required_status_checks": {"contexts": list(names)}})

    # Kills: a red required check reported as anything but fail (`if red:` dropped or downgraded).
    def test_a_red_required_check_on_the_head_fails(self):
        self.protect("cert-gate", "build-core")
        self.runs({"name": "cert-gate", "status": "completed", "conclusion": "failure"},
                  {"name": "build-core", "status": "completed", "conclusion": "success"})
        c = self.v.required_checks()
        self.assertEqual(c["status"], "fail")
        self.assertIn("cert-gate (failure)", c["detail"])

    # Kills: the newest run of a name not winning (setdefault replaced by plain assignment: an older green run
    # listed after a newer red one would hide the red).
    def test_the_newest_run_of_a_name_decides(self):
        self.protect("cert-gate")
        self.runs({"name": "cert-gate", "status": "completed", "conclusion": "failure"},
                  {"name": "cert-gate", "status": "completed", "conclusion": "success"})
        self.assertEqual(self.v.required_checks()["status"], "fail")

    # Kills: an absent required check counted as red, or pending counted as green.
    def test_absent_is_noted_and_pending_warns(self):
        self.protect("cert-gate", "shell-lint")
        self.runs({"name": "cert-gate", "status": "completed", "conclusion": "success"})
        c = self.v.required_checks()
        self.assertEqual(c["status"], "pass")
        self.assertIn("not reported on the push", c["detail"])
        self.assertEqual(c["evidence"]["not_reported"], ["shell-lint"])
        self.runs({"name": "cert-gate", "status": "in_progress", "conclusion": None},
                  {"name": "shell-lint", "status": "completed", "conclusion": "skipped"})
        c = self.v.required_checks()
        self.assertEqual((c["status"], c["evidence"]["pending"]), ("warn", ["cert-gate"]))

    # Kills: reading only check runs (a required context posted through the Statuses API would read as absent).
    def test_a_red_commit_status_fails(self):
        self.protect("cert-gate", "ext-ci")
        self.runs({"name": "cert-gate", "status": "completed", "conclusion": "success"})
        self.gh(self.STATUS, {"statuses": [{"context": "ext-ci", "state": "failure"}]})
        c = self.v.required_checks()
        self.assertEqual(c["status"], "fail")
        self.assertIn("ext-ci (failure)", c["detail"])
        self.gh(self.STATUS, {"statuses": [{"context": "ext-ci", "state": "pending"}]})
        self.assertEqual(self.v.required_checks()["evidence"]["pending"], ["ext-ci"])

    # Kills: a gh failure reported as pass.
    def test_unreadable_protection_or_statuses_is_an_error(self):
        self.gh(self.PROT, rc=1, err="HTTP 403")
        c = self.v.required_checks()
        self.assertEqual(c["status"], "error")
        self.assertIn("403", c["detail"])
        self.protect("cert-gate")
        self.runs({"name": "cert-gate", "status": "completed", "conclusion": "success"})
        self.gh(self.STATUS, rc=1, err="HTTP 502")
        self.assertEqual(self.v.required_checks()["status"], "error")


class MasterWorkflows(Base):
    LIST = ["workflow", "list", "--all", "--limit", "500", "--json", "id,name,path,state"]
    TREE = ["git", "ls-tree", "--name-only", "origin/master", ".github/workflows/"]
    BULK = ["run", "list", "--branch", "master", "--limit", "500",
            "--json", "workflowDatabaseId,conclusion,status,createdAt,headSha,event,databaseId"]

    def setUp(self):
        super().setUp()
        self.r.add(self.TREE, 0, "\n".join([".github/workflows/compat-gate.yml", ".github/workflows/cert-gate.yml",
                                            ".github/workflows/dormant.yml", ".github/workflows/a.yml"]) + "\n")
        self.gh(self.BULK, [])

    def newest(self, wid, rows):
        self.gh(["run", "list", "--workflow", str(wid), "--branch", "master", "--limit", "1",
                 "--json", "conclusion,status,createdAt,headSha,event,databaseId"], rows)

    def per_workflow_calls(self, wid):
        return [c for c, _ in self.r.calls if c[:4] == ("gh", "run", "list", "--workflow") and c[4] == str(wid)]

    # Kills: a red newest run not reported, and a rarely run gate (asked per workflow) being missed.
    def test_a_workflow_whose_newest_master_run_is_red_warns(self):
        self.gh(self.LIST, [{"id": 1, "name": "compat-gate", "path": ".github/workflows/compat-gate.yml", "state": "active"},
                            {"id": 2, "name": "Cert gate", "path": ".github/workflows/cert-gate.yml", "state": "active"},
                            {"id": 3, "name": "Dormant", "path": ".github/workflows/dormant.yml", "state": "active"}])
        self.newest(1, [{"conclusion": "failure", "status": "completed", "createdAt": "2026-09-30T20:48:51Z",
                         "headSha": OLD, "event": "push", "databaseId": 9}])
        self.newest(2, [{"conclusion": "success", "status": "completed", "createdAt": "2026-10-12T04:00:00Z",
                         "headSha": SHA, "event": "push", "databaseId": 10}])
        self.newest(3, [])
        c = self.v.master_workflows()
        self.assertEqual(c["status"], "warn")
        self.assertEqual([x["workflow"] for x in c["evidence"]["red"]], ["compat-gate"])
        self.assertIn("compat-gate (failure, 2026-09-30)", c["detail"])
        self.assertEqual(c["evidence"]["never_on_master"], ["Dormant"])

    # Kills: the bulk read being ignored (every workflow asked one by one) or its newest run not winning.
    def test_the_bulk_read_answers_without_a_per_workflow_call(self):
        self.gh(self.LIST, [{"id": 1, "name": "compat-gate", "path": ".github/workflows/compat-gate.yml", "state": "active"}])
        self.gh(self.BULK, [{"workflowDatabaseId": 1, "conclusion": "success", "status": "completed",
                             "createdAt": "2026-09-01T00:00:00Z", "headSha": OLD, "event": "push", "databaseId": 1},
                            {"workflowDatabaseId": 1, "conclusion": "failure", "status": "completed",
                             "createdAt": "2026-09-30T00:00:00Z", "headSha": SHA, "event": "push", "databaseId": 2}])
        c = self.v.master_workflows()
        self.assertEqual([x["run"] for x in c["evidence"]["red"]], [2])
        self.assertEqual(self.per_workflow_calls(1), [])

    # Kills: a disabled workflow's last red run reported as a live red gate.
    def test_a_disabled_workflow_is_not_reported_red(self):
        self.gh(self.LIST, [{"id": 1, "name": "Lab", "path": ".github/workflows/dormant.yml", "state": "disabled_manually"}])
        self.gh(self.BULK, [{"workflowDatabaseId": 1, "conclusion": "failure", "status": "completed",
                             "createdAt": "2026-09-30T00:00:00Z", "headSha": SHA, "event": "push", "databaseId": 2}])
        c = self.v.master_workflows()
        self.assertEqual((c["status"], c["evidence"]["red"], c["evidence"]["not_active"]), ("pass", [], ["Lab"]))

    # Kills: a workflow whose runs cannot be read being skipped silently.
    def test_an_unreadable_workflow_is_an_error(self):
        self.gh(self.LIST, [{"id": 1, "name": "A", "path": ".github/workflows/a.yml", "state": "active"}])
        self.gh(["run", "list", "--workflow", "1", "--branch", "master", "--limit", "1",
                 "--json", "conclusion,status,createdAt,headSha,event,databaseId"], rc=1, err="HTTP 502")
        self.assertEqual(self.v.master_workflows()["status"], "error")

    # Kills: counting workflows whose file is gone from master (GitHub still lists them, and Dependabot's
    # dynamic workflow): a deleted workflow's last red run would read as a live red gate.
    def test_a_workflow_deleted_from_master_is_not_counted_or_reported_red(self):
        self.gh(self.LIST, [{"id": 1, "name": "compat-gate", "path": ".github/workflows/compat-gate.yml", "state": "active"},
                            {"id": 7, "name": "Old gate", "path": ".github/workflows/old-gate.yml", "state": "active"},
                            {"id": 8, "name": "Dependabot Updates", "path": "dynamic/dependabot/dependabot-updates", "state": "active"}])
        self.newest(1, [{"conclusion": "success", "status": "completed", "createdAt": "2026-10-12T00:00:00Z",
                         "headSha": SHA, "event": "push", "databaseId": 1}])
        self.newest(7, [{"conclusion": "failure", "status": "completed", "createdAt": "2026-08-01T00:00:00Z",
                         "headSha": OLD, "event": "push", "databaseId": 2}])
        c = self.v.master_workflows()
        self.assertEqual((c["status"], c["evidence"]["workflows"], c["evidence"]["red"]), ("pass", 1, []))
        self.assertEqual(c["evidence"]["listed_but_not_on_master"],
                         ["Dependabot Updates (dynamic/dependabot/dependabot-updates)", "Old gate (.github/workflows/old-gate.yml)"])
        self.assertIn("1 workflows on master", c["detail"])

    def test_all_green_passes(self):
        self.gh(self.LIST, [{"id": 1, "name": "A", "path": ".github/workflows/a.yml", "state": "active"}])
        self.newest(1, [{"conclusion": "success", "status": "completed", "createdAt": "2026-10-12T00:00:00Z",
                         "headSha": SHA, "event": "push", "databaseId": 1}])
        self.assertEqual(self.v.master_workflows()["status"], "pass")


class Readiness(Base):
    RUNS = ["run", "list", "--workflow", v.READINESS_WORKFLOW, "--branch", "master", "--limit", "20",
            "--json", "headSha,conclusion,event,createdAt"]

    def verdict(self, sha, rc, text):
        self.r.add([BASH, "scripts/release/readiness-verdict-for-sha.sh", sha], rc, text)

    # Kills: VERIFIED read from anything but the verdict line with exit 0.
    def test_verified_head_passes_and_refused_warns(self):
        self.verdict(SHA, 0, verdict_line(SHA, "VERIFIED"))
        self.assertEqual(self.v.readiness_head()["status"], "pass")
        self.verdict(SHA, 1, "looking up runs\n" + verdict_line(SHA, "REFUSED (no-readiness-run)"))
        c = self.v.readiness_head()
        self.assertEqual(c["status"], "warn")
        self.assertIn("REFUSED (no-readiness-run)", c["detail"])

    # Kills: the observed Windows failure (WSL's bash.exe ran instead of Git Bash, exit 1, no verdict line) being
    # read as a REFUSED verdict. Without a verdict line it is an error.
    def test_no_verdict_line_is_an_error_not_a_refusal(self):
        self.verdict(SHA, 1, "<3>WSL (13 - Relay) ERROR: CreateProcessCommon:818: execvpe(/bin/bash) failed\n")
        c = self.v.readiness_head()
        self.assertEqual(c["status"], "error")
        self.assertIn("no verdict line", c["detail"])

    # Kills: the bash probe being skipped (a broken bash would reach the verdict script).
    def test_a_broken_bash_is_an_error_before_any_script_runs(self):
        self.r.add([BASH, "-c", "echo bash-ok"], 1, "", "execvpe(/bin/bash) failed")
        self.assertEqual(self.v.readiness_head()["status"], "error")
        self.assertEqual(self.v.local("docs-paths", "Docs", [BASH, "scripts/check-docs-phantom-paths.sh"],
                                      needs_bash=True)["status"], "error")
        self.assertNotIn(((BASH, "scripts/release/readiness-verdict-for-sha.sh", SHA), None), self.r.calls)

    # Kills: the first successful run being taken as verified without asking the verdict script, and the
    # commits-since count.
    def test_last_verified_skips_refused_runs_and_counts_commits_since(self):
        self.gh(self.RUNS, [{"headSha": SHA, "conclusion": "success", "event": "push", "createdAt": "2026-10-11T00:00:00Z"},
                            {"headSha": "c" * 40, "conclusion": "failure", "event": "push", "createdAt": "2026-10-10T00:00:00Z"},
                            {"headSha": OLD, "conclusion": "success", "event": "schedule", "createdAt": "2026-10-05T00:00:00Z"}])
        self.verdict(SHA, 1, verdict_line(SHA, "REFUSED (partial)"))
        self.verdict(OLD, 0, verdict_line(OLD, "VERIFIED"))
        self.r.add(["git", "rev-list", "--count", f"{OLD}..origin/master"], 0, "7\n")
        c = self.v.readiness_last_verified()
        self.assertEqual((c["status"], c["evidence"]["sha"], c["evidence"]["commits_since"]), ("info", OLD, 7))

    # Kills: stopping at the first unreadable verdict (a verified commit further back would be lost).
    def test_an_unreadable_verdict_does_not_hide_an_older_verified_one(self):
        self.gh(self.RUNS, [{"headSha": SHA, "conclusion": "success", "event": "push", "createdAt": "2026-10-11T00:00:00Z"},
                            {"headSha": OLD, "conclusion": "success", "event": "schedule", "createdAt": "2026-10-05T00:00:00Z"}])
        self.verdict(SHA, 124, "timed out\n")
        self.verdict(OLD, 0, verdict_line(OLD, "VERIFIED"))
        self.r.add(["git", "rev-list", "--count", f"{OLD}..origin/master"], 0, "2\n")
        self.assertEqual(self.v.readiness_last_verified()["evidence"]["sha"], OLD)
        self.verdict(OLD, 124, "timed out\n")
        c = self.v.readiness_last_verified()
        self.assertEqual(c["status"], "error")
        self.assertIn("could not be read", c["detail"])

    def test_no_verified_run_warns(self):
        self.gh(self.RUNS, [{"headSha": SHA, "conclusion": "success", "event": "push", "createdAt": "2026-10-11T00:00:00Z"}])
        self.verdict(SHA, 1, verdict_line(SHA, "REFUSED (partial)"))
        self.assertEqual(self.v.readiness_last_verified()["status"], "warn")


class PullRequestsAndSecurity(Base):
    PRS = ["pr", "list", "--state", "open", "--limit", "200", "--json", "number,title,mergeStateStatus,updatedAt,isDraft,headRefName"]

    # Kills: the stale test comparing the wrong way, or the stale-days threshold being ignored.
    def test_stale_prs_warn_and_fresh_ones_are_info(self):
        self.gh(self.PRS, [{"number": 750, "mergeStateStatus": "DIRTY", "updatedAt": "2026-10-08T00:00:00Z"},
                           {"number": 781, "mergeStateStatus": "BEHIND", "updatedAt": "2026-10-12T10:00:00Z"}])
        c = self.v.open_prs()
        self.assertEqual((c["status"], c["evidence"]["stale"]), ("warn", [750]))
        self.assertEqual(c["evidence"]["by_merge_state"], {"DIRTY": 1, "BEHIND": 1})
        self.gh(self.PRS, [{"number": 781, "mergeStateStatus": "BEHIND", "updatedAt": "2026-10-12T10:00:00Z"}])
        self.assertEqual(self.v.open_prs()["status"], "info")

    # Kills: a public repository with secret scanning off reported as plain info.
    def test_public_repo_without_secret_scanning_warns(self):
        sa = {"secret_scanning": {"status": "disabled"}, "dependabot_security_updates": {"status": "disabled"}}
        self.gh(["api", f"repos/{REPO}"], {"visibility": "public", "security_and_analysis": sa})
        c = self.v.repo_security()
        self.assertEqual(c["status"], "warn")
        self.assertIn("secret_scanning", c["detail"])
        self.gh(["api", f"repos/{REPO}"], {"visibility": "private", "security_and_analysis": sa})
        self.assertEqual(self.v.repo_security()["status"], "info")

    # Kills: a token that cannot see the settings (not an admin) reading as "disabled: none".
    def test_settings_not_visible_to_the_token_is_an_error(self):
        self.gh(["api", f"repos/{REPO}"], {"visibility": "public"})
        c = self.v.repo_security()
        self.assertEqual(c["status"], "error")
        self.assertIn("not visible", c["detail"])


class Stranded(Base):
    REFS = ["git", "for-each-ref", "--format=%(refname:short)\t%(upstream:short)\t%(upstream:track)", "refs/heads"]
    NOMERGED = ["git", "branch", "--format=%(refname:short)", "--no-merged", "origin/master"]
    LOG_P = ["git", "log", "-p", "--no-merges", "--since=180.days", "origin/master"]
    NAMED = ["git", "log", "--format=%x00%s", "--name-only", "--since=180.days", "origin/master"]

    def setUp(self):
        super().setUp()
        self.r.add(self.LOG_P, 0, "")
        # Two master commits: "fix the gate (#12)" touched a.txt; "wip" touched other.txt.
        self.r.add(self.NAMED, 0, "\x00fix the gate (#12)\n\na.txt\n\x00wip\n\nother.txt\n")

    def branch(self, name, remote=False, subject="wip", files="a.txt\n", same_content=False):
        self.r.add(["git", "branch", "-r", "--contains", name, "--format=%(refname:short)"], 0,
                   "origin/" + name + "\n" if remote else "")
        self.r.add(["git", "log", "-1", "--format=%s", name], 0, subject + "\n")
        self.r.add(["git", "merge-base", "origin/master", name], 0, "base\n")
        self.r.add(["git", "diff", "--name-only", "base", name], 0, files)
        self.r.add(["git", "diff", "--quiet", "origin/master", name, "--"] + [f for f in files.split() if f],
                   0 if same_content else 1, "")
        self.r.add(["git", "diff", "base", name], 0, "diff --git a/a.txt b/a.txt\n+x\n")
        self.r.add(["git", "patch-id", "--stable"], 0, "")
        self.r.add(["git", "rev-list", "--count", f"origin/master..{name}"], 0, "3\n")
        self.r.add(["git", "log", "-1", "--format=%cs", name], 0, "2026-10-01\n")

    # Kills: each "landed" test (remote containment, a squash subject that touched the same files, identical
    # content), a bare subject match hiding work, an [ahead N] branch being skipped, and the warn itself.
    def test_only_work_that_is_nowhere_else_is_stranded(self):
        self.r.add(self.REFS, 0, "on-remote\t\t\nsquashed\t\t\nsame\t\t\nlost\t\t\ngeneric\t\t\n"
                                 "gone\torigin/gone\t[gone]\nahead\torigin/ahead\t[ahead 2]\ntracked\torigin/tracked\t\n")
        self.r.add(self.NOMERGED, 0, "on-remote\nsquashed\nsame\nlost\ngeneric\ngone\nahead\ntracked\n")
        self.branch("on-remote", remote=True)
        self.branch("squashed", subject="Fix the gate")
        self.branch("same", same_content=True)
        self.branch("lost", subject="something new")
        self.branch("generic", subject="wip")            # master's "wip" touched other.txt, not a.txt
        self.branch("gone", subject="something new")
        self.branch("ahead", subject="something new")
        c = self.v.stranded_branches()
        self.assertEqual(c["status"], "warn")
        self.assertEqual([s["branch"] for s in c["evidence"]["stranded"]], ["ahead", "generic", "gone", "lost"])
        self.assertEqual(c["evidence"]["stranded"][3], {"branch": "lost", "ahead": 3, "tip_date": "2026-10-01"})
        self.assertNotIn("tracked", json.dumps(c["evidence"]))

    def test_nothing_stranded_passes(self):
        self.r.add(self.REFS, 0, "main\torigin/main\t\n")
        self.assertEqual(self.v.stranded_branches()["status"], "pass")

    def test_normalize_subject_strips_squash_suffixes(self):
        self.assertEqual(v.normalize_subject("Fix  the gate (#12) (#13)"), "fix the gate")


class LocalChecksAndExit(Base):
    # Kills: a failing local script reported as pass, and a missing one reported as fail rather than error.
    def test_local_check_statuses(self):
        self.r.add(["py", "kg.py"], 0, "knowledge-graph: current\n")
        self.assertEqual(self.v.local("kg", "KG", ["py", "kg.py"])["status"], "pass")
        self.r.add(["py", "kg.py"], 1, "", "knowledge-graph: 2 check(s) failed\n")
        c = self.v.local("kg", "KG", ["py", "kg.py"])
        self.assertEqual(c["status"], "fail")
        self.assertIn("2 check(s) failed", c["detail"])
        self.assertEqual(self.v.local("x", "X", ["missing"])["status"], "error")

    # Kills: exit_code ignoring error, or treating warn as failure.
    def test_exit_code(self):
        mk = lambda s: v.check("x", "X", s, "")
        self.assertEqual(v.exit_code([mk("pass"), mk("warn"), mk("info")]), 0)
        self.assertEqual(v.exit_code([mk("pass"), mk("fail")]), 1)
        self.assertEqual(v.exit_code([mk("pass"), mk("error")]), 1)

    def test_unknown_status_is_refused(self):
        with self.assertRaises(ValueError):
            v.check("x", "X", "ok", "")

    def test_unreleased_counts_and_missing_tag_errors(self):
        self.r.add(["git", "describe", "--tags", "--abbrev=0", "--match", "v*", "origin/master"], 0, "v0.2.0\n")
        self.r.add(["git", "rev-list", "--count", "v0.2.0..origin/master"], 0, "89\n")
        c = self.v.unreleased()
        self.assertEqual((c["status"], c["evidence"]["commits"], c["evidence"]["tag"]), ("info", 89, "v0.2.0"))
        self.r.add(["git", "describe", "--tags", "--abbrev=0", "--match", "v*", "origin/master"], 128, "", "fatal: No names found")
        self.assertEqual(self.v.unreleased()["status"], "error")


class FindBash(unittest.TestCase):
    # Kills: WSL's System32 bash.exe being chosen on Windows (observed: execvpe(/bin/bash) failed).
    def test_windows_prefers_git_bash_beside_git(self):
        git = os.path.join("C:\\", "Git", "cmd", "git.exe")
        want = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(git))), "bin", "bash.exe")
        which = {"git": git, "bash": "C:\\Windows\\System32\\bash.exe"}.get
        self.assertEqual(v.find_bash(env={}, which=which, isfile=lambda p: p == want, windows=True), want)

    # Kills: looking only one level above git, which misses Git Bash when git resolves to <Git>\mingw64\bin.
    def test_windows_finds_git_bash_when_git_is_the_mingw64_copy(self):
        git = os.path.join("C:\\", "Git", "mingw64", "bin", "git.exe")
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(git))))
        want = os.path.join(root, "bin", "bash.exe")
        which = {"git": git, "bash": "C:\\Windows\\System32\\bash.exe"}.get
        self.assertEqual(v.find_bash(env={}, which=which, isfile=lambda p: p == want, windows=True), want)

    def test_windows_never_picks_system32_bash(self):
        which = {"bash": "C:\\Windows\\System32\\bash.exe"}.get
        self.assertEqual(v.find_bash(env={}, which=which, isfile=lambda p: False, windows=True), "bash")

    def test_override_and_posix(self):
        self.assertEqual(v.find_bash(env={"ASHLAR_BASH": "/x/bash"}, windows=True), "/x/bash")
        self.assertEqual(v.find_bash(env={}, windows=False), "bash")


class Main(unittest.TestCase):
    # Kills: main not returning the computed exit code, the files not written, or Markdown pipes unescaped.
    def test_main_writes_json_and_markdown_and_returns_the_exit_code(self):
        r = FakeRunner()
        r.add([BASH, "-c", "echo bash-ok"], 1, "")      # every bash-dependent check errors; the rest have no answer
        with tempfile.TemporaryDirectory() as d:
            js, md = os.path.join(d, "v.json"), os.path.join(d, "v.md")
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                rc = v.main(["--no-fetch", "--json", js, "--markdown", md, "--workstream", "w", "--phase", "E"],
                            runner=r, now=NOW, bash=BASH)
            self.assertEqual(rc, 1)
            with open(js, encoding="utf-8") as f:
                data = json.load(f)
            self.assertEqual((data["schema_version"], data["workstream"], data["phase"], data["exit_code"]), (1, "w", "E", 1))
            self.assertEqual(sum(data["summary"].values()), len(data["checks"]))
            self.assertTrue(all(c["status"] in v.STATUSES for c in data["checks"]))
            self.assertGreaterEqual(len(data["checks"]), 12)
            with open(md, encoding="utf-8") as f:
                text = f.read()
            self.assertEqual(text, out.getvalue())
            self.assertIn("| Check | Status | Detail |", text)
            self.assertIn("error", text)

    def test_markdown_escapes_pipes(self):
        rep = v.report([v.check("x", "X", "warn", "a | b")], REPO, None, None, SHA, NOW)
        self.assertIn("a \\| b", v.render_markdown(rep))


if __name__ == "__main__":
    unittest.main()
