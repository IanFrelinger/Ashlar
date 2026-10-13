From: claude
To: grok
Kind: handoff
About: pr-794, budgeted-autonomy-close

PR #794, the budgeted-autonomy close-out's cleanup (decisions log row "Workstream", landed by #793), is ready. Head `f011cf606` on `chore/budgeted-autonomy-close-cleanup`, rebased onto master `2d60ba8fc`.

**What changed.**
- `.gitattributes` pins `.claude/workflows/*.js` to `text eol=lf`. A CRLF checkout could not run `project-audit` by name.
- `scripts/phase-end/check_workflows.js` refuses a workflow that holds a control character other than tab and newline.
- `tests/scripts/claude-workflows.test.sh` asserts the attribute, and adds a sixth broken fixture: a CRLF script.
- `ci/cert-gate-assertions.md` row 82 names both rules.
- The controller README intro, the `outbox.py` row and "Not in PR 3" are corrected. So is the package docstring.
- `tools/autonomy-controller/PILOT-PR4.md` (#777) is removed.
- `CHANGELOG.md` gets an `[Unreleased]` "Fixed" entry.

**Verified.**
- The test was observed red on CRLF copies, with the fix in its message, and green on LF copies.
- `scripts/mutation-check.sh` killed 4 of 4 at `248c0886c`, which is this head before the rebase. The rebase only added #793's six rows:
  - the attribute dropped;
  - the attribute set to `crlf`;
  - the control-character check skipped;
  - CR allowed.
- An adversarial review's findings are fixed here.
- Docs paths: 0 missing. Knowledge graph: current.

**Records it touches:** `.gitattributes`, `ci/cert-gate-assertions.md` (row 82), `CHANGELOG.md`, `tools/autonomy-controller/README.md` (:3-4, :37, "Not in PR 3"), `tools/autonomy-controller/ashlar_controller/__init__.py` (docstring), `scripts/phase-end/check_workflows.js` and `tests/scripts/claude-workflows.test.sh`. `PILOT-PR4.md` is deleted.

**After merge:** an existing Windows clone keeps its CRLF copies until `rm <file> && git checkout -- <file>`. I will run that in the session checkout.
