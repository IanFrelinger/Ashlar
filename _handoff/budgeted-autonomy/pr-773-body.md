## Budgeted autonomy controller, PR 4: operator controls, evidence retention, pilot (card #772)

Phase D of workstream `budgeted-autonomy` (roadmap #755; PR 1 #756 `1d8bc7448`, PR 2 #769 `f236bd4ad`, PR 3 #771 `d2c11b347`). Base master `d2c11b347`.

### What changed

- **The operator view.** `status` adds per task the PR record and `evidence`: the last run's directory under `<state dir>/runs/<run>/`, its `manifest.json`, `stdout.json`, `stderr.log`, the transcript located by session id from the manifest (the manifest's path is only a hint), the recorded workspace and whether it is still on disk, and the evidence archive when one exists; `null` for what is not on disk. The last run is picked by insertion order (rowid), not by run id. No adapter is asked (`Controller.run_evidence`).
- **`report`** renders `status` and `metrics` as Markdown from the ledger alone (`metrics.render_report`); `--json` gives both dicts and the text. **`metrics`** (`metrics.collect`) is counts and sums from the ledger: tasks and runs by state, runs by stop reason and role, turns consumed and reserved, overshoot, attempts, PRs by outcome and repair rounds, retries, latest usage per provider and kind, the per-provider sum of `estimated_api_value` deltas kept apart from every turns figure, journal by kind, the controls.
- **`backup <dest>`** copies the open ledger through `sqlite3.Connection.backup`, refuses to overwrite, journals `backup` in the source. **`restore <source> [--replace]`** runs before the state is opened: integrity, tables and `schema_version` of the source are checked (`1` and `2` are known; anything else is refused with nothing written), an existing `--state` is refused without `--replace`, the copy goes to a temp file beside the destination, stale `-wal`/`-shm` sidecars are removed, the file is replaced atomically, and the result is opened with `Store.open` (schema 1 migrates, integrity runs) and journals `restored`.
- **`evidence archive <run>`** writes `<state dir>/evidence/<run>.tar.gz`: `run/<file>` for each file in the run directory, `transcript.jsonl` when it can be located, `ledger.json` (run, task, pool, PR, journal, usage, policy version rows) and `MANIFEST.txt`; never overwrites; journals `evidence-archived` with the sha256. **`evidence list`** lists them.
- **Controls at the next tick**, proven: `pause` is checked before the PR phase (a red head gets no repair and is not observed while paused); `provider disable` removes the provider from dispatch and repair launches (no round spent, the PR stays watched); `run cancel` of a running repair settles it `cancelled`, keeps the round spent, keeps the PR open and watched (a merge by hand is recorded and makes the task `done`), moves the task to `cancelled`, launches nothing more for it.
- README ("Operator controls and evidence (PR 4)", command rows, "Not in PR 4"), CHANGELOG, DocsIndex; test floor raised.

### Integrator decisions (no owner rows; the owner can reverse any)

- A cancelled repair leaves the task `cancelled` (operator took over) rather than `pr-open`; the PR is still watched for a merge or a closure, and `task set-state <id> pr-open` hands it back to the controller.
- `restore` refuses a `schema_version` it does not know, including a newer one; `1` is accepted because `Store.open` migrates it in place.
- Archives live under `<state dir>/evidence/` and are never pruned or overwritten.
- `restore` exits 3 (the state-refused code) on every refusal.

### The live run of the PR 3 lifecycle (owner's terminal, 2026-10-11 21:18 to 21:39 UTC)

