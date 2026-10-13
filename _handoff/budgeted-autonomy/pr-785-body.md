Card #779 (budgeted-autonomy phase F, close-out). Before the owner ran the PR 5 live run (`live-run-pr5.md`), a pre-flight checked the sheet against master `dd38c007c`. It was a workflow of five lanes, two verifiers per finding and a completeness critic, 124 agents in all, and launched no workers. It found defects in PR 5's own code that would have spoiled the run. This PR fixes them. The owner decided on 2026-10-12 that the live run happens after this merges (decisions log, #784).

## What the pre-flight found, and the fix

| Finding | What was wrong | Fix |
|---|---|---|
| D1 | `task transfer` had no `--gh`. A Codex run that had already finished (a run of this size takes under a minute) was settled `done` with no commit, push or PR, then re-queued on the new provider. | `--gh none\|cli` and `--workspaces`. Without the lifecycle a completed writer is refused (`transfer-needs-pr-lifecycle`, nothing changed); with it the run is proposed before the move. The reconcile covers only this task's runs, and both of its loops refuse. The journal records how each run stopped. The output says `moved`. |
| SEQ-1 / T3 / E1 | Every `tick` is its own process, so a Cursor run's exit code was lost. A clean run settled `no-final-result`, or was re-queued when the model's text mentioned "network" or "rate limit". | `adapters/_exit_recorder.py` (new) runs the CLI and writes its exit code to the run directory; a poll from any process reads it first. The recorder ignores Ctrl+C while it waits. |
| ENV-3 | The owner's Codex `config.toml` sets `service_tier = "priority"` ("2x speed, increased usage"), applied to every run although the policy says standard. | Refused before launch, naming the file, the line and the operator step (no standard value is observed, so no override is passed). The manifest records it. |
| C8 | The owner's plugins (computer-use, browser, chrome) and MCP servers reached unattended Codex runs. `-c mcp_servers={}`, passed since PR 5, removes nothing. | `-c features.plugins=false`, `-c notify=[]`, and `-c mcp_servers.<name>.enabled=false` per declared server. Observed without a model call (`codex debug prompt-input`, `codex mcp list`). |
| T4, T5 | `run cancel` had no `--adapter`: it settled a live run `cancelled` and left the worker running. A reused pid made a finished run poll as running, and a transfer then killed an unrelated process tree. | `run cancel --adapter`; a live run without it is refused. Codex, Claude and Cursor never cancel a run whose stream or exit file shows it ended. A cancel waits up to 5 s for a run still exiting, and never settles a completed writer unproposed. |
| SEQ-5, T8 | `run settle` of a settled run still moved its task; `--stop-reason completed` and `readiness evaluate` could finish a task with nothing proposed. | Both refused; a passing readiness report on a task with an open PR changes nothing. |
| E8 | A failed push or `gh pr create` after a completed run printed a traceback and stranded the task `running`. The next tick launched another task. | `propose-failed` is journalled, and the task is `needs-reconcile`. Nothing else launches (`propose-failed-blocks-dispatch`) until the operator resolves it. |
| C1 | GitHub lists check runs newest first, and the verdict took the last in the list, i.e. the oldest. A re-run green check still read red and launched a paid repair. | The check run with the largest id decides (observed: ids descend on #784's head). |
| ENV-7 | Every provider's commit carried `Co-Authored-By: Claude Fable 5.1`. | `Worker: <provider> <model>`; the Claude trailer only for a Claude run; the PR body names the worker. |
| C7 | Codex transcripts in evidence archives carry the ChatGPT account ids, and the storage branch is public. | The archived copy drops `creator_user_id` and `creator_account_id` (MANIFEST and journal say so). The PR 3 and PR 4 evidence already published was scanned: no account ids, emails or tokens. |
| C9 | A worker that commits its own edit (Cursor runs with `-f`) read as `no-change-to-propose`, stranding its commit. | Commits past the starting commit are folded into one controller commit. |
| SEQ-6, REPORT-CP1252, T10 | The dry run used other rules than the tick; `report > file` crashed on a non-cp1252 character; the fake Cursor CLI raced the test on Windows (about 12% of runs). | Dry run mirrors the tick (give it the tick's `--adapter`s); redirected output is UTF-8; the fake records the child pid in its own file (125/125 runs passed). |

## How it was built

Three lanes in isolated worktrees, each with an adversarial reviewer on its commit, plus integration work on this branch:
- **A:** controller and CLI.
- **B:** the Cursor adapter.
- **C:** the Codex and Claude adapters.

The reviewers' must-fix and should-fix items are fixed here, among them a cancel path that settled a completed writer `done` unproposed, and a race between lane C's no-op cancel and lane A's post-cancel poll.

## Integrator decisions (no owner row)

- A completed writer that cannot be proposed (no `--gh cli`) is refused, not settled, by `task transfer`, `run cancel` and `run settle`.
- `propose-failed` blocks dispatch until the operator moves the task.
- A configured Codex `service_tier` refuses the launch. The refusal charges nothing but counts an attempt, as the existing speed refusal does.
- `notify=[]` is passed although its effect is not observed; it loads without error.
- After a cancel, a run still polling RUNNING is polled 10 more times, 0.5 s apart, before it is settled `cancelled`.

## Verified

- **Host suite** at `c1aff5246`: `Ran 513 tests`, `OK (skipped=1)`, `autonomy-controller.test: 513 tests passed`. `MINIMUM_TESTS` goes from 430 to 490.
- **Container suite** (`scripts/test-in-container.sh`) at `c1aff5246`: `Ran 513 tests`, `OK`.
- **Observed red:**
  - Every new test was observed failing on the unfixed code. The lanes did this by swapping in the base files; I did it by hand mutation for the integration fixes.
  - Hand mutations were killed: lane A 20, lane B 10, lane C 11; integration 4 (review A), 4 (C8), 3 (review C), and 1 or 3 each for attribution, check order, redaction, worker-commit fold, the dispatch guard and the recorder's SIGINT.
  - The `scripts/mutation-check.sh` batch (one per finding, at `738198fe2`) is listed in the comment below.
- **Reviews:** each lane's adversarial reviewer ran on its commit in a separate clone. Lane A's must-fix and should-fix items, lane B's should-fix items and lane C's should-fix items are fixed here.
- `scripts/check-docs-phantom-paths.sh`: 0 missing. `verify-knowledge-graph-current.py`: current.
- **The owner's live-run wrapper** (`pr5.cmd`, on the storage branch) was exercised through `cmd.exe` against a copy of the live ledger. That covered the log and exit codes, the one-line tick-and-transfer form, and the dry run with `--adapter codex --gh cli` (`would-launch pr5-codex`, reservation 12.0).

## Not verified

- No real `codex`, `cursor-agent` or `claude` worker ran: that is the owner's live run, next, from the corrected sheet.
- The effect of `-c notify=[]`.
- Cursor's real stream shape (it is still not logged in).
- A Codex thread first run under priority and then resumed after the tier line is commented out.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

