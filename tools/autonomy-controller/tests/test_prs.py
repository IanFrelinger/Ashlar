"""The GitHub side of the PR lifecycle (ashlar_controller.prs). GitHub is always FakeGh; the real GhCli is
exercised only through a recording fake runner that returns the documented gh output shapes."""
import json
import subprocess
import unittest

from ashlar_controller import prs
from ashlar_controller.prs import (CheckRun, CheckVerdict, FakeGh, GhCli, GhError, MergeRefused, PrView,
                                   REQUIRED_CHECKS_DEFAULT, compact_handoff, evaluate_checks)
from ashlar_controller.store import utcnow_iso

from .helpers import ControllerCase

FIVE = REQUIRED_CHECKS_DEFAULT
REPO = "IanFrelinger/Ashlar"


def completed(names, conclusion="success", **kw):
    return [CheckRun(name=n, status="completed", conclusion=conclusion, **kw) for n in names]


def seed_task(store, task_id="t1", card=755, now=None):
    """A tasks row (and its pool) by raw SQL: prs.task_id has a foreign key and foreign_keys is ON."""
    at = utcnow_iso(now)
    pool = f"pool:{task_id}"
    with store.tx():
        store.conn.execute(
            "INSERT INTO budget_pools(pool_id, unit, task_ceiling, invocation_ceiling, attempts_max, created_at) "
            "VALUES (?,?,?,?,?,?)", (pool, "turns", 100, 20, 3, at))
        store.conn.execute(
            "INSERT INTO tasks(task_id, card_number, workstream, title, handoff_ref, provider, pool_id, state, "
            "created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?)",
            (task_id, card, "budgeted-autonomy", f"PR for {task_id}", "handoff:phase-c", "fake", pool, "queued", at, at))
    return store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))


