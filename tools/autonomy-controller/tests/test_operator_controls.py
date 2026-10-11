"""PR 4 acceptance, part 1: the operator's controls take effect at the next tick, and `status` names the
evidence of the last run without asking an adapter.

- pause stops the next tick before the PR phase (no repair launches for a red head) and resume lets it go;
- a disabled provider gets no implementation launch and no repair launch until re-enabled;
- cancelling a running repair keeps its round spent and the PR open and watched, and nothing launches for
  the task afterwards; a merge done outside the controller is still recorded;
- `status` reports the run directory, manifest, stdout, stderr, transcript, workspace and archive paths of
  the last run from the ledger row and the run directory alone, with None for what is not on disk;
- `status` picks the last run by insertion order, not by run id.
"""
import json
import os
import unittest

from ashlar_controller.models import RunState, RunStatus, StopReason, TaskState

from .helpers import ControllerCase
from .test_pr_lifecycle import LifecycleCase, red


class ControlsAtTheNextTick(LifecycleCase):
    def test_pause_stops_a_repair_launch_and_resume_allows_it(self):
        self.make()
        self.open_pr()
        self.gh.set_checks("head-1", red("head-1"))
        self.ctl.pause("owner review")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("paused", StopReason.PAUSED))
        self.assertEqual(self.adapter.launch_count, 1)          # the implementation run only; no repair
        self.assertEqual(self.gh.view_count, 0)                 # the PR was not even observed while paused
        self.ctl.resume()
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("launched", "repair"))
        self.assertEqual(self.adapter.launch_count, 2)

    def test_a_disabled_provider_gets_no_repair_and_no_launch_until_enabled(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", red("head-1"))
        self.store.set_control("disabled_providers", json.dumps(["fake"]))
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertEqual(self.adapter.launch_count, 1)
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 0)   # no round was spent on a disabled provider
        # A second, queued task on the same provider is not launched either once the PR is gone.
        self.gh.close(n)
        self.assertEqual(self.ctl.tick().action, "pr")
        self.add_task("t2")
        self.assertEqual(self.ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 1)
        self.store.set_control("disabled_providers", json.dumps([]))
        self.assertEqual(self.ctl.tick().action, "launched")
        self.assertEqual(self.adapter.launch_count, 2)

    def test_cancelling_a_running_repair_keeps_the_round_spent_and_the_pr_open(self):
        self.make()
        n = self.open_pr()["pr_number"]
        self.gh.set_checks("head-1", red("head-1"))
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("launched", "repair"))
        out = self.ctl.cancel_run(r.run_id)
        self.assertTrue(out["cancelled"])
        self.assertEqual(self.adapter.cancel_count, 1)
        run = self.run_row(r.run_id)
        self.assertEqual((run["state"], run["stop_reason"], run["role"]), (RunState.CANCELLED, StopReason.CANCELLED, "readiness-fixer"))
        pr = self.pr_row(n)
        self.assertEqual((pr["state"], pr["repair_rounds_used"]), ("open", 1))
        t = self.task()
        self.assertEqual((t["state"], t["stop_reason"]), (TaskState.CANCELLED, StopReason.CANCELLED))
        # Next tick: the PR is still watched (observed by head) and still blocks dispatch, but nothing launches
        # for a cancelled task, and the red head does not spend another round.
        views = self.gh.view_count
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.PR_OPEN_BLOCKS_DISPATCH))
        self.assertGreater(self.gh.view_count, views)
        self.assertEqual(self.adapter.launch_count, 2)
        self.assertEqual(self.pr_row(n)["repair_rounds_used"], 1)
        self.assertEqual(self.task()["state"], TaskState.CANCELLED)
        # The operator merges by hand: recorded as merged, the task is done, the queue is free.
        self.gh.merge_externally(n, "merge-by-hand")
        r = self.ctl.tick()
        self.assertEqual((r.action, r.reason), ("pr", "merged"))
        self.assertEqual(self.pr_row(n)["merge_sha"], "merge-by-hand")
        self.assertEqual(self.task()["state"], TaskState.DONE)

    def test_cancelling_a_queued_run_is_refused_without_an_adapter_call(self):
        self.make()
        self.add_task()
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        r = self.ctl.tick()
        self.ctl.cancel_run(r.run_id)
        out = self.ctl.cancel_run(r.run_id)
        self.assertFalse(out["cancelled"])
        self.assertEqual(self.adapter.cancel_count, 1)


