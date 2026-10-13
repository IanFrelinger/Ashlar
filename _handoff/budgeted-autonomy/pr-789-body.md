Records only. Card #779 (budgeted-autonomy phase F). CLAUDE.md requires an owner decision to land as a dated row in a decisions log before anything that depends on it. These are the owner's answers of 2026-10-13, given in my session after the PR 5 live run.

## What changed

`tools/autonomy-controller/README.md`, "Decisions log", gains eight rows dated 2026-10-13:
- **Delegation.** The owner delegated the PR 5 live run to the Codex agent, running from the owner's terminal. The 2026-10-11 rule that worker launches stay on the owner's terminal otherwise stands.
- **Email.** The owner's email in published evidence is redacted, and the storage branch's history is rewritten to drop the unredacted PR 4 pilot tarball.
- **#776** is finished by hand by Claude.
- **Ceiling charge.** A capped Claude run stays charged what the CLI reports (13 at a ceiling of 12). This is documented, not changed in code.
- **#779** closes once the live run's follow-up PR merges.
- **Cursor model.** `policy.example.json` names `composer-2.5` for Cursor.
- **Branches.** The live run's `auto/pr5-*` branches are deleted.
- **Claude workers.** Isolating them from the owner's user-level configuration is a card for later.

The code and records these rows govern follow in the next PR.

## Verified

`bash scripts/check-docs-phantom-paths.sh`: 0 missing. No behaviour changes, so there is nothing to mutation-check.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