class EvaluateChecks(unittest.TestCase):
    def test_all_five_success_is_green(self):
        v = evaluate_checks(FIVE, "aaa", completed(FIVE))
        self.assertEqual(v.verdict, "green")
        self.assertEqual(v.green, list(FIVE))
        self.assertEqual((v.red, v.pending), ([], []))
        self.assertEqual(v.head_sha, "aaa")

    def test_one_failure_is_red_with_the_identity(self):
        runs = completed(FIVE[1:]) + [CheckRun(name="cert-gate", status="completed", conclusion="failure",
                                               title="3 failed", summary="FAIL test_a\nFAIL test_b",
                                               details_url="https://x/run/1")]
        v = evaluate_checks(FIVE, "aaa", runs)
        self.assertEqual(v.verdict, "red")
        self.assertEqual([f.name for f in v.red], ["cert-gate"])
        f = v.red[0]
        self.assertEqual(f.conclusion, "failure")
        self.assertTrue(f.identity.startswith("gate:check:cert-gate:"), f.identity)
        self.assertIn("FAIL test_a", f.evidence)
        self.assertEqual(f.details_url, "https://x/run/1")
        self.assertEqual(len(v.green), 4)

    def test_missing_run_is_pending_even_when_the_other_four_are_green(self):
        v = evaluate_checks(FIVE, "aaa", completed(FIVE[:4]))
        self.assertEqual(v.verdict, "pending")
        self.assertEqual(v.pending, ["Readiness summary"])
        self.assertEqual(len(v.green), 4)

    def test_in_progress_run_is_pending(self):
        runs = completed(FIVE[1:]) + [CheckRun(name="cert-gate", status="in_progress", conclusion=None)]
        v = evaluate_checks(FIVE, "aaa", runs)
        self.assertEqual(v.verdict, "pending")
        self.assertEqual(v.pending, ["cert-gate"])

    def test_neutral_is_pending_not_green(self):
        runs = completed(FIVE[1:]) + completed(FIVE[:1], conclusion="neutral")
        self.assertEqual(evaluate_checks(FIVE, "aaa", runs).verdict, "pending")

    def test_red_beats_pending(self):
        runs = completed(FIVE[2:]) + completed(FIVE[:1], conclusion="timed_out")   # FIVE[1] has no run
        v = evaluate_checks(FIVE, "aaa", runs)
        self.assertEqual(v.verdict, "red")
        self.assertEqual(v.pending, ["build-core"])
        self.assertEqual(v.red[0].conclusion, "timed_out")

    def test_every_red_conclusion_is_red(self):
        for conclusion in prs.RED_CONCLUSIONS:
            runs = completed(FIVE[1:]) + completed(FIVE[:1], conclusion=conclusion)
            self.assertEqual(evaluate_checks(FIVE, "aaa", runs).verdict, "red", conclusion)

    def test_skipped_counts_green(self):
        runs = completed(FIVE[1:]) + completed(FIVE[:1], conclusion="skipped")
        v = evaluate_checks(FIVE, "aaa", runs)
        self.assertEqual(v.verdict, "green")
        self.assertIn("cert-gate", v.green)

    def test_a_run_for_a_name_that_is_not_required_is_ignored(self):
        runs = completed(FIVE) + completed(["some-other-lane"], conclusion="failure")
        v = evaluate_checks(FIVE, "aaa", runs)
        self.assertEqual(v.verdict, "green")
        self.assertEqual(v.red, [])

    def test_latest_run_of_a_name_wins(self):
        runs = completed(FIVE[1:]) + completed(FIVE[:1], conclusion="failure") + completed(FIVE[:1])
        self.assertEqual(evaluate_checks(FIVE, "aaa", runs).verdict, "green")
        runs = completed(FIVE[1:]) + completed(FIVE[:1]) + completed(FIVE[:1], conclusion="failure")
        self.assertEqual(evaluate_checks(FIVE, "aaa", runs).verdict, "red")

    def test_identity_is_equal_across_heads_with_the_same_summary_and_differs_otherwise(self):
        def red(sha, summary):
            runs = completed(FIVE[1:]) + [CheckRun(name="cert-gate", status="completed", conclusion="failure",
                                                   title="failed", summary=summary)]
            return evaluate_checks(FIVE, sha, runs).red[0].identity
        same_a = red("aaaaaaa1", "FAIL tests/test_x.py::test_one\n[12 ms] done at 2026-10-11T10:00:00Z")
        same_b = red("bbbbbbb2", "FAIL tests/test_x.py::test_one\n[48 ms] done at 2026-10-11T11:30:00Z")
        other = red("aaaaaaa1", "FAIL tests/test_y.py::test_two")
        self.assertEqual(same_a, same_b)
        self.assertNotEqual(same_a, other)

    def test_identity_depends_on_the_check_name(self):
        a = evaluate_checks(FIVE, "s", completed(FIVE[1:]) + completed(FIVE[:1], conclusion="failure", summary="x")).red[0]
        b = evaluate_checks(FIVE, "s", completed(FIVE[2:]) + completed(FIVE[:1]) +
                            completed(FIVE[1:2], conclusion="failure", summary="x")).red[0]
        self.assertNotEqual(a.identity, b.identity)
        self.assertIn("check:cert-gate", a.identity)
        self.assertIn("check:build-core", b.identity)


class OldGreenCannotSatisfyAChangedHead(unittest.TestCase):
    def test_green_on_a_does_not_merge_b(self):
        gh = FakeGh()
        gh.branches["auto/t1"] = "a" * 40
        pr = gh.pr_create(REPO, "auto/t1", "master", "t", "b")
        gh.set_checks("a" * 40, {n: "success" for n in FIVE})
        self.assertEqual(evaluate_checks(FIVE, pr.head_sha, gh.check_runs(REPO, pr.head_sha)).verdict, "green")
        gh.push_head(pr.number, "b" * 40)
        self.assertEqual(gh.pr_view(REPO, pr.number).head_sha, "b" * 40)
        on_b = evaluate_checks(FIVE, "b" * 40, gh.check_runs(REPO, "b" * 40))
        self.assertEqual(on_b.verdict, "pending")
        self.assertEqual(on_b.pending, list(FIVE))
        with self.assertRaises(MergeRefused):
            gh.pr_merge(REPO, pr.number, "a" * 40)
        self.assertEqual(gh.pr_view(REPO, pr.number).state, "OPEN")
        self.assertIsNone(gh.pr_view(REPO, pr.number).merge_sha)
        self.assertEqual(gh.merge_count, 1)

    def test_merge_at_the_current_head_succeeds_with_the_documented_merge_sha(self):
        gh = FakeGh()
        gh.branches["auto/t1"] = "c" * 40
        pr = gh.pr_create(REPO, "auto/t1", "master", "t", "b")
        gh.pr_merge(REPO, pr.number, "c" * 40)
        view = gh.pr_view(REPO, pr.number)
        self.assertEqual(view.state, "MERGED")
        self.assertEqual(view.merge_sha, f"merge-{pr.number}-{'c' * 7}")
        with self.assertRaises(MergeRefused):
            gh.pr_merge(REPO, pr.number, "c" * 40)   # not OPEN any more

    def test_fake_refuses_a_second_open_pr_for_the_same_branch(self):
        gh = FakeGh()
        gh.pr_create(REPO, "auto/t1", "master", "t", "b")
        with self.assertRaises(GhError):
            gh.pr_create(REPO, "auto/t1", "master", "t", "b")
        self.assertEqual(len(gh.prs), 1)
        gh.close(1)
        self.assertEqual(gh.pr_create(REPO, "auto/t1", "master", "t", "b").number, 2)


