# PR 4 lanes: operator controls, evidence retention, pilot (card #772)

Base: master `d2c11b347d1759215b87bc98c94efced62f3b6d8`. Package: `tools/autonomy-controller/ashlar_controller/`.
Python 3.8+, standard library only. Every lane: one new module, one new test file, one commit, nothing else
touched (the integrator wires `cli.py`, `controller.py`, README, CHANGELOG). Tests run with
`bash tests/scripts/autonomy-controller.test.sh` from the repo root (host; `OK (skipped=1)` on Windows is green).
Fixtures: `tests/helpers.py` (`ControllerCase`, `FakeClock`, fake adapter) and the lifecycle fixtures in
`tests/test_pr_lifecycle.py` (`LifecycleCase`, `FakeGit`, `prs.FakeGh`). Build ledgers THROUGH the Controller
(`ctl.ensure_task`, `ctl.tick()`, `adapter.default_status = ...`), not by hand-inserting rows, except where a
test needs a malformed ledger on purpose.

Everything is LLM-free: no module in this PR imports an adapter to call it; a test asserts `adapter_calls()`
did not change across the operation.

Write files with LF line endings (`newline="\n"` in Python; the repo has `core.autocrlf=true`).

## Lane A: `metrics.py` (counts from the ledger) and the report

```python
def collect(store: Store, now: Optional[float] = None) -> Dict[str, Any]
```
Pure SQL over the ledger; no adapter, no policy needed. Returns, with every key always present:
- `generated_at` (ISO, `store.utcnow_iso(now)`), `controller_id`, `schema_version`
- `tasks`: `{"total": int, "by_state": {state: n}}`
- `runs`: `{"total", "by_state", "by_stop_reason", "by_role", "consumed_total" (sum consumed), "reserved_active_total"
  (sum reservation over runs in reserved/launching/running), "overshoot_total" (sum budget_pools.overshoot),
  "attempts_used_total" (sum budget_pools.attempts_used)}`
- `prs`: `{"total", "open", "merged", "closed_unmerged", "repair_rounds_total", "by_outcome": {...}}`
- `retries`: `{"identities": n distinct failure identities with count>0, "total": sum count}`
- `usage`: `{"latest": [ {provider, kind, unit, freshness, value, total, observed_at} latest per (provider, kind) ],
  "estimated_api_value_total_by_provider": {provider: sum of delta where kind='estimated_api_value'}}`
  The estimated API value is labelled an estimate and is NEVER added to `runs.consumed_total` or to any turns figure.
- `events`: `{"total", "handled"}`
- `journal`: `{"total", "by_kind": {kind: n}}`
- `controls`: `{"paused": bool, "disabled_providers": [...]}`

```python
def render_report(status: Dict[str, Any], metrics: Dict[str, Any]) -> str
```
Markdown built from `Controller.status()`'s dict and `collect()`'s dict only. Sections, in order:
`# Controller <id>` line with state path, policy version/sha12, paused, disabled providers;
`## Tasks`: one `### <task_id>` per task with state, stop reason, blocker, attempts `used/max (remaining permitted)`,
budget `consumed+reserved of ceiling unit`, funding pool, PR record (`PR #n state outcome head12 merge12 rounds`)
when present, last run (id, state, stop reason, model/effort, session) and, when `status["tasks"][i]["evidence"]`
is present (the integrator adds it: a dict `{run_dir, manifest, stdout, stderr, transcript, workspace, archive}`
of paths or None), an `Evidence:` list of those paths;
`## Metrics`: tables for runs by stop reason, PRs by outcome, usage latest (with the estimate caveat line
"estimated_api_value is an estimate of API list value, not a charge"), retries, journal by kind.
Tolerate missing keys (`.get`) so an older status dict still renders.

Tests `tests/test_metrics.py` (>= 10), each killing a specific mutation, e.g.: by_stop_reason counts after one
completed and one task-failed run; consumed_total equals the sum of settled consumption and excludes the
estimated API value (record an `estimated_api_value` observation via `budget.record_usage` and assert it is in
`usage` and not in `runs`); `reserved_active_total` counts a running run and drops after settlement; PR counts
after a merge and a closed-unmerged (use LifecycleCase); `retries.total` after a requeued diagnosed failure;
`collect` makes zero adapter calls; `render_report` contains the blocker text, `attempts_remaining`, the PR
number, each evidence path, and the estimate caveat; `render_report` on a status without `evidence` still renders.

## Lane B: `evidence.py` (backup, restore, per-run archives)

