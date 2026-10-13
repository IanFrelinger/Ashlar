# Phase F: pre-flight fix lanes (budgeted-autonomy, card #779)

Base: `origin/master` at `dccba4e035783da9a6be565c16656785198079af` (#784). Integration branch:
`fix/budgeted-autonomy-preflight-findings` in the clone `C:\Users\icfre\Documents\Codex\2026-09-12\f-2\work\ashlar-phase-f`.

These are defects found by the pre-flight of the owner-run `live-run-pr5.md` (workflow wf_efab9420-6f4), each
reproduced and confirmed by two verifiers. They are defect fixes in PR 5's code, not features. Every lane:

- works only in its own worktree, after `git fetch origin && git reset --hard dccba4e035783da9a6be565c16656785198079af`;
- touches only the files its section names, plus new test files it names;
- does NOT edit `tools/autonomy-controller/README.md`, `CHANGELOG.md`, or `tests/scripts/autonomy-controller.test.sh`
  (the integrator does the docs and raises the floor); write the README text you want in your final message instead;
- adds a test for every behaviour it changes, runs the whole suite (`bash tests/scripts/autonomy-controller.test.sh`;
  on this host `python` is Python 3.13 and `python3` is the Store stub, the wrapper probes and picks `python`), and
  OBSERVES each new test fail on the unfixed code (temporarily revert your fix, run the test, restore; say what
  failed with what message), then pass;
- runs at least one hand mutation per behaviour (apply, see the specific test go red, restore) and reports each
  as `mutation <id>: red=<failing test> green=restored`, with the exact old and new text so the integrator can
  rerun it through `scripts/mutation-check.sh`;
- keeps Python 3.8 compatibility (the wrapper says 3.8+): no hard dependency on `tomllib`, no `match`, no `X | Y` types;
- preserves line endings (Python and JSON are LF; check with `git diff --stat` that only intended lines changed);
- never launches a real CLI worker (`codex exec`, `cursor-agent -p`, `claude -p`), never passes `--gh cli` to a real
  controller against a real remote, never pushes, never touches the ledger under `%LOCALAPPDATA%`;
- commits on its worktree branch with a message `lane <X>: <summary>` (author from the repo config) and reports
  the commit SHA, the files changed, the new test names, the suite count line, the observed-red evidence, and the
  mutations. Deviations from this contract are allowed when the code demands it: say what and why.

Names below are exact where given; keep them.

## Lane A: controller and CLI operator paths

Files: `ashlar_controller/controller.py`, `ashlar_controller/cli.py`, `ashlar_controller/models.py`,
`ashlar_controller/budget.py` (only if needed), tests in `tests/test_provider_transfer.py`, `tests/test_cli_operator.py`,
`tests/test_controller.py`, `tests/test_pr_lifecycle.py`, `tests/test_operator_controls.py` or a new
`tests/test_preflight_fixes.py`.

A starting patch for A1, A3 and the README rows exists (read it, do not apply it blindly; it was written against
`dd38c007c`, which has the same controller code):
`C:\Users\icfre\AppData\Local\Temp\claude\C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction\875065c3-5d64-44f0-9643-39122a4af911\scratchpad\pf\transfer\d1-transfer-fix.diff`.

- **A1 (D1) `task transfer` and the PR lifecycle.** `task transfer` gains `--gh {none,cli}` (default none) and
  `--workspaces`, as `tick` has. In `Controller.transfer_task` step 3, every active run is polled once before
  anything is settled. Without a lifecycle, if a polled implementation or readiness-fixer run has completed, the
  transfer is refused with `ControllerError` whose message starts with `StopReason.TRANSFER_NEEDS_PR_LIFECYCLE`
  (`"transfer-needs-pr-lifecycle"`, new in `models.StopReason`), and nothing changes. With the lifecycle, the
  completed run is proposed (commit, push, PR) and the task moves `pr-open`, as the unit test already shows with a
  fake gh. If, after step 3, the task is `done`, the transfer journals `transfer-ended` and returns
  `moved: False` without moving it; a successful transfer returns `moved: True`. Step 3's
  `settle_and_reconcile()` call currently settles every uncertain run in the ledger: restrict the reconcile to this
  task's own runs (add a task filter to `settle_and_reconcile`, or reconcile the task's runs directly).
  Tests: a controller built with `gh=None` over a finished fake run refuses and changes nothing (task state,
  provider, journal kinds, run state all as before); the CLI form (`controller.py ... task transfer ... --gh none`)
  exits 1 with `transfer-needs-pr-lifecycle` on stderr; the `--gh` flag parses; a settlement that makes the task
  done returns `moved: False` and journals `transfer-ended`.