class PrRows(ControllerCase):
    def setUp(self):
        super().setUp()
        self.s = self.init_store()
        self.gh = FakeGh()
        self.gh.branches["auto/t1"] = "h" * 40
        self.task = seed_task(self.s, "t1", now=self.clock())

    def open(self, task_id="t1", branch="auto/t1", base_sha="b" * 40):
        return prs.open_or_reuse_pr(self.s, self.gh, REPO, task_id, branch, "master", base_sha, "title", "body",
                                    now=self.clock())

    def journal_kinds(self):
        return [r["kind"] for r in self.s.rows("SELECT kind FROM journal WHERE kind LIKE 'pr-%' ORDER BY id")]

    def test_three_calls_make_one_row_and_one_create(self):
        rows = [self.open() for _ in range(3)]
        self.assertEqual({r["pr_number"] for r in rows}, {1})
        self.assertEqual(self.gh.create_count, 1)
        self.assertEqual(self.gh.for_branch_count, 1)
        self.assertEqual(self.s.row("SELECT COUNT(*) AS n FROM prs")["n"], 1)
        row = rows[0]
        self.assertEqual((row["task_id"], row["state"], row["outcome"]), ("t1", "open", None))
        self.assertEqual((row["head_sha"], row["base_sha"], row["branch"]), ("h" * 40, "b" * 40, "auto/t1"))
        self.assertEqual(row["url"], f"https://github.com/{REPO}/pull/1")
        self.assertEqual(row["title"], "title")
        self.assertEqual(self.journal_kinds(), ["pr-opened"])

    def test_a_pre_existing_open_pr_for_the_branch_is_adopted_with_zero_creates(self):
        existing = self.gh.pr_create(REPO, "auto/t1", "master", "opened by hand", "x")
        self.gh.create_count = 0
        row = self.open()
        self.assertEqual(row["pr_number"], existing.number)
        self.assertEqual(self.gh.create_count, 0)
        self.assertEqual(row["title"], "opened by hand")
        self.assertEqual(self.journal_kinds(), ["pr-adopted"])
        self.assertEqual(json.loads(self.s.row("SELECT detail FROM journal WHERE kind='pr-adopted'")["detail"])
                         ["pr_number"], existing.number)

    def test_a_second_task_cannot_reuse_the_first_tasks_row(self):
        seed_task(self.s, "t2", card=756, now=self.clock())
        self.gh.branches["auto/t2"] = "i" * 40
        r1 = self.open("t1", "auto/t1")
        r2 = self.open("t2", "auto/t2")
        self.assertNotEqual(r1["pr_number"], r2["pr_number"])
        self.assertEqual(r2["task_id"], "t2")
        self.assertEqual(self.gh.create_count, 2)
        self.assertEqual(prs.open_pr_for_task(self.s, "t2")["pr_number"], r2["pr_number"])

    def test_open_pr_for_task_ignores_closed_and_merged_rows(self):
        row = self.open()
        prs.record_closed(self.s, row["pr_number"], now=self.clock())
        self.assertIsNone(prs.open_pr_for_task(self.s, "t1"))
        self.gh.close(row["pr_number"])
        again = self.open()
        self.assertNotEqual(again["pr_number"], row["pr_number"])
        self.assertEqual(self.gh.create_count, 2)

    def test_record_head_journals_only_on_change(self):
        row = self.open()
        self.assertFalse(prs.record_head(self.s, row["pr_number"], "h" * 40, now=self.clock()))
        self.assertEqual(self.journal_kinds(), ["pr-opened"])
        self.clock.advance(10)
        self.assertTrue(prs.record_head(self.s, row["pr_number"], "j" * 40, now=self.clock()))
        self.assertEqual(self.s.row("SELECT head_sha FROM prs")["head_sha"], "j" * 40)
        self.assertEqual(self.journal_kinds(), ["pr-opened", "pr-head"])
        detail = json.loads(self.s.row("SELECT detail FROM journal WHERE kind='pr-head'")["detail"])
        self.assertEqual((detail["from"], detail["to"]), ("h" * 40, "j" * 40))

    def test_record_merged(self):
        row = self.open()
        prs.record_merged(self.s, row["pr_number"], "m" * 40, now=self.clock())
        r = self.s.row("SELECT * FROM prs")
        self.assertEqual((r["state"], r["outcome"], r["merge_sha"]), ("merged", "merged", "m" * 40))
        self.assertEqual(self.journal_kinds(), ["pr-opened", "pr-merged"])
        self.assertIsNone(prs.open_pr_for_task(self.s, "t1"))

    def test_record_closed_is_never_a_success(self):
        row = self.open()
        prs.record_closed(self.s, row["pr_number"], now=self.clock())
        r = self.s.row("SELECT * FROM prs")
        self.assertEqual((r["state"], r["outcome"], r["merge_sha"]), ("closed", "closed-unmerged", None))
        self.assertEqual(self.journal_kinds(), ["pr-opened", "pr-closed-unmerged"])

    def test_bump_repair_round_increments(self):
        row = self.open()
        self.assertEqual(prs.bump_repair_round(self.s, row["pr_number"], now=self.clock()), 1)
        self.assertEqual(prs.bump_repair_round(self.s, row["pr_number"], now=self.clock()), 2)
        self.assertEqual(self.s.row("SELECT repair_rounds_used FROM prs")["repair_rounds_used"], 2)
        with self.assertRaises(KeyError):
            prs.bump_repair_round(self.s, 999)

    def test_latest_merge_sha_orders_by_updated_at_then_rowid(self):
        self.assertIsNone(prs.latest_merge_sha(self.s))
        seed_task(self.s, "t2", card=756, now=self.clock())
        seed_task(self.s, "t3", card=757, now=self.clock())
        self.gh.branches["auto/t2"] = "i" * 40
        self.gh.branches["auto/t3"] = "k" * 40
        r1 = self.open("t1", "auto/t1")
        r2 = self.open("t2", "auto/t2")
        r3 = self.open("t3", "auto/t3")
        prs.record_merged(self.s, r1["pr_number"], "m1", now=self.clock())
        self.clock.advance(60)
        prs.record_merged(self.s, r2["pr_number"], "m2", now=self.clock())
        self.assertEqual(prs.latest_merge_sha(self.s), "m2")
        # Same second as #2, higher rowid: #3 wins the tie.
        prs.record_merged(self.s, r3["pr_number"], "m3", now=self.clock())
        self.assertEqual(prs.latest_merge_sha(self.s), "m3")
        # A later update to #1 makes it the newest.
        self.clock.advance(60)
        prs.record_merged(self.s, r1["pr_number"], "m1b", now=self.clock())
        self.assertEqual(prs.latest_merge_sha(self.s), "m1b")
        # A closed PR never counts, however new.
        self.clock.advance(60)
        prs.record_closed(self.s, r2["pr_number"], now=self.clock())
        self.assertEqual(prs.latest_merge_sha(self.s), "m1b")

    def test_pr_body(self):
        body = prs.pr_body(self.task, "b" * 40, "h" * 40)
        self.assertIn("`t1`", body)
        self.assertIn("#755", body)
        self.assertIn("budgeted-autonomy", body)
        self.assertIn("b" * 40, body)
        self.assertIn("h" * 40, body)
        self.assertIn("opened by the budgeted autonomy controller", body)
        self.assertEqual(body.rstrip("\n").split("\n")[-1], prs.GENERATED_LINE)

    # Kills: a Claude co-author trailer or the Claude Code line on every provider's commit and PR (pre-flight
    # finding ENV-7), and a Claude run losing its co-author trailer.
    def test_attribution_names_the_worker_and_credits_claude_only_for_a_claude_run(self):
        codex = {"provider": "codex", "model": "gpt-6-astra", "run_id": "run-1"}
        claude = {"provider": "claude", "model": "claude-sonnet-5", "run_id": "run-2"}
        self.assertEqual(prs.attribution_trailers(codex), "Worker: codex gpt-6-astra")
        self.assertEqual(prs.attribution_trailers(claude),
                         "Worker: claude claude-sonnet-5\nCo-Authored-By: Claude <noreply@anthropic.com>")
        body = prs.pr_body(self.task, "b" * 40, "h" * 40, codex)
        self.assertIn("- Worker: `codex` (`gpt-6-astra`), run `run-1`", body)
        self.assertNotIn("Claude", body)
        self.assertIn("`codex` worker", body.rstrip("\n").split("\n")[-1])
        body = prs.pr_body(self.task, "b" * 40, "h" * 40, claude)
        self.assertIn("- Worker: `claude` (`claude-sonnet-5`), run `run-2`", body)
        self.assertEqual(body.rstrip("\n").split("\n")[-1], prs.GENERATED_LINE)


