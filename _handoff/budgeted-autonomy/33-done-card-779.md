From: claude
To: all
Kind: done
About: card-779

Card #779 (budgeted autonomy PR 5: Codex and Cursor adapters, bounded provider transfer) is done, and the card is closed. Its acceptance is met:
- PR 5 merged as #781 (`b130228ee`).
- The pre-flight fixes merged as #785 (`af7ff471b`).
- The live run is done and verified, and its findings are fixed in #791 (`200fd1e5e`). Under the owner's row of 2026-10-13, #779 closes once #791 merges.

The live run took place on 2026-10-13, on ledger `ctl-pr5-live`. The Codex agent operated it from the owner's terminal, on the owner's delegation (decisions log). Results:
- **Codex:** `run-4c1f88184756` completed, charged 6 items, PR #786.
- **Cursor:** `run-bdc63ec68d56`, `composer-2.5`, completed with exit 0 through the recorder, charged the full 12 requests, PR #787.
- **Transfer:** `run-82eb9983dca6` (Codex) was cancelled 5 s after launch and charged 12. Claude `run-effc72e96849` then completed in a fresh clone, charged 7 turns, PR #788. A second transfer was refused with exit 6 (`provider-transfers-exhausted`, journal row 34).
- **The three PRs** were closed unmerged; their checks were pending at the close and went green later. No merges, no repairs.
- **Outbox:** the `gh` poster's first live post (6149018991).
- **Card #776:** two capped attempts of 13 turns. The second reused the workspace and session, then the task parked at 26 of 36 turns.

Evidence on `claude/budgeted-autonomy-workspace`:
- `live-run-pr5-record.md`: the verified record;
- `live-run-pr5-verification.md`: every claim checked;
- `live-run-pr5-console.log`;
- `preflight-pr5.md`.

The six run archives stay local, because four of them hold the owner's account email. Code redacts it from now on.

Not exercised live: a controller merge, a repair round, check observation on a PR 5 PR, and Codex or Cursor resume.

My claim on card-779 ends with this `done`. My claim on card-776 stands until its PR merges.
