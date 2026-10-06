# <Workstream> handoff: end of phase <P>, start of phase <P+1>

*Written <date and time UTC> at the end of phase <P>. Start the next thread with the prompt in §9, or with `/start-phase <workstream>`.*

<!--
How to fill this template (delete this comment in the real handoff):
- Every section is required. Write "none" rather than dropping a section.
- State facts with their evidence: commit SHAs, PR numbers, branch names at their SHAs, check names and
  results, counts. A claim the next thread cannot verify is a liability.
- Carry forward everything still true from the previous handoff (decisions, environment facts, queued work),
  and add what this phase learned. The next thread reads only this file and what it points to.
- Write for a reader with none of this thread's context.
-->

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end each phase with `/handoff <workstream> <phase>`;
- start each phase with `/start-phase <workstream>`.

The handoff is published to `claude/<workstream>-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff.

## 1. Where things stand

- **Master:** `<sha>`.
- **Merged this phase, and earlier in the workstream:** a table of PR, what it does, and its merge SHA.
- **Readiness verdict for master's head:** `<verified/partial/...>`, with the run link.
- **Agent-bus (#695):** the open items, or "nothing open".
- **This phase's exit criteria:** met, or not met with the exact blocker.

## 2. Read these first

- **In the repo:** `CLAUDE.md`, the spec, and the records the next phase touches.
- **On the storage branch:** design notes, lane rules and PR-body drafts, each with its path.

## 3. Decisions

- Every owner decision this workstream relies on, with where it is recorded (for example, the spec's decisions log).
- Questions still open, and whether they block the next phase.

## 4. Plan and live status

- **Phase map:** a table of phase, scope and "ends when", with the current phase marked done and the next one in bold.
- **Every work item:** branch at its SHA, PR number, state (built / checked / PR open / merged), and any check findings still to fix.
- **The next phase,** spelled out: its scope, its order, and its exit criteria.

## 5. How to do the next phase's routine work

The repeatable steps, for example how to take a built lane to a merged PR: verify, mutation-check, PR, bus handoff, merge, done.

## 6. Environment facts

Everything a fresh container needs: tooling, gotchas, known pre-existing failures, and what changed this phase.

## 7. Queued work

Follow-ups, deferred defects, and task cards outside the phase plan, with the owner's choices noted.

## 8. Ways of working the owner has endorsed

Conventions that are not yet in `CLAUDE.md`.

## 9. Starting prompt for the next phase

> Execute **phase <P+1>** of <workstream>. Run `/start-phase <workstream>`, or read this handoff in full, starting with §0. <The phase's scope, order and exit criteria in two or three sentences.> Build and test only through `scripts/test-in-container.sh`. If anything the owner has not decided blocks you, ask instead of choosing. When the phase is done, run `/handoff <workstream> <P+1>` and notify me.