```python
KNOWN_SCHEMA_VERSIONS = ("1", "2")   # 1 is migrated in place on open; anything else is unknown

def backup(store: Store, dest_path: str, now: Optional[float] = None) -> Dict[str, Any]
```
SQLite online backup (`sqlite3.Connection.backup`) of the OPEN store into `dest_path`. Refuses (StateCorrupt)
if `dest_path` exists. Creates parent dirs. Returns `{"path", "sha256", "schema_version", "rows": {table: n for
table in REQUIRED_TABLES}}`. Journals `backup` `{path, sha256}` in the SOURCE store (so the backup file itself
does not carry its own backup row; take the journal row AFTER the copy).

```python
def restore(src_path: str, dest_path: str, *, replace: bool = False, now: Optional[float] = None) -> Dict[str, Any]
```
1. `src_path` must exist; open it read-only (`file:...?mode=ro`); `PRAGMA integrity_check` must be `ok`; every
   `store.REQUIRED_TABLES` present; `meta.schema_version` in `KNOWN_SCHEMA_VERSIONS`, else StateCorrupt
   ("refusing to restore ...: schema_version X is not one this controller knows"). Nothing is written on refusal.
2. `dest_path` exists and `replace` is False: StateCorrupt ("refusing to overwrite live state ...; pass replace").
3. Copy with the backup API into a temp file beside dest, remove stale `dest-wal` / `dest-shm` sidecars, then
   `os.replace` the temp file onto dest (atomic). On Windows close every connection before replacing.
4. Open the result with `Store.open(dest_path)` (which migrates schema 1 to 2 and runs integrity): the restored
   ledger must accept it. Journal `restored` `{source, sha256, replaced: bool}` in the restored store; close it.
Returns `{"path", "source", "sha256", "schema_version_before", "schema_version_after", "replaced"}`.

```python
def archive_run(store: Store, run_id: str, out_dir: str, runs_dir: str, now: Optional[float] = None) -> Dict[str, Any]
def list_archives(out_dir: str) -> List[Dict[str, Any]]
```
`archive_run` writes `<out_dir>/<run_id>.tar.gz` (refuses if it exists; creates out_dir). Members:
- `run/<file>` for every regular file directly under `<runs_dir>/<run_id>/` (manifest.json, stdout.json,
  stderr.log when present; a missing run dir is allowed: the archive then holds the ledger only, and
  `members` says so);
- `transcript.jsonl` when the manifest names `transcript_path` / `projects_dir` + `session_id` and
  `ashlar_controller.adapters.claude.locate_transcript(projects_dir, session_id, hint)` finds a file (import
  lazily inside the function; never call any adapter method);
- `ledger.json`: `{"run": row, "task": row, "pool": row, "pr": row or None (runs.pr_number, else the task's
  latest prs row by rowid), "journal": rows where run_id=? OR task_id=?, "usage": rows where run_id=?,
  "policy_version": row for run.policy_version_id, "archived_at": iso}` with sqlite rows as dicts;
- `MANIFEST.txt`: one line per member with its byte size, plus the archive's `run_id` and `archived_at`.
Unknown `run_id`: PolicyError("no run ..."). Journals `evidence-archived` `{run_id, path, sha256, members}`.
Returns `{"path", "sha256", "run_id", "members": [names], "transcript_included": bool, "pr_number"}`.
`list_archives` returns `[{"run_id", "path", "bytes", "modified_at"}]` sorted by run_id for `*.tar.gz` in out_dir.

Tests `tests/test_evidence.py` (>= 12), each killing a specific mutation: backup refuses an existing dest;
backup's row counts equal the source's and the file opens with `Store.open` and has the same `controller_id`;
restore refuses an unknown schema (write a copy whose `meta.schema_version` is `'99'`) and writes nothing
(dest absent afterwards); restore refuses an existing dest without replace and leaves it byte-identical; restore
with replace swaps the file and the restored store accepts `ensure_task` + `tick` (fake adapter) afterwards;
restore of a schema-1 file (set `meta.schema_version='1'` in a copy) opens and is migrated (journal has
`schema-migrated`); restore removes stale `-wal`/`-shm` sidecars; archive refuses an existing path; archive
members include `run/manifest.json`, `run/stdout.json`, `run/stderr.log`, `ledger.json`, `MANIFEST.txt` when a
run dir with those files exists (make the run dir by hand under a temp runs_dir with a manifest naming a
`transcript_path` that exists, so `transcript.jsonl` is included; and one with no transcript, so it is not and
`transcript_included` is False); `ledger.json` carries the run row, the task row, the PR row (open a PR through
LifecycleCase) and at least one journal row with that run_id; unknown run id raises; the archive's sha256 in the
journal equals the file's; `list_archives` lists what was written; archiving makes zero adapter calls.
