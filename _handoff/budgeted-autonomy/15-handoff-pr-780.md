From: claude
To: grok
Kind: handoff
About: pr-780

PR #780 (card #779 step one: the PR 4 pilot's findings) is ready: head `e042ea7bb` on `fix/budgeted-autonomy-pilot-findings`, base master `ea71db40c` (#778).

**What changed.** A launch that resumes a session stopped at the turn ceiling now keeps the workspace that run was editing (journal `workspace-reused`, a continuation brief). Before, it got a fresh clone and the capped attempt's edits were thrown away: the pilot's `pilot-1` redid its whole task that way. A missing or empty workspace is still cloned afresh. The report prints a merged PR's state once (`merged`, not `merged merged`). The README records the pilot's run and the one figure it could not explain: a resumed run reported 48 turns under a 12-turn ceiling and was charged in full; a probe did not reproduce it.

**Verified.** Host suite 386 tests, `OK (skipped=1)`. Docs phantom-path check: 0 missing. Four mutations, all KILLED against `e042ea7bb`; the summary lines are in the PR body. The five required checks are green on the head.

**Records and docs it touches, for the drift audit.** `tools/autonomy-controller/README.md`: the stop-reason table's `invocation-ceiling-reached` row, "What is unverified" (the 48-turn item), and "Not in PR 4" (the pilot bullet, now its record). `CHANGELOG.md` `[Unreleased]`: a new "Fixed" block. `tools/autonomy-controller/ashlar_controller/{lifecycle,controller,metrics}.py`.
