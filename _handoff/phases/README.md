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
4. Write the handoff from [`HANDOFF-TEMPLATE.md`](HANDOFF-TEMPLATE.md).
5. Publish it with `scripts/handoff-publish.sh`, send the file to the owner, and notify them.
6. Stop. The next phase starts in a new thread.

## Starting a phase: `/start-phase <workstream>`

The full steps are in `.claude/commands/start-phase.md`. In short:
1. Fetch the handoff: the attached file, or `scripts/handoff-fetch.sh --workstream <workstream>`.
2. Read it, then everything its "Read these first" section names.
3. Check it against the live repo, and report anything that differs before acting.
4. Restate the phase's scope and exit criteria.
5. Execute the phase. End with `/handoff`.

## Scripts

- **`scripts/handoff-publish.sh`** `--workstream <w> --phase <P> --file <handoff.md> [--attach <file>]...`
  - Commits the handoff, and any attachments, to the storage branch and pushes it.
  - Uses git plumbing with a private index, so the checkout and the working tree never change.
  - Unchanged content makes no commit. Earlier phases' files are kept.
- **`scripts/handoff-fetch.sh`** `--workstream <w> [--phase <P>] [--out <file>] [--list]`
  - Prints or saves the latest handoff, or a given phase's.
- **Test:** `tests/scripts/handoff-scripts.test.sh`, which runs in `shell-lint` through `scripts/ci/run-repo-gates.sh`.

## Why a storage branch, not master

A handoff is a working note for the next thread. It is not product documentation, and merging one per phase would cost a PR and a CI cycle each time. The storage branch keeps every phase's handoff, with its history, and stays out of master.

Decisions the owner makes still go into the durable records on master, such as the spec's decisions log, through normal PRs.
