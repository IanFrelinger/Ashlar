From: claude
To: grok
Kind: handoff
About: pr-781

PR #781 (card #779, budgeted autonomy PR 5: Codex and Cursor adapters, bounded provider transfer) is ready. Head `41a0f3440` on `feat/budgeted-autonomy-codex-cursor-adapters`, base master `4003ccb0a` (#780).

**What changed.**
- `adapters/codex.py` runs `codex exec --json` and declares only what probes of the installed CLI showed on 2026-10-12. It never selects the bundled `.sandbox-bin` binary, which cannot run tools. Both a fresh run and a resume override the MCP servers, the sandbox and the approval policy. The model is `gpt-6-astra`; the earlier candidate `sol` is refused with HTTP 400.
- `adapters/cursor.py` runs `cursor-agent -p`. The CLI was logged out when probed, so the adapter declares only the route check, charges the full reservation, never resumes, and keeps the first logged-in run's output as evidence.
- `task transfer <id> --to <provider>` moves a task to another provider. It is bounded by `limits.provider_transfers_per_pr`, counted per pool before any launch. The previous writer is stopped and settled through its own adapter first. The task, PR, attempts and pool are kept. The new provider gets a fresh session and the compact handoff.
- `--adapter` is repeatable. `status`, `metrics` and the report show the transfers.

**Verified.**
- Host suite: 451 tests, `OK (skipped=1)`. Container suite: 451 tests, `OK`.
- Lane tests: 29 for Codex and 22 for Cursor.
- Mutation lines through `scripts/mutation-check.sh` are in the PR body: 15 for the transfer and 14 for the adapters and their wiring.
- What only CI and the owner's terminal can prove: no live task has gone through either new adapter yet. The command sheet is `live-run-pr5.md` on the storage branch.

**Records and docs it touches, for the drift audit.**
- `tools/autonomy-controller/README.md`:
  - the intro paragraph;
  - operator setup step 4;
  - the commands table (the `tick` row and the new `task transfer` row);
  - the stop-reason table (the `provider-transferred` row);
  - the new sections "Codex adapter", "Cursor adapter", "Provider transfer (PR 5)" and "Not in PR 5";
  - two layout rows.
- `tools/autonomy-controller/policy.example.json`: the Codex model and the `_comment`.
- `CHANGELOG.md` `[Unreleased]`: the PR 5 block.
- `docs/DocsIndex.md`: the controller row.
- `tests/scripts/autonomy-controller.test.sh`: the floor, from 360 to 430.
