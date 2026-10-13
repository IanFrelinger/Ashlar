The budgeted-autonomy close-out's one cleanup PR (owner decision of 2026-10-13, decisions log row "Workstream", landed by #793).

## What changed

- **Saved workflows stay LF.** `.gitattributes` pins `.claude/workflows/*.js` to `text eol=lf`. With `core.autocrlf=true`, as on this Windows host, they were checked out CRLF. The Workflow tool then refused `project-audit` by name: "script contains control characters that would be hidden in the approval dialog". So the phase F handoff had to run an LF copy through `scriptPath`.
- **The checker refuses control characters.** `scripts/phase-end/check_workflows.js` now refuses a workflow that holds a control character other than tab and newline. It names the code point and the line, and prints the fix: `rm <file> && git checkout -- <file>`. Before, the checker printed `ok` for a CRLF workflow that the tool refuses.
- **The test.** `tests/scripts/claude-workflows.test.sh`:
  - asserts every `.claude/workflows/*.js` is pinned LF (`git check-attr eol`);
  - adds a sixth broken fixture, a valid script with CRLF line ends, which must be refused for that reason.
- **The registry row.** `ci/cert-gate-assertions.md` row 82 names both new rules.
- **The controller's docs.** In `tools/autonomy-controller/README.md`:
  - The intro says the controller takes tasks the operator adds with `task add`, optionally naming a card, and that nothing reads cards from GitHub. The package docstring in `ashlar_controller/__init__.py` says the same.
  - The `outbox.py` row says the operator enqueues and flushes the posts, and the tick posts nothing itself.
  - "Not in PR 3" says what PR 5 added: the Codex and Cursor adapters, and the transfer driven by `task transfer`.
- **The pilot file.** `tools/autonomy-controller/PILOT-PR4.md`, the PR 4 pilot's throwaway file (#777), is removed. Nothing references it.
- **The changelog.** `CHANGELOG.md` gets an `[Unreleased]` "Fixed" entry.

## After merge

Merging does not rewrite existing Windows checkouts: git rewrites no file whose blob is unchanged, and the `.js` blobs did not change. So each active Windows clone needs the fix once: `rm <file> && git checkout -- <file>` for both workflows. Until then `claude-workflows.test.sh` fails there, with that command in its message. I will run it in the session checkout.

## Verified

- **Observed red, then green, by hand on this host.**
  - Before the checker change, the test's first byte check, which used `grep $'\r'`, passed on CRLF copies. Git for Windows' grep cannot see a CR before a line end. That check was replaced, first by `tr`/`cmp` and then by the checker rule.
  - The `tr`/`cmp` version went red on the CRLF working copies and green after the re-checkout.
  - The final version goes red on a CRLF copy of `project-audit.js`, with `holds a control character (U+000D at line 1)` and the fix. It goes green on the LF copy.
- **`scripts/mutation-check.sh`**, with `ASHLAR_MUTATION_RUNNER="bash scripts/phase-end/mutation-runner.sh"` (the `test_validate.py` suite plus this test), at `248c0886c`:
  ```
  mutation close-m1-attribute-dropped: KILLED red=failed:1/35 green=passed:35/35 ref=248c0886cd5aaf4b29de6d6725f7251baee505cd runner=override
  mutation close-m2-attribute-crlf: KILLED red=failed:1/35 green=passed:35/35 ref=248c0886cd5aaf4b29de6d6725f7251baee505cd runner=override
  mutation close-m3-control-check-skipped: KILLED red=failed:1/35 green=passed:35/35 ref=248c0886cd5aaf4b29de6d6725f7251baee505cd runner=override
  mutation close-m4-cr-allowed: KILLED red=failed:1/35 green=passed:35/35 ref=248c0886cd5aaf4b29de6d6725f7251baee505cd runner=override
  ```
  - m1 and m2 failed on the attribute check: `eol: unspecified` and `eol: crlf`.
  - m3 and m4 failed on "the checker accepted the broken fixture crlf.js".
- **An adversarial review** (portability, records, breakage) found these, and all are fixed here:
  - the registry row was not updated;
  - the checker did not enforce the rule its header claims;
  - the README's card wording was wrong.

  The reviewer also ran the attribute check on Linux (Ubuntu 24.04, `autocrlf` unset), checked every output form of `git check-attr`, ran the test from another cwd and from a worktree, and confirmed the `.js` blobs are unchanged, so no renormalize is needed.
- **Other checks:** `scripts/check-docs-phantom-paths.sh` reports 0 missing. The knowledge graph is current; a rebuild changed only line endings in this checkout.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
