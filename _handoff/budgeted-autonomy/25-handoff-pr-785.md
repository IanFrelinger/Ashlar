From: claude
To: grok
Kind: handoff
About: pr-785

PR #785 (card #779, phase F) is ready. Head `c1aff5246` on `fix/budgeted-autonomy-preflight-findings`, base master `dccba4e03` (#784).

**What changed.** A pre-flight of the owner-run PR 5 live run, with no worker launched, found defects in PR 5's own code. This PR fixes them before the owner runs it. In outline:
- `task transfer` gains `--gh`. A finished Codex run was being settled `done`, never proposed, and re-queued.
- A Cursor run's exit code now outlives the `tick` process (new `adapters/_exit_recorder.py`). Before, every live Cursor run would have settled `no-final-result`.
- Codex now refuses an operator `service_tier` (`operator-config-refused`, waiting in `blocked-permission`). It also keeps the operator's plugins and MCP servers out of every run: `-c mcp_servers={}`, passed since PR 5, removes nothing.
- `run cancel` gains `--adapter`. A cancel never settles a completed writer unproposed, and waits for a run that is still exiting.
- `run settle` and `readiness evaluate` refusals.
- A failed proposal is journalled `propose-failed` and blocks dispatch.
- The dry run follows the tick's rules.
- Redirected output is UTF-8.
- The newest check run decides.
- A worker's own commits are folded into the controller's commit.
- Commits and PRs name the worker; Claude is credited only for a Claude run.
- Evidence archives drop the Codex account ids.

**Verified.**
- Host and container suites: 513 tests, OK.
- Every new test was observed red. Hand mutations were killed across three lanes and the integration.
- Each lane had an adversarial review, and its must-fix and should-fix items are fixed.
- A `scripts/mutation-check.sh` batch is running (results to follow in the PR).

**Records and docs it touches, for the drift audit:**
- `tools/autonomy-controller/README.md`: the Commands table (`tick`, `run`, `task transfer`, `readiness evaluate`), the exit-code note, the stop-reason table (`propose-failed`, `operator-config-refused`), "Codex adapter" (plugins and MCP servers, service tier, finished runs), "Cursor adapter" (the exit recorder, cancel), "Claude adapter" (cancel), "PR lifecycle" steps 2 and 3, "Provider transfer (PR 5)" steps 3 and 4, evidence archives, "Not in PR 5".
- `CHANGELOG.md`: `[Unreleased]` "Fixed — … PR 5 pre-flight findings (phase F)".
- `tests/scripts/autonomy-controller.test.sh`: `MINIMUM_TESTS` 430 to 490.

No owner decision is needed. The integrator decisions are listed in the PR body.
