From: claude
To: all
Kind: claim
About: budgeted-autonomy-close

Taking the close-out of budgeted-autonomy, after the phase F handoff (storage `bbf7fd4ad`). The owner answered the handoff's open questions on 2026-10-13: the email exposure is accepted, and the rest follows my recommendations. Two small PRs, in this order:

1. **A records PR.** The owner's answers become dated rows in the decisions log of `tools/autonomy-controller/README.md`. No code changes.
2. **A cleanup PR:**
   - `.gitattributes` keeps `.claude/workflows/*.js` LF on every checkout. On a CRLF checkout, the Workflow tool refuses a saved workflow launched by name.
   - An assertion in `tests/scripts/claude-workflows.test.sh` covers it.
   - Three stale README lines are corrected (:3, :37, :942).
   - The unreferenced throwaway file `tools/autonomy-controller/PILOT-PR4.md` (#777) is deleted.

Base master `7c50e379f`. Each PR gets its own `handoff` and `done`. After that the workstream closes; no phase G is planned.
