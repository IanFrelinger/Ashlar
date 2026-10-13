From: claude
To: all
Kind: claim
About: card-779, card-776

released. The owner is handing the rest of budgeted-autonomy phase F to Codex, as of 2026-10-13. Where things stand:

- **Merged:**
  - #784 (`dccba4e03`): the workstream's decisions log, in the controller README;
  - #785 (`af7ff471b`): the PR 5 pre-flight fixes. Host and container suites passed 513 tests, and `scripts/mutation-check.sh` killed 15 of 15.
- **Nothing in flight:** no open branch or PR of mine, and the lane worktrees are removed.
- **The live run has not happened.** It is card #779's last criterion and #776's task.
  - The ledger `ctl-pr5-live` is prepared under `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pr5\`. It holds four tasks queued: `pr5-codex`, `pr5-cursor`, `pr5-transfer` and `pr5-776`, the last for #776 on Claude.
  - The dry run says `would-launch pr5-codex`, reservation 12. The start backup is `backups\02-start-phase-f.db`.
  - `pr5.cmd`, which logs every command, and the outbox body are beside the ledger.
- **What to run:** `live-run-pr5.md` v2 on `claude/budgeted-autonomy-workspace`.
  - The owner's prerequisites: comment out `service_tier = "priority"` in the Codex `config.toml`, and log Cursor in.
  - Why each step is there: `preflight-pr5.md`, with `preflight-pr5-findings.txt` and `preflight-pr5-critic.txt` beside it.
- **Constraints the next agent inherits:**
  - The decisions log row of 2026-10-11 says worker launches stay on the owner's terminal.
  - Phase F still needs the handoff (`/handoff budgeted-autonomy F`, or the equivalent) after the run.
- **Open, out of this card:** a task chip for the CR artefact in `scripts/mutation-check.sh` on Windows (`line 448: arithmetic syntax error`; it does not change verdicts).
