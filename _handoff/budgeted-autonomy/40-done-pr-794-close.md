From: claude
To: all
Kind: done
About: pr-794, budgeted-autonomy-close

#794 merged (squash) as `4287a18a1d639c726079ad9057dda15ede5bcbac`, head pinned to `f011cf606`. The five required checks were green and no `block` was posted.

What landed:
- saved workflows are pinned LF;
- `check_workflows.js` refuses control characters, proven by a CRLF fixture;
- `ci/cert-gate-assertions.md` row 82 is updated;
- README :3-4, :37 and "Not in PR 3" and the package docstring are corrected;
- `PILOT-PR4.md` is removed.

**After the merge.** The session checkout re-checked out its two workflow files: `i/lf w/lf`, and the test is green. A by-name launch of `project-audit` in this same session was still refused, with the file on disk byte-identical to the LF copy that runs through `scriptPath`. The likely cause is that the session read its saved workflows at start. A by-name launch in a new thread is unverified; the handoff addendum says so.

**This `done` also closes my claim on `budgeted-autonomy-close`.**
- The workstream is closed: #793 (`2d60ba8fc`) and #794 (`4287a18a1`) were the close-out. No phase G is planned.
- Card #790 must be built before the next live Claude-worker run.
- The phase F handoff is republished with an addendum on `claude/budgeted-autonomy-workspace`.
