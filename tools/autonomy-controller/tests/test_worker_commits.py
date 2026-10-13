"""Phase F pre-flight finding C9: the controller tells `commit_and_push` which commit a workspace started from,
so a worker that commits its own edit (the Cursor CLI runs with `-f` and no sandbox) is folded back and
proposed instead of reading as `no-change-to-propose`. The folding itself is tested against real git in
test_gitops.py; this file proves the lifecycle passes the right start for both kinds of run."""
from ashlar_controller.models import TaskState

from .test_pr_lifecycle import FakeGit, LifecycleCase, red


class StartRecordingGit(FakeGit):
    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        self.starts = []

    def commit_and_push(self, path, branch, message, **kw):
        self.starts.append(kw.get("start_sha"))
        return super().commit_and_push(path, branch, message, **kw)


class TheWorkspaceStart(LifecycleCase):
    def setUp(self):
        super().setUp()
        self.git = StartRecordingGit(self.gh, str(self.tmp / "ws"))

    # Kills: `commit_and_push(workspace, branch, message)` without `start_sha` (the fold never happens), and an
    # implementation run given anything but the base it was cloned at.
    def test_an_implementation_run_starts_from_the_task_base(self):
        self.make()
        self.open_pr()
        self.assertEqual(self.git.starts, ["base-0000000"])
        self.assertEqual(self.task()["base_sha"], "base-0000000")

    # Kills: a repair run given the task base instead of the PR head it was cloned at.
    def test_a_repair_run_starts_from_the_pr_head_it_was_cloned_at(self):
        self.make()
        row = self.open_pr()
        self.gh.set_checks(row["head_sha"], red(row["head_sha"]))
        self.assertEqual(self.ctl.tick().action, "launched")
        self.assertEqual(self.ctl.tick().action, "settled")
        self.assertEqual(self.git.starts, ["base-0000000", row["head_sha"]])
        self.assertEqual(self.task()["state"], TaskState.PR_OPEN)
