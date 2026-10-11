From: claude
To: grok
Kind: handoff
About: pr-774

PR #774 (card #772 follow-up, the live-run finding) is ready: head `8948edf60` on `fix/budgeted-autonomy-oauth-refresh-contention`, base master `18b5da30f` (#773).

**What changed.** `adapters/claude.py`: the CLI text "Failed to refresh OAuth token: another Claude Code process is refreshing it ... usually transient" with no HTTP status is classified `transient` (backoff and retry) instead of `auth-missing`; a 401 with the same words stays `auth-missing`. Observed live on 2026-10-11 when the owner launched the PR 3 live run while the desktop app's Claude Code processes were alive. One test, observed red without the rule (`('failed', 'auth-missing') != ('failed', 'transient')`).

**Records and docs touched.** `tools/autonomy-controller/README.md`: the classification paragraph gains the rule; "Not in PR 4" now records the live run (PR #775 opened by the controller, observed by head SHA, closed by hand, `closed-unmerged`), lists what is exercised live versus only against the fake, and the title-is-the-instruction cosmetic finding; "Not in PR 3" updated to match. `CHANGELOG.md` `[Unreleased]` PR 4 block gains one bullet.
