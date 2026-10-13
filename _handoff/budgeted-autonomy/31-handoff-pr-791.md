From: claude
To: grok
Kind: handoff
About: pr-791

PR #791 (card #779, phase F) is ready. Head `da496656b` on `fix/budgeted-autonomy-live-run-findings`, base master `f2ddbee01` (#789: the owner's 2026-10-13 decisions).

**What changed.** These are the fixes for what the PR 5 live run exposed:
- evidence archives redact the owner's account email: the Claude `session_context` attachment, the Cursor manifest `status_text`, and a safety net over every text member;
- the Cursor adapter masks the logged-in status text, and keeps the last `result` event as data;
- the Claude allowance is the binding window (the higher of five-hour and seven-day);
- the report's permitted attempts honour the budget and the policy;
- the Codex fake matches the real stdin line;
- the records the run made stale.

Separately, and not in the PR, the storage branch's history was rewritten to drop the unredacted PR 4 pilot tarball (6151763815).

**Verified.** Host and container suites passed 539 tests. Every lane test was observed red, the hand mutations were killed, and three adversarial reviews returned `ok`, with their should-fix items fixed. A `scripts/mutation-check.sh` batch is running; its results will follow on the PR.

**Records and docs it touches, for the drift audit:**
- `tools/autonomy-controller/README.md`:
  - the Commands table (`status`, `outbox`);
  - Budget accounting: capped runs are charged ceiling + 1, and the "(N permitted)" figure;
  - "Claude adapter": the stream, the allowance window, the `session_context` record, worker configuration (#790), `num_turns`;
  - "Codex adapter": the stdin line, `file_change`, the live cancel, accounting;
  - "Cursor adapter": the first logged-in run, `result_event`, `composer-2.5`;
  - Evidence archives: the redaction layers and their gaps;
  - "Provider transfer (PR 5)": exercised live, and `provider_transfers_used`;
  - "Not in PR 5": the live-run record, operator deviations, known limits;
  - "Not in PR 4" and "Not in PR 3".
- `CHANGELOG.md`: `[Unreleased]` "Fixed — … PR 5 live-run findings (phase F)".
- `tools/autonomy-controller/policy.example.json`: Cursor model `composer-2.5` (decisions log 2026-10-13).
- `tests/scripts/autonomy-controller.test.sh`: `MINIMUM_TESTS` 490 to 520.
- Docstrings in `adapters/{codex,cursor,claude,__init__}.py` and `outbox.py`.
