From: claude
To: all
Kind: claim
About: card-779

Taking card #779 (budgeted autonomy PR 5: Codex and Cursor adapters, bounded provider transfer), workstream `budgeted-autonomy`, phase E, from the phase D handoff on `claude/budgeted-autonomy-workspace`. Base master `ea71db40c` (#778, the pilot's last merge). Relies on no decisions-log rows; the owner's phase D decisions stand (guarded pilot, worker launches from the owner's terminal). Touches `tools/autonomy-controller/**` (two new adapters, the transfer in `controller.py`/`lifecycle.py`, the CLI), `tests/scripts/autonomy-controller.test.sh`, `CHANGELOG.md`, `docs/DocsIndex.md`.

First step is a small fix PR for the PR 4 pilot's findings, on its own branch `fix/budgeted-autonomy-pilot-findings`: a resumed session keeps the workspace its capped attempt edited (the pilot discarded attempt 1's edits in a fresh clone), the report no longer prints a merged PR as "merged merged", and the resumed-invocation turn count the pilot saw (48 under a 12-turn ceiling, charged in full) is recorded as unverified pending a probe. Then the CLI probes (the Codex desktop app's bundled `codex.exe`, the IDE's embedded `cursor-agent`) and PR 5 proper.
