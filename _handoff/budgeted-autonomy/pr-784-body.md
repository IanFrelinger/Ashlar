Records only. Card #779 (budgeted-autonomy phase F, close-out). CLAUDE.md requires an owner decision to land as a dated row in a decisions log before anything that depends on it. This PR is that log.

## What changed

- `tools/autonomy-controller/README.md` gains **"Decisions log (added 2026-10-12)"**, which holds the owner's decisions only:
  - 2026-10-11: the pilot is guarded; worker launches stay on the owner's terminal, with no allow rule. These are the phase D decisions, which until now were only in that handoff's addendum.
  - 2026-10-12: the owner's answers to phase E's open questions 1 to 5:
    - transfers stay operator-initiated;
    - only the log's rows are owner decisions;
    - Cursor is logged in and carries one live task;
    - the outbox's `gh` poster is exercised once, with one `status` post on #695;
    - card #776 is the live run's fourth task, on Claude.
  - The live run happens in one sitting, after the fix for the transfer defect found in pre-flight has merged.
- "Not in PR 5" now cites the transfer row.
- "Not in PR 4" said `provider_transfers_used` is "counted and not driven". That has been false since #781, and it is corrected.
- `CLAUDE.md` and `docs/DocsIndex.md` point at the log.

## Integrator decisions

The log lives in the controller README, beside "Operating policy defaults (integrator decisions)", because the workstream has no spec under `docs/specs/`. The owner can move it.

## Verified

- `bash scripts/check-docs-phantom-paths.sh`: all referenced paths exist.
- `python scripts/knowledge-graph/verify-knowledge-graph-current.py`: current. The rebuild after `git add` changed line endings only.
- `bash tests/scripts/autonomy-controller.test.sh` on this branch (host): `Ran 451 tests`, `OK (skipped=1)`, `autonomy-controller.test: 451 tests passed`.

No behaviour changes, so there is nothing to mutation-check.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