class EvidenceInStatus(ControllerCase):
    def _last_run_id(self):
        return self.store.row("SELECT run_id FROM runs ORDER BY rowid DESC LIMIT 1")["run_id"]

    def test_status_names_the_evidence_of_the_last_run_from_disk(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.tick()
        run_id = self._last_run_id()
        run_dir = os.path.join(ctl.runs_dir, run_id)
        os.makedirs(run_dir)
        projects = self.tmp / "projects" / "C--some-truncated-slug-abc123"
        projects.mkdir(parents=True)
        (projects / "sess-1.jsonl").write_text('{"type":"user"}\n', encoding="utf-8")
        manifest = {"run_id": run_id, "session_id": "sess-1", "projects_dir": str(self.tmp / "projects"),
                    "transcript_path": str(self.tmp / "projects" / "wrong-slug" / "sess-1.jsonl"), "cwd": str(self.tmp / "ws")}
        for name, body in (("manifest.json", json.dumps(manifest)), ("stdout.json", '{"type":"result"}\n'), ("stderr.log", "")):
            with open(os.path.join(run_dir, name), "w", encoding="utf-8") as f:
                f.write(body)
        calls = self.adapter_calls()
        e = ctl.status()["tasks"][0]["evidence"]
        self.assertEqual(self.adapter_calls(), calls)
        self.assertEqual(e["run_dir"], run_dir)
        self.assertEqual(e["manifest"], os.path.join(run_dir, "manifest.json"))
        self.assertEqual(e["stdout"], os.path.join(run_dir, "stdout.json"))
        self.assertEqual(e["stderr"], os.path.join(run_dir, "stderr.log"))
        # The hinted path does not exist; the transcript is found by its session id under the projects dir.
        self.assertEqual(e["transcript"], str(projects / "sess-1.jsonl"))
        self.assertIsNone(e["archive"])
        os.makedirs(ctl.evidence_dir)
        with open(os.path.join(ctl.evidence_dir, f"{run_id}.tar.gz"), "wb") as f:
            f.write(b"\x1f\x8b")
        self.assertEqual(ctl.status()["tasks"][0]["evidence"]["archive"], os.path.join(ctl.evidence_dir, f"{run_id}.tar.gz"))

    def test_status_evidence_is_none_for_what_is_not_on_disk(self):
        ctl = self.make_controller()
        self.add_task()
        ctl.tick()
        s = ctl.status()
        e = s["tasks"][0]["evidence"]
        self.assertEqual({k: e[k] for k in ("run_dir", "manifest", "stdout", "stderr", "transcript", "archive")},
                         {k: None for k in ("run_dir", "manifest", "stdout", "stderr", "transcript", "archive")})
        self.assertFalse(e["workspace_present"])
        self.assertEqual(s["runs_dir"], ctl.runs_dir)
        self.assertEqual(s["evidence_dir"], ctl.evidence_dir)
        self.assertEqual(os.path.dirname(ctl.runs_dir), os.path.dirname(os.path.abspath(self.state_path)))

    def test_status_without_a_run_has_no_evidence(self):
        ctl = self.make_controller()
        self.add_task()
        self.assertIsNone(ctl.status()["tasks"][0]["evidence"])
        self.assertIsNone(ctl.status()["tasks"][0]["last_run"])

    def test_status_last_run_is_the_latest_by_insertion_order(self):
        # Two runs reserved in the same second: the one inserted later is the last run, whatever its id sorts as.
        ctl = self.make_controller()
        self.add_task()
        r = ctl.tick()
        first = self.run_row(r.run_id)
        with self.store.tx():
            for run_id in ("run-zzzzzzzzzzzz", "run-aaaaaaaaaaaa"):
                self.store.conn.execute(
                    "INSERT INTO runs(run_id, task_id, pool_id, attempt_no, role, provider, model, effort, speed, funding_pool, "
                    "limits_json, state, reservation, reserved_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    (run_id, "t1", first["pool_id"], 9, "verifier", "fake", "fake-model", "medium", "standard", "fake-pool",
                     first["limits_json"], RunState.FAILED, 0.0, first["reserved_at"]))
        self.assertEqual(ctl.status()["tasks"][0]["last_run"]["run_id"], "run-aaaaaaaaaaaa")


if __name__ == "__main__":
    unittest.main()