class CompactHandoff(ControllerCase):
    def setUp(self):
        super().setUp()
        self.s = self.init_store()
        self.gh = FakeGh()
        self.gh.branches["auto/t1"] = "h" * 40
        seed_task(self.s, "t1", now=self.clock())
        self.row = prs.open_or_reuse_pr(self.s, self.gh, REPO, "t1", "auto/t1", "master", "b" * 40, "title", "body",
                                        now=self.clock())

    def red_verdict(self, summary="FAIL tests/test_x.py::test_one\nFAIL tests/test_y.py::test_two"):
        runs = completed(FIVE[2:]) + [
            CheckRun(name="cert-gate", status="completed", conclusion="failure", title="2 failed", summary=summary,
                     details_url="https://github.com/x/actions/runs/1"),
            CheckRun(name="build-core", status="completed", conclusion="cancelled", title="cancelled", summary="")]
        return evaluate_checks(FIVE, "h" * 40, runs)

    def test_contains_the_facts_identities_and_evidence(self):
        v = self.red_verdict()
        text = compact_handoff(self.row, v, 1, 2, "master", "/work/ws")
        self.assertIn("round 1 of 2", text)
        self.assertIn(f"#{self.row['pr_number']}", text)
        self.assertIn(self.row["url"], text)
        self.assertIn("auto/t1", text)
        self.assertIn("master", text)
        self.assertIn("b" * 40, text)
        self.assertIn("h" * 40, text)
        self.assertIn("/work/ws", text)
        for f in v.red:
            self.assertIn(f.identity, text)
            self.assertIn(f"{f.name}: {f.conclusion}", text)
        self.assertIn("https://github.com/x/actions/runs/1", text)
        self.assertIn("FAIL tests/test_y.py::test_two", text)
        self.assertIn("Do not run git", text)
        self.assertIn("Do not weaken", text)
        self.assertNotIn(prs.TRUNCATED_MARKER, text)

    def test_truncates_long_evidence_by_lines(self):
        summary = "\n".join(f"FAIL line {i}" for i in range(200))
        text = compact_handoff(self.row, self.red_verdict(summary), 2, 2, "master", "/ws")
        self.assertIn(prs.TRUNCATED_MARKER, text)
        self.assertIn("FAIL line 10", text)
        self.assertNotIn("FAIL line 199", text)
        self.assertNotIn("FAIL line 100", text)

    def test_truncates_long_evidence_by_chars(self):
        summary = "\n".join("x" * 400 for _ in range(20))   # 20 lines (under 60), 8000+ chars
        text = compact_handoff(self.row, self.red_verdict(summary), 2, 2, "master", "/ws")
        self.assertIn(prs.TRUNCATED_MARKER, text)
        start = text.index("```") + 3
        end = text.index("```", start)
        self.assertLessEqual(end - start, prs.HANDOFF_MAX_CHARS + len(prs.TRUNCATED_MARKER) + 4)

    def test_carries_nothing_it_was_not_given(self):
        # A run result, a session id and a previous prompt exist in the process; the handoff is built from
        # pr_row + verdict only, so none of them can appear.
        fake_run_result = {"result": "TRANSCRIPT: the worker said many things", "session_id": "SESSION-0xdeadbeef"}
        previous_prompt = "PREVIOUS-PROMPT body"
        text = compact_handoff(self.row, self.red_verdict(), 1, 2, "master", "/ws")
        self.assertNotIn("TRANSCRIPT", text)
        self.assertNotIn(fake_run_result["session_id"], text)
        self.assertNotIn(previous_prompt, text)

    def test_handoff_from_a_non_red_verdict_says_so(self):
        v = CheckVerdict(head_sha="h" * 40, verdict="pending", pending=list(FIVE))
        text = compact_handoff(self.row, v, 1, 2, "master", "/ws")
        self.assertIn("none red", text)


