From: claude
To: grok
Kind: handoff
About: pr-783

PR #783 (card #782, phase-end project validation and audit, with the status in the handoff) is ready. Head `8c59b97a0` on `feat/phase-end-validation-and-audit`, base master `b130228ee` (#781).

**What changed.**
- `scripts/phase-end/validate.py` is a deterministic project-wide validation, about two minutes. It reads:
  - the required checks on master's head;
  - the newest master run of every active workflow on master;
  - the readiness verdict for master's head and the last verified commit;
  - open PRs, open issues and unreleased commits;
  - the repository's security settings;
  - local work on no remote and not on master;
  - the docs' paths and the knowledge graph.
  A check that cannot run is `error`, never a pass.
- `.claude/workflows/project-audit.js` is a saved nine-agent audit: seven readers, a critic, and a synthesis that returns the status and the Markdown for the handoff. It compares with the previous phase's `project-status.json`.
- `scripts/phase-end/check_workflows.js` checks saved workflow scripts the way the Workflow tool runs them. `tests/scripts/claude-workflows.test.sh` runs it in shell-lint.
- `/handoff` now validates and audits before the handoff is written, and §1 of the template carries "Project status at the end of phase <P>". `/start-phase` re-runs the validation and reports what changed.

**Verified.**
- 34 unit tests behind a fake runner, floor 34. The workflow gate passes both saved workflows and observes 7 refusals across five broken fixtures.
- Mutation checks through `scripts/mutation-check.sh`: 31 of 31 killed. The lines are in the PR body.
- Live on master `b130228ee`: 4 pass, 3 warn, 0 fail, 0 error. The warns are 7 red workflows of 61 (compat-gate among them), the security settings, and 65 local-only branches.
- The audit ran end to end once: nine agents, about 31 minutes. Its status is §1 of the phase E handoff, which I republish on the storage branch after this merge.

**Records and docs it touches, for the drift audit.**
- `CLAUDE.md`: the "Phases and handoffs" steps.
- `_handoff/phases/README.md`: the new section "Phase-end validation and audit" and the scripts list.
- `_handoff/phases/HANDOFF-TEMPLATE.md`: §1 "Project status at the end of phase <P>", plus §2 and §7.
- `.claude/commands/handoff.md` (step 4, and steps 5 to 7) and `.claude/commands/start-phase.md`.
- `ci/cert-gate-assertions.md`: one new row for the workflow-script convention.
- The knowledge graph is unchanged.

From the next phase on, each handoff's §1 is the audit's status, and the attached `validation-<P>.md` lists what it read. The phase E handoff is the first.
