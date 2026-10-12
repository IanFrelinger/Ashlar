# Phases and handoffs

**The owner's rule (2026-10-05): one thread per phase.** A workstream runs as a sequence of phases. Each phase runs in its own Claude Code thread. At the end of a phase, the thread writes a handoff, and the owner starts the next thread from it.

The procedure is mechanical on purpose:
- `/handoff` ends a phase;
- `/start-phase` begins one;
- two scripts store and retrieve the handoff, so nothing depends on a cloud session's scratchpad surviving.

## Names

| Thing | Convention | Example |
|---|---|---|
| Workstream | lowercase letters, digits and dashes | `spec-007-pr4` |
| Phase | letters and digits, in order | `A`, `B`, `C` |
| Storage branch | `claude/<workstream>-workspace`, never merged | `claude/spec-007-pr4-workspace` |
| Handoff path | `_handoff/<workstream>/handoff.md` on the storage branch, plus `handoff-phase-<P>.md` for each phase | `_handoff/spec-007-pr4/handoff-phase-B.md` |

## Ending a phase: `/handoff <workstream> <phase>`

The full steps are in `.claude/commands/handoff.md`. In short:
1. Check the phase's exit criteria from the phase map. If they are not met, the handoff names the blocker.
2. Push every in-flight branch to GitHub.
3. Gather the live facts: master and the merges this phase, open PRs, the agent-bus, the readiness verdict.
4. Validate and audit the whole project (see "Phase-end validation and audit" below).
5. Write the handoff from [`HANDOFF-TEMPLATE.md`](HANDOFF-TEMPLATE.md), with the project status in §1.
6. Publish it with `scripts/handoff-publish.sh`, with the validation and audit attached. Send the file to the
   owner and notify them.
7. Stop. The next phase starts in a new thread.

## Starting a phase: `/start-phase <workstream>`

The full steps are in `.claude/commands/start-phase.md`. In short:
1. Fetch the handoff: the attached file, or `scripts/handoff-fetch.sh --workstream <workstream>`.
2. Read it, then everything its "Read these first" section names.
3. Check it against the live repo, and report anything that differs before acting. This includes re-running
   the validation and comparing it with the handoff's.
4. Restate the phase's scope and exit criteria.
5. Execute the phase. End with `/handoff`.

## Phase-end validation and audit

Every phase ends with a check of the **whole project**, not only the workstream. It runs before the handoff is
written, and its result is the handoff's "Project status" subsection in §1. A phase handoff therefore also
reports on the release, the specs, the bug backlog, CI health and the roadmap. It names what moved since the
last phase, and it surfaces a gate that went red where nobody looks.

- **Validation** (deterministic, about two minutes): `scripts/phase-end/validate.py`. It checks:
  - the required checks on master's head, and the newest master run of every workflow, asked per workflow;
  - the readiness verdict for the head, and the last verified commit;
  - open PRs and how stale they are, and open issues by label;
  - the repository's security settings, and commits since the last release tag;
  - local work that is on no remote and not on master;
  - the docs' referenced paths and the knowledge graph.

  Each check is `pass`, `warn`, `fail`, `error` (could not run, never a pass) or `info`. It writes JSON and
  Markdown. `/start-phase` re-runs it to see what changed since the handoff.
- **Audit** (about 25 minutes, nine read-only agents): the saved workflow `.claude/workflows/project-audit.js`.
  - Seven readers score the project against its own definitions of done, with file-level evidence: the
    roadmap, the next release, the specs, the autonomy unlock, the bug backlog, CI health, and the workstream
    ending its phase.
  - A critic tries to refute the load-bearing claims and looks for gaps.
  - A synthesis returns a structured status and the Markdown section for the handoff. It compares against the
    previous phase's `project-status.json`, so every row says what changed.
- **Stored** on the storage branch with the handoff:
  - `validation-<P>.json` and `validation-<P>.md`;
  - `project-status-<P>.json`, and `project-status.json` (the latest, which the next audit compares against);
  - `project-audit-<P>.json` (every reader's full report and the critic's verdicts).
- **If the audit cannot run,** the handoff says so and why, and carries the validation alone. The status is
  never written from memory.

## Scripts

- **`scripts/handoff-publish.sh`** `--workstream <w> --phase <P> --file <handoff.md> [--attach <file>]...`
  - Commits the handoff, and any attachments, to the storage branch and pushes it.
  - Uses git plumbing with a private index, so the checkout and the working tree never change.
  - Unchanged content makes no commit. Earlier phases' files are kept.
- **`scripts/handoff-fetch.sh`** `--workstream <w> [--phase <P>] [--out <file>] [--list]`
  - Prints or saves the latest handoff, or a given phase's.
- **Test:** `tests/scripts/handoff-scripts.test.sh`, which runs in `shell-lint` through `scripts/ci/run-repo-gates.sh`.
- **`scripts/phase-end/validate.py`** `[--workstream <w>] [--phase <P>] [--json <file>] [--markdown <file>]`
  `[--no-fetch] [--skip-local] [--with-repo-gates] [--stale-days <n>]`
  - The phase-end validation. Exit 0 when no check is `fail` or `error`, 1 otherwise.
  - Tests: `scripts/phase-end/test_validate.py`, run by `tests/scripts/phase-end-validate.test.sh` in
    `shell-lint`.
- **`scripts/phase-end/check_workflows.js`**: checks that the saved workflows in `.claude/workflows/` parse
  the way the Workflow tool runs them and follow its rules. It runs in `shell-lint` through
  `tests/scripts/claude-workflows.test.sh`, which also proves it refuses broken scripts.

## Why a storage branch, not master

A handoff is a working note for the next thread. It is not product documentation, and merging one per phase would cost a PR and a CI cycle each time. The storage branch keeps every phase's handoff, with its history, and stays out of master.

Decisions the owner makes still go into the durable records on master, such as the spec's decisions log, through normal PRs.