- **A2 (E2) the transfer's evidence in the journal.** The `provider-transferred` detail keeps `stopped_runs` and
  adds `stopped`: the list of `{run_id, provider, role, how, charged, stop_reason}` the transfer returns.
- **A3 (T4 and T5) `run cancel`.** `run cancel` gains a repeatable `--adapter`. `Controller.cancel_run`: a run in
  `launching`, `running` or `unknown` whose provider has no registered adapter is refused with
  `ControllerError` starting `previous-writer-not-stoppable` (`StopReason.TRANSFER_WRITER_NOT_STOPPABLE`) and nothing
  is settled. After `adapter.cancel(...)`, poll the adapter once more: if the run is no longer RUNNING and the
  adapter's status is not CANCELLED (it had already finished), settle with the adapter's own status rather than a
  synthesized CANCELLED. Tests: a running fake-provider run cancelled with no adapter registered is refused and
  stays running; a run whose adapter reports it finished at the post-cancel poll is settled `completed`, not
  `cancelled`; the CLI `run cancel ... --adapter fake` parses.
- **A4 (SEQ-5, T8) `run settle`.** `run settle` of a run whose `settled_at` is set is refused with exit 1 and a
  message naming its state and stop reason, and changes nothing. `_apply_outcome` also returns early, changing no
  task state, when `budget.settle` reports `already_settled`. `run settle --stop-reason completed` of an
  `implementation` or `readiness-fixer` run is refused (exit 1): it has no PR lifecycle and would make the task
  `done` with nothing proposed; the message says to tick with `--gh cli` instead. Tests for each.
- **A5 (T8) readiness on a task with an open PR.** `evaluate_readiness` with no failures does not set `done` on a
  task that has an open PR row: it returns an action that says the PR phase decides (`"pr-open"`) and leaves the
  task as it is. Test.
- **A6 (E8) a failed proposal.** In `_apply_outcome`, an exception from `self.lifecycle.after_completed(...)`
  (`gitops.GitError`, `prs.GhError`, or any other `Exception`) is caught: journal `propose-failed` with
  `{run_id, error: str(e)[:2000]}`, set the task `needs-reconcile` with blocker
  `"completed run not proposed: <first line of the error>"`, and return `settled["lifecycle"] =
  {"action": "propose-failed", "error": ...}`. The tick then reports the settle normally (no traceback, exit 0).
  Tests with a fake gh whose `pr_create` raises and a fake git whose push raises: the tick returns normally, the
  task is `needs-reconcile` with that blocker, the journal has `propose-failed`, and the run stays settled as it was.
  Say in your report what the next tick then does (the task is not eligible; another queued task may launch), and
  whether that is acceptable or needs a guard (do not add one without saying why).
- **A7 (SEQ-6) dry-run parity.** `Controller._plan` mirrors the tick's dispatch rules, read-only: a writer is any
  active run whose role is `implementation` or `readiness-fixer`; with the lifecycle on (dry run given `--gh cli`)
  any open PR row gives `would-block` with reason `pr-open-blocks-dispatch`; a picked task whose provider has no
  registered adapter gives the same blocked report the real tick gives (read controller.py tick around the adapter
  lookup and use its exact reason). `tick --dry-run` must still open the state read-only and call no adapter. Tests.
- **A8 (REPORT-CP1252) output encoding.** `cli._print` (or `main`) must not crash when stdout is redirected to a
  file under a legacy code page: when `sys.stdout` is not a TTY, write UTF-8 (`sys.stdout.reconfigure(encoding=
  "utf-8")` where available, 3.7+). Test by running the CLI in a subprocess with `PYTHONIOENCODING=cp1252` and
  `PYTHONUTF8=0`, stdout captured (a pipe is not a TTY), over a ledger whose task blocker holds a non-cp1252 character
  (for example U+2192), for both `report` and `--json status`: exit 0, output decodes as UTF-8 and contains it.

## Lane B: the Cursor adapter across processes

Files: `ashlar_controller/adapters/cursor.py`, new `ashlar_controller/adapters/_exit_recorder.py`,
`tests/fake_cursor_cli.py`, `tests/test_cursor_adapter.py`, new `tests/test_cursor_across_processes.py`.

