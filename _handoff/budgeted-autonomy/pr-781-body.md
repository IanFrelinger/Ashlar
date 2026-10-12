Card #779, budgeted autonomy PR 5 (roadmap #755): the Codex and Cursor adapters and the bounded provider transfer. Workstream `budgeted-autonomy`, phase E. Base master `4003ccb0a` (#780, the PR 4 pilot's findings). Built in three lanes from a written contract: two adapter lanes in isolated worktrees plus the integration, with the lane commits cherry-picked and the merged branch verified here.

## What changed

- **`ashlar_controller/adapters/codex.py`** runs the Codex CLI headless, as `codex exec --json`. The probes ran on 2026-10-12 against `codex-cli 0.162.0-alpha.17.2`; the reports are on the storage branch. It declares exact resume, cancel, structured output, transcript retention and the funding-route check. It does not declare a turn limit or usage observation: the CLI has no turn-limit flag and prints no allowance figure.
  - It never selects the bundled `~/.codex/.sandbox-bin/codex.exe`. That binary has no code-mode host beside it, and every tool call fails closed there.
  - Both a fresh run and a resume pass `-c mcp_servers={}`, the sandbox and the approval policy, so the operator's `config.toml` MCP servers and its `danger-full-access` sandbox never enter a run.
  - The model id is `gpt-6-astra`. The earlier candidate `sol` is refused with HTTP 400 on a ChatGPT login.
- **`ashlar_controller/adapters/cursor.py`** runs the Cursor agent CLI headless, as `cursor-agent -p --output-format stream-json`. The CLI was not logged in when probed, so the adapter declares only the funding-route check:
  - it refuses to launch while `status` says `Not logged in`;
  - it never passes `--resume`;
  - it charges the full reservation;
  - it keeps a real run's output, so the first logged-in run records the true shape.
  - It launches `node.exe index.js` directly instead of the `.cmd`, which would cut a multi-line prompt at its first line break.
- **`task transfer <id> --to <provider>`** (`Controller.transfer_task`) moves a task to another provider. It runs in this order, so a refusal changes nothing:
  1. It validates the target. It refuses a provider that is unknown, disabled by the policy or by the operator, already the task's provider, or counting a different unit. It also refuses a done task.
  2. It checks the bound. At most `limits.provider_transfers_per_pr` transfers are allowed, counted per budget pool from the journal before any launch.
  3. It stops and reconciles the previous writer. An uncertain launch is reconciled through its own adapter. A run that already ended is settled with its real outcome. A running run is cancelled through its own adapter. If that adapter is not registered, the transfer is refused.
  4. It makes the move in one transaction. The task keeps its PR row, attempts and pool, and the PR row mirrors the transfer count.

  The new provider never resumes another provider's session. It gets a fresh clone and the compact handoff, plus one line naming the previous provider and run.
- **CLI and reporting.** `--adapter` is repeatable and accepts `codex` and `cursor`. `status`, `metrics` and the report show the transfers. `status` and `evidence archive` find a Codex run's session file through `adapters.locate_run_transcript`.
- **Records.** The README gains Codex and Cursor adapter sections, a "Provider transfer (PR 5)" section and "Not in PR 5". CHANGELOG and DocsIndex are updated. The example policy's Codex model is now `gpt-6-astra`. The test floor rises from 360 to 430.

## Verification

- Host suite: `Ran 451 tests`, `OK (skipped=1)`. Container suite (`bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'`, the committed tree at `41a0f3440`): `Ran 451 tests`, `OK`. The transfer code is unchanged between `d74f5c228` and `41a0f3440`; only `run_evidence`'s transcript lookup moved to `locate_run_transcript`, which `w1` covers.
- Lane A (Codex) has 29 tests; Lane B (Cursor) has 22; the transfer has 15.
- Each lane also hand-applied mutations before committing and watched them fail: six for Lane A and twelve for Lane B.
- Mutation checks through `scripts/mutation-check.sh` with the controller's runner. The transfer ran against `d74f5c228`; the adapters and the transcript wiring ran against `41a0f3440`. The output is verbatim:

```
mutation t1-transfer-skips-pr-mirror: KILLED red=failed:2/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t2-transfer-skips-the-stop: KILLED red=failed:2/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t3-transfer-always-cancels: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t4-transfer-no-adapter-check: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t5-transfer-no-reconcile: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t6-bound-off-by-one: KILLED red=failed:5/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t7-count-per-task-not-pool: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t8-resume-across-providers: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t9-transfer-note-dropped: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t10-unit-check-dropped: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t11-operator-disable-ignored: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t12-state-always-queued: KILLED red=failed:2/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t13-fake-not-always-registered: KILLED red=failed:6/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t14-cli-transfer-not-dispatched: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation t15-metrics-transfers-zero: KILLED red=failed:1/399 green=passed:398/399 ref=d74f5c22844f0dd423ec67672b14f44b2f2b27f1 runner=override
mutation a1-codex-stdin-left-open: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation a2-codex-model-400-not-recognised: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation a3-codex-error-items-charged: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation a4-codex-fresh-run-keeps-mcp: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation a5-codex-resume-inherits-sandbox: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation a6-codex-hostless-binary-accepted: KILLED red=failed:2/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation a7-codex-api-key-not-refused: KILLED red=failed:2/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation b1-cursor-logged-out-accepted: KILLED red=failed:2/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation b2-cursor-turns-charged-zero: KILLED red=failed:10/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation b3-cursor-auth-error-not-classified: KILLED red=failed:2/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation b4-cursor-api-key-ignored: KILLED red=failed:5/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation b5-cursor-tree-not-killed: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation b6-cursor-empty-stdout-completed: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
mutation w1-transcript-codex-branch-dropped: KILLED red=failed:1/451 green=passed:450/451 ref=41a0f3440c647e5f119eeaadd03c1395e572f4c7 runner=override
```

## What only the owner's terminal can prove

No live task has gone through either new adapter yet, because this session's classifier refuses worker launches. The command sheet `live-run-pr5.md` on the storage branch covers two runs:

- one small real task through `--adapter codex`, which is logged in;
- one through `--adapter cursor`, after `cursor-agent login`.

The Codex CLI's shapes were probed live; the controller's own launch through it was not.

## Integrator decisions (no owner rows)

- Transfers are operator-initiated only. Nothing transfers a task automatically, because that would be a fallback launch on another provider's allowance.
- The transfer count lives per budget pool, in the journal, and is mirrored on the open PR row.
- A target counting a different unit is refused, because units are never converted. The example's Cursor provider counts `requests`.
- Codex `consumed_units` is the number of completed non-error items. A Cursor run is always charged its full reservation.
- Codex resolver order: `codex` on PATH, then the newest app binary that has a code-mode host, then the plugin app-server copy.
- The Cursor launcher bypasses the `.cmd` shim.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
