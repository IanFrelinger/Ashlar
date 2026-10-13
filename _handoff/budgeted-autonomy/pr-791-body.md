Card #779 (budgeted-autonomy phase F). The PR 5 live run of 2026-10-13 was operated by the Codex agent on the owner's delegation (decisions log, #789). It was verified against the ledger, the console log, the evidence archives and GitHub by a workflow of five readers and a synthesis. This PR fixes what that run exposed. The owner's decisions it relies on are the 2026-10-13 rows merged in #789.

## The live run, verified

| Task | Run | Outcome | Charged | PR |
|---|---|---|---|---|
| Codex | `run-4c1f88184756` | completed | 6 items | #786 |
| Cursor (`composer-2.5`) | `run-bdc63ec68d56` | completed, exit 0 through the recorder | the full 12 requests | #787 |
| Transfer, Codex | `run-82eb9983dca6` | cancelled 5 s after launch | 12 | none |
| Transfer, Claude | `run-effc72e96849` | completed, fresh clone | 7 turns | #788 |
| #776, attempt 1 | `run-5c155cbe6f82` | stopped at the 12-turn ceiling | 13 | none |
| #776, attempt 2 | `run-a062813c1dae` | resumed the session in the same workspace; stopped at the ceiling | 13 | none |

- A second transfer was refused with exit 6 (`provider-transfers-exhausted`).
- #776 parked at 26 of 36 turns.
- All three PRs were closed unmerged. Their checks were pending at the close and went green later.
- No merges, no repairs.
- The outbox posted once (6149018991).
- Six evidence archives and the end backup were written.

## What it exposed, and the fix

| | Defect | Fix |
|---|---|---|
| F1, F3 | The owner's account email was in four of the six archives. Every Claude transcript has a `session_context` attachment with `userEmail` and a rendered copy, and the Cursor manifest's `status_text` reads "Logged in as …". The same address was in the PR 4 pilot evidence already published on the public storage branch. | `evidence.py` redacts the `session_context` email and its rendered text and the manifest's `status_text`, then runs a safety net over every UTF-8 member. The noreply trailer and the reserved test domain are kept, and each change is named in MANIFEST.txt and the journal. The storage branch's history was rewritten with a redacted copy (owner decision; bus 6151763815). |
| F2 | The Cursor adapter stored the logged-in status text verbatim. | It is masked before it is stored or quoted. |
| F6 | Cursor's `result` event (subtype, is_error, session_id, request_id, token usage) lay past the first 2000 characters the adapter kept, so the ledger lost it. | It is kept as `result_event`: recorded data, not a charge. The capability values are unchanged. |
| F4 | Every Claude `rate_limit_event` carried five_hour 0.02 and seven_day 0.69, but the adapter recorded 2 percent, so the 25 percent reserve could not see the binding weekly window. | The allowance is the higher utilization of the windows present, and the window taken is named. |
| F7 | The real Codex CLI prints the stdin line even under DEVNULL, but the fake and its test assumed it does not. | The fake prints it; the test checks that stdin is DEVNULL and the run completes. |
| F8 | The report printed `2/3 (1 permitted)` for a pool that could not fund another invocation. | The figure is min(the attempts `budget.reserve` allows, capped by the policy, and whole invocations left). |
| Records | The README, docstrings and `policy.example.json` were stale in several places. | See the list below. |

The records changes:
- the live-run record replaces "no live task through either new adapter yet";
- the outbox `gh` post is exercised;
- Codex's `file_change` item;
- Cursor's first logged-in run;
- `composer-2.5` (owner decision);
- the Claude stream, and its workers' user configuration (card #790);
- capped runs charged ceiling + 1 (owner decision: documented, not changed);
- the transfer, the cancel and workspace reuse exercised live;
- the operator's deviations, stated as facts.

## How it was built

Three lanes in isolated worktrees, each adversarially reviewed in its own clone:
- **P:** archive privacy.
- **C:** the Cursor adapter.
- **O:** the other shapes.

All three reviews returned `ok`. Their should-fix items are fixed here: the attempts left capped by the policy, the other direction of the ceiling minimum, and the safety net keeping the test domain. Each fix has a test and a killed mutation. A records pass wrote the docs.

## Verified

- **Host suite** at `da496656b`: `Ran 539 tests`, `OK (skipped=1)`. **Container suite** (`scripts/test-in-container.sh`) at `da496656b`: `Ran 539 tests`, `OK`. `MINIMUM_TESTS` goes from 490 to 520.
- **Observed red:** every lane test was observed failing on the unfixed code (lane P 8/8, lane C and lane O per their reports). Hand mutations were killed: lane P 12, lane C 9, lane O 9, and the integration's review fixes 3 of 3. The `scripts/mutation-check.sh` batch (12 mutations at `da496656b`) is in a comment below.
- **The redacted PR 4 bundle:** the controller's redaction found 10 addresses before and 0 after. A scan of every blob reachable from the rewritten storage branch found 0.
- `scripts/check-docs-phantom-paths.sh`: 0 missing. Knowledge graph: current. The diff adds no address except the `noreply@anthropic.com` trailer named in the README.

## Not done here

- The reviewers' coverage nits (the stderr-blocker test, fixture variants, NaN and bool allowance subtests, the surrogate-encode risk in the archive writer). They are recorded in the phase F handoff.
- #776's own PR comes next.
- Merge, repair and check observation were not exercised live.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