A starting patch exists:
`C:\Users\icfre\AppData\Local\Temp\claude\C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction\875065c3-5d64-44f0-9643-39122a4af911\scratchpad\pf\transfer\cursor-exit-code-fix.diff`.

- **B1 (SEQ-1, E1, T3) the exit code outlives the tick.** Every `tick` is its own process, so the process that
  settles a Cursor run never holds the Popen that launched it, `manifest["exit"]` stays None, and a clean exit-0 run
  settles `no-final-result` (or `transient`/`rate-limited`/`quota-exhausted` when the model's text mentions those
  words). Launch the CLI under `adapters/_exit_recorder.py` (run as `[sys.executable, "-I", <path to
  _exit_recorder.py>, <run dir>/exit_code, "--", *argv]`), which waits for the CLI with inherited stdout/stderr,
  writes the exit code to the file atomically (temp file + `os.replace`), and exits with the same code. The
  manifest records `exit_file`. `_alive` and `_classify` read the file when no Popen is held and the pid is gone.
  The manifest pid is the recorder's, so `cancel`'s tree kill still covers the CLI. Tests in
  `tests/test_cursor_across_processes.py`, each launching with one `CursorAdapter` and polling with a FRESH
  `CursorAdapter(runs_dir)` after the child has exited: clean exit 0 with a result stream is FINISHED `completed`
  with exit 0; exit 0 whose stdout mentions "network", "rate limit" and "usage limit" is still `completed`; exit 3
  is `task-failed` with exit 3. Show all three fail on the unfixed adapter.
- **B2 (T10) the fake's race.** `tests/fake_cursor_cli.py` records the child pid by `os.replace` onto the
  `print-<pid>.json` the test polls, which Windows refuses while the test has it open (about 12 percent of runs of
  `test_cancel_records_first_then_kills_the_whole_tree`). Record the child pid in its own file
  (`child-<pid>.json`) that is written once and never replaced, and make the test read it. Run the cancel test 25
  times in a loop and report the pass count.
- **B3 (T5) never cancel a finished run.** `CursorAdapter.cancel` does not write `cancelled_at` and does not kill
  when the run's exit file already exists (the run finished; a live pid then belongs to someone else). Test.

## Lane C: the Codex and Claude adapters

Files: `ashlar_controller/adapters/codex.py`, `ashlar_controller/adapters/claude.py`, `tests/test_codex_adapter.py`,
`tests/test_claude_adapter.py`, `tests/fake_codex_cli.py` and `tests/fake_claude_cli.py` if needed.

- **C1 (ENV-3) Codex's service tier.** The owner's `%USERPROFILE%\.codex\config.toml` sets
  `service_tier = "priority"` at top level, and the adapter keeps the user config loaded (it must: with
  `--ignore-user-config` writes were refused) and does not override it, so every controller Codex run would use the
  priority tier ("Fast: 2x speed, increased usage" in the CLI's own catalog) although the policy's speed is
  `standard`. No value of `service_tier` that means standard has been observed, so do not pass an override.
  Instead: read the effective top-level `service_tier` from `<CODEX_HOME or ~/.codex>/config.toml` (use `tomllib`
  when importable, else a minimal parser of top-level `key = "value"` lines before the first `[table]`). When it is
  set and the request's speed is `standard`, refuse the launch with `LaunchRefused` (reuse the existing refusal
  path the adapter uses for a non-standard speed; read it), with a message naming the file and line and the operator
  step ("comment out service_tier in <file> for the controller's runs"). Record `service_tier` (or None) in the
  manifest of every launch. The adapter takes its codex home the same way it finds session files today; read how.
  Tests with a temporary CODEX_HOME: unset tier launches; `priority` is refused before any process starts; the
  manifest records the tier; a tier inside a `[profiles.x]` table is not top level and does not count.
- **C2 (T5) never cancel a finished Codex or Claude run.** `CodexAdapter.cancel`: if the run's stdout already
  holds `turn.completed` or `turn.failed`, return without writing `cancelled_at` and without killing (a live pid
  then is either exiting or reused). `ClaudeAdapter.cancel`: the same when its stream already holds its terminal
  `result` event (read how `claude.py` detects it). Tests for each: a finished stream plus a live unrelated process
  under the manifest pid (start a harmless sleeping python child of the test and point the manifest pid at it) is
  not killed and gets no `cancelled_at`; the poll then classifies from the stream.
