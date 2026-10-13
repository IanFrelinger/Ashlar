Closes #776. Budgeted-autonomy phase F. The owner decided on 2026-10-13 that #776 is finished by hand by Claude as a normal PR (decisions log, #789). The work starts from the partial edits that the controller's two capped Claude attempts left in the PR 5 live run: `card-776-partial.patch` on `claude/budgeted-autonomy-workspace`, 6 files, +33/-12, no tests.

## What changed

- **`task add --brief <text>`:** an optional short title. It must be one line, not blank, and at most 72 characters. Anything else is a usage error (exit 2) before the state is opened, so nothing is written. `Controller.ensure_task` refuses the same briefs before anything is written. One check, `models.brief_problem`, serves both.
- **PR title and commit subject.** The brief, when present, is the PR title and the commit subject of the task's controller-opened PR (`lifecycle.subject_line`). The full title opens the commit body. Without a brief both are the title, as before. The worker's prompt is the full title either way (`implementation_prompt` is unchanged).
- **Ledger schema 3** adds `tasks.brief` in one additive, journalled step (`schema-migrated` 2 -> 3), on the first read-write open:
  - a read-only open of a schema-2 ledger is refused until then, as schema 1 already was;
  - `restore` accepts schemas 1, 2 and 3 and migrates the older ones;
  - `evidence.KNOWN_SCHEMA_VERSIONS` includes "3".
- **Docs:** README (the `task add` row, PR lifecycle step 2, the restore row and bullet, the schema note, "Not in PR 4") and the CHANGELOG. `MINIMUM_TESTS` goes from 520 to 536.

## How it was built

A builder agent finished the patch in an isolated worktree, and an adversarial reviewer checked it in its own clone. The reviewer reproduced every acceptance item: a 73-character brief exits 2, a real schema-2 ledger copy from the live run is migrated, a schema-2 backup restores, and a fake-lifecycle PR carries the brief as its title and subject.

Its should-fix items are fixed here:
- **Briefs that break the subject.** A brief with a line break, or of only whitespace, was accepted and broke the PR title or the commit subject. It is now refused, with tests and two killed mutations.
- **Files outside `tools/autonomy-controller/`.** See the integrator decisions below.

## Integrator decisions (no owner row)

- **The card's "nothing outside `tools/autonomy-controller/` changes"** is not met to the letter: `CHANGELOG.md` and `tests/scripts/autonomy-controller.test.sh` change. Repo convention requires both: every PR adds an `[Unreleased]` entry, and the test script says "Raise it when you add tests". The owner's row also says "as a normal PR".
- **An unusable brief exits 2** (a usage error, through argparse), not 1. The partial patch had exit 1, after the state was opened.
- **A padded one-line brief is kept as given**, without stripping.

Known, not changed: the 1 -> 2 and 2 -> 3 migrations read the schema version outside their transaction. Two processes opening the same old ledger read-write at once could each journal a `schema-migrated` row; the column is added once. This pattern predates this PR.

## Verified

- **Host suite** at `bf3473a73`: `Ran 555 tests`, `OK (skipped=1)`. **Container suite** (`scripts/test-in-container.sh`) at `bf3473a73`: `Ran 555 tests`, `OK`.
- **Observed red:** the 8 builder tests were observed failing on the source without the feature: 5 failures and 3 errors, with the messages in the builder's report. The 2 review tests were red under their mutations.
- **Hand mutations killed:** the builder's 9 or more (CLI length check and boundary, brief not passed, ensure_task guard, INSERT drops brief, subject ignores brief, title fallback, ...) and the review's 2 (line break, blank).
- **The `scripts/mutation-check.sh` batch** (4 mutations at `bf3473a73`) is in a comment below.
- `scripts/check-docs-phantom-paths.sh`: 0 missing. Knowledge graph: current.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