class RecordingRunner:
    """A stand-in for subprocess.run: records every argv and answers from a queue of
    (returncode, stdout, stderr) triples."""

    def __init__(self, *answers):
        self.answers = list(answers)
        self.calls = []

    def __call__(self, argv, **kw):
        self.calls.append((list(argv), kw))
        if not self.answers:
            raise AssertionError(f"unexpected gh call: {argv}")
        rc, out, err = self.answers.pop(0)
        return subprocess.CompletedProcess(argv, rc, out, err)


PR_JSON = {"number": 801, "state": "OPEN", "headRefOid": "h" * 40, "baseRefOid": "b" * 40,
           "headRefName": "auto/t1", "baseRefName": "master", "mergeCommit": None,
           "url": "https://github.com/IanFrelinger/Ashlar/pull/801", "title": "PR 3"}


class GhCliArgv(unittest.TestCase):
    def test_pr_view_argv_and_parsing(self):
        runner = RecordingRunner((0, json.dumps(dict(PR_JSON, state="MERGED", mergeCommit={"oid": "m" * 40})), ""))
        view = GhCli(runner=runner).pr_view(REPO, 801)
        argv, kw = runner.calls[0]
        self.assertEqual(argv, ["gh", "pr", "view", "801", "--repo", REPO, "--json", prs.PR_JSON_FIELDS])
        self.assertEqual(kw["stdout"], subprocess.PIPE)
        self.assertEqual(kw["stderr"], subprocess.PIPE)
        self.assertEqual(kw["timeout"], 120)
        self.assertEqual(view, PrView(number=801, state="MERGED", head_sha="h" * 40, base_sha="b" * 40,
                                      head_branch="auto/t1", base_branch="master", merge_sha="m" * 40,
                                      url=PR_JSON["url"], title="PR 3"))

    def test_pr_for_branch_argv_empty_list_is_none(self):
        runner = RecordingRunner((0, "[]", ""), (0, json.dumps([PR_JSON]), ""))
        cli = GhCli(runner=runner)
        self.assertIsNone(cli.pr_for_branch(REPO, "auto/t1"))
        self.assertEqual(runner.calls[0][0], ["gh", "pr", "list", "--repo", REPO, "--head", "auto/t1", "--state", "open",
                                              "--json", prs.PR_JSON_FIELDS])
        view = cli.pr_for_branch(REPO, "auto/t1")
        self.assertEqual((view.number, view.state, view.merge_sha), (801, "OPEN", None))

    def test_pr_create_argv_then_lists_the_branch(self):
        runner = RecordingRunner((0, PR_JSON["url"] + "\n", ""), (0, json.dumps([PR_JSON]), ""))
        view = GhCli(runner=runner).pr_create(REPO, "auto/t1", "master", "PR 3", "the body")
        argv = runner.calls[0][0]
        self.assertEqual(argv[:11], ["gh", "pr", "create", "--repo", REPO, "--head", "auto/t1", "--base", "master",
                                     "--title", "PR 3"])
        self.assertEqual(argv[11], "--body-file")
        self.assertEqual(len(argv), 13)
        self.assertEqual(runner.calls[1][0][:3], ["gh", "pr", "list"])
        self.assertEqual(view.number, 801)

    def test_pr_create_body_file_holds_the_body(self):
        seen = {}

        def runner(argv, **kw):
            if argv[1:3] == ["pr", "create"]:
                with open(argv[argv.index("--body-file") + 1], encoding="utf-8") as fh:
                    seen["body"] = fh.read()
                return subprocess.CompletedProcess(argv, 0, "", "")
            return subprocess.CompletedProcess(argv, 0, json.dumps([PR_JSON]), "")
        GhCli(runner=runner).pr_create(REPO, "auto/t1", "master", "PR 3", "line one\n\n🤖 tail")
        self.assertEqual(seen["body"], "line one\n\n🤖 tail")

    def test_check_runs_argv_and_one_object_per_line(self):
        lines = [json.dumps({"name": "cert-gate", "status": "completed", "conclusion": "failure",
                             "details_url": "https://x/1", "output": {"title": "2 failed", "summary": "FAIL a\nFAIL b"}}),
                 json.dumps({"name": "build-core", "status": "in_progress", "conclusion": None, "details_url": None,
                             "output": {"title": None, "summary": None}})]
        runner = RecordingRunner((0, "\n".join(lines) + "\n", ""))
        runs = GhCli(runner=runner).check_runs(REPO, "h" * 40)
        self.assertEqual(runner.calls[0][0], ["gh", "api", "--paginate", f"repos/{REPO}/commits/{'h' * 40}/check-runs",
                                              "--jq", prs.CHECK_RUNS_JQ])
        self.assertEqual(runs[0], CheckRun(name="cert-gate", status="completed", conclusion="failure", title="2 failed",
                                           summary="FAIL a\nFAIL b", details_url="https://x/1"))
        self.assertEqual(runs[1], CheckRun(name="build-core", status="in_progress", conclusion=None))
        self.assertEqual(evaluate_checks(FIVE, "h" * 40, runs).verdict, "red")

    def test_check_runs_empty_output_is_no_runs(self):
        self.assertEqual(GhCli(runner=RecordingRunner((0, "", ""))).check_runs(REPO, "h" * 40), [])

    def test_check_runs_unparsable_line_is_gh_error(self):
        with self.assertRaises(GhError):
            GhCli(runner=RecordingRunner((0, "not json\n", ""))).check_runs(REPO, "h" * 40)

    def test_pr_merge_argv_and_refusal_with_stderr(self):
        runner = RecordingRunner((0, "", ""), (1, "", "X Pull request #801 is not mergeable: the head was modified."))
        cli = GhCli(runner=runner)
        cli.pr_merge(REPO, 801, "h" * 40)
        self.assertEqual(runner.calls[0][0], ["gh", "pr", "merge", "801", "--repo", REPO, "--squash",
                                              "--match-head-commit", "h" * 40])
        with self.assertRaises(MergeRefused) as cm:
            cli.pr_merge(REPO, 801, "h" * 40)
        self.assertIn("head was modified", str(cm.exception))

    def test_branch_head_argv_404_is_none_other_failures_raise(self):
        runner = RecordingRunner((0, "t" * 40 + "\n", ""),
                                 (1, "", "gh: Not Found (HTTP 404)"),
                                 (1, "", "gh: HTTP 401: Bad credentials"))
        cli = GhCli(runner=runner)
        self.assertEqual(cli.branch_head(REPO, "auto/t1"), "t" * 40)
        self.assertEqual(runner.calls[0][0], ["gh", "api", f"repos/{REPO}/branches/auto/t1", "--jq", ".commit.sha"])
        self.assertIsNone(cli.branch_head(REPO, "auto/t1"))
        with self.assertRaises(GhError) as cm:
            cli.branch_head(REPO, "auto/t1")
        self.assertIn("Bad credentials", str(cm.exception))

    def test_non_zero_exit_on_a_read_is_gh_error_with_stderr(self):
        with self.assertRaises(GhError) as cm:
            GhCli(runner=RecordingRunner((1, "", "gh: not logged in"))).pr_view(REPO, 801)
        self.assertEqual(str(cm.exception), "gh: not logged in")

    def test_timeout_and_missing_executable_are_gh_errors(self):
        def timeout(argv, **kw):
            raise subprocess.TimeoutExpired(argv, kw["timeout"])

        def missing(argv, **kw):
            raise FileNotFoundError("gh")
        with self.assertRaises(GhError):
            GhCli(runner=timeout, timeout=7).pr_view(REPO, 1)
        with self.assertRaises(GhError):
            GhCli(runner=missing).pr_view(REPO, 1)


if __name__ == "__main__":
    unittest.main()
