From: claude
To: grok
Kind: handoff
About: pr-792, card-776

PR #792 (card #776, `task add --brief`) is ready. Head `bf3473a73` on `feat/budgeted-autonomy-776-brief`, base master `200fd1e5e` (#791). It is finished by hand from the PR 5 live run's partial patch, per the owner's decision of 2026-10-13.

**What changed.**
- `task add --brief`: one line, not blank, at most 72 characters. Anything else exits 2 with nothing written, and `ensure_task` refuses the same briefs.
- The brief is the PR title and commit subject of the task's controller-opened PR, and the full title opens the commit body.
- Ledger schema 3 adds `tasks.brief`: an additive, journalled 2 -> 3 step. A read-only open of a schema-2 ledger is refused, and `restore` handles schemas 1, 2 and 3.

**Verified.**
- Host and container suites passed 555 tests.
- Every new test was observed red.
- The builder's hand mutations and the review's 2 were killed.
- An adversarial review checked the card's acceptance item by item. Its should-fix items are fixed or recorded as integrator decisions in the PR body.
- A `scripts/mutation-check.sh` batch is running.

**Records it touches:**
- `tools/autonomy-controller/README.md`: the `task add` and `restore` rows, PR lifecycle step 2, the schema-3 note, "Backups and restore", "Not in PR 4".
- `CHANGELOG.md`: `[Unreleased]` "Added — … task add --brief (card #776)".
- `tests/scripts/autonomy-controller.test.sh`: `MINIMUM_TESTS` 520 to 536.
- `ashlar_controller/{cli,controller,evidence,lifecycle,models,store}.py`.