Run while this PR was open, with the checkout's working tree (so `status`, `report`, `metrics`, `evidence archive` and `backup` from this PR were exercised against a real ledger). Task `live-pr3`, ceilings 8/16/3, a sixth never-running required check as a guard. Attempt 1 (`run-1ced8587a0bb`) died at once on OAuth refresh contention with the desktop app's Claude Code processes (1 turn charged, requeued by hand; the classifier rule is fixed in #774). Attempt 2 (`run-561ad9689fe5`) completed in 3 turns (0.0825 USD-estimate; five-hour window 32 percent); the controller committed as `ashlar-controller`, pushed `auto/live-pr3`, opened #775 at `a84e0e9` on base `d2c11b347`, observed it by head SHA (`pr-observed`: three green and `cert-gate`, `build-core` pending; then five green and the guard pending, verdict `pending`, no merge), and after the hand close recorded `pr-closed-unmerged` with the task `parked-failure` ("nothing landed, the task is not done"). Both runs archived with their transcripts; a backup taken; `report` rendered. Findings: the OAuth text classification (#774) and the PR title being the whole task title (cosmetic, queued).

### Not done in this PR, and why

- **The pilot (three bounded real tasks) has not run.** The session's auto-mode classifier refuses `tick --adapter claude --gh cli` ("Create Unsafe Agents"; third phase in a row), so it is prepared for the owner's terminal (`pilot-pr4.md` on `claude/budgeted-autonomy-workspace`); whether the controller may merge to master during the pilot is an open owner question. `gh pr merge --match-head-commit` and a repair round remain exercised against the fake only; the README text saying so lands with #774.

### Verification

- `bash tests/scripts/autonomy-controller.test.sh` on the host: 383 passed (`OK (skipped=1)`); in the container (`scripts/test-in-container.sh`): 383 passed.
- `bash scripts/check-docs-phantom-paths.sh`: all referenced paths exist.
- Mutation checks (`scripts/mutation-check.sh`, `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`), verbatim:

```
mutation c1-pause-ignored-by-tick: KILLED red=failed:6/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c2-disabled-provider-still-repairs: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c3-cancelled-task-may-launch-repair: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c4-cancel-skips-the-adapter: KILLED red=failed:3/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c5-evidence-transcript-not-located: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c6-evidence-archive-reported-when-absent: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c7-status-last-run-by-run-id: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c8-restore-replaces-without-flag: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a1-consumed-total-includes-estimate: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a2-active-states-drop-running: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a3-estimate-caveat-dropped: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a4-pr-outcomes-counted-by-state: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a5-report-drops-evidence: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b1-unknown-schema-accepted: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b2-stale-sidecars-kept: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b3-restore-ignores-replace: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b4-archive-overwrites: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b5-ledger-drops-the-task-pr: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b6-transcript-claimed-when-absent: KILLED red=failed:3/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b7-backup-overwrites: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
```

Lane observations (each lane applied three mutations by hand and watched its named test fail):

**Lane B (evidence.py), commit `8c88df458`, 22 tests:**
1. `test_restore_removes_stale_wal_and_shm_sidecars`; mutation `_remove_sidecars(dest_path)` in `restore` replaced by `pass`. Red: `AssertionError: 'yes' is not None` (the stale `-wal` planted by the test, a valid WAL holding an extra `meta` row, was replayed onto the restored file). `Ran 22 tests ... FAILED (failures=1)`; restored, green 22/22.
2. `test_restore_refuses_an_unknown_schema_and_writes_nothing`; mutation `KNOWN_SCHEMA_VERSIONS = ("1", "2", "99")`. Red: `AssertionError: "schema_version '99' is not one this controller knows" not found in "controller state ...\restored\controller.db: schema_version '99' != 2"` (the refusal came from `Store.open` after the file was already in place, not from restore's pre-write guard). `FAILED (failures=1)`; restored, green.
3. `test_ledger_json_carries_the_run_the_task_the_pr_and_journal_rows_with_the_run_id`; mutation: the task-level `prs ... ORDER BY rowid DESC` fallback in `_ledger_slice` disabled (`if pr is None:` to `if False:`). Red: `TypeError: 'NoneType' object is not subscriptable` at `ledger["pr"]["pr_number"]` (the implementation run's `runs.pr_number` is NULL, so without the fallback the archive carries no PR). `FAILED (errors=1)`; restored, green.

Lane B's resolved ambiguities, accepted by the integrator: an existing archive path is refused with `StateCorrupt` (as `backup` does); `restore`'s `sha256` is the source file's (the restored file changes the moment `restored` is journalled into it); a missing run dir gives `members == ["ledger.json", "MANIFEST.txt"]`; the transcript's session id falls back to `runs.native_session_id`; `backup`'s `rows["journal"]` is the source's count minus the `backup` row written after the copy; a missing source and a source missing a required table are refused before anything is written.


🤖 Generated with [Claude Code](https://claude.com/claude-code)
