---
description: End the current phase. Push in-flight work, write the handoff from the template, publish it to the workstream's storage branch, send it to the owner and notify them. Usage - /handoff <workstream> <phase>
---

# /handoff: end a phase and write the next thread's starting point

The owner's rule is one thread per phase (`_handoff/phases/README.md`). This command ends the current phase.

Arguments are in `$ARGUMENTS`: `<workstream> <phase>`, for example `spec-007-pr4 B`, where `<phase>` is the phase that is ending. If either is missing:
- take the workstream from the current handoff's title;
- take the phase from its phase map: the phase marked as current.

Say which values you took.

## Steps

1. **Check the exit criteria.** Read the current phase's "ends when" in the phase map of the handoff this thread started from. Check each criterion against live state.
   - If one is not met and you cannot meet it now, do not pretend it is met. The handoff must name the blocker exactly: what is failing, the evidence, and what is needed from whom.
   - If it is blocked on an owner decision, ask the owner first (AskUserQuestion), when they are present.

2. **Push every in-flight branch.**
   - Clones in the scratchpad and local branches are lost with the container. Push each branch this phase created or changed to `origin`, plain branches with no PRs, with `git push -u origin <branch>`.
   - If a clone's `origin` is the local checkout, fetch the branch into the checkout first.
   - Record each branch with its SHA.

3. **Gather live facts.** Do not use memory for these:
   - `git fetch origin`; master's SHA; the merges this phase (`git log --oneline <phase-start>..origin/master`).
   - The PRs you opened that are still open, and their check state (GitHub MCP tools).
   - The agent-bus: recent comments on issue #695, and any open `ask`, `block` or `drift-`.
   - The readiness verdict for master's head.
   - For every work item in the phase map: branch, SHA, PR, state, and any check or review findings not yet fixed.

4. **Write the handoff from `_handoff/phases/HANDOFF-TEMPLATE.md`.**
   - Fill every section. Carry forward everything still true from the previous handoff, and add what this phase learned.
   - Spell out the next phase: its scope, order and exit criteria.
   - Write the §9 starting prompt for it.
   - Write the file to the scratchpad, for example `<scratchpad>/handoff.md`.

5. **Publish it.**

   ```bash
   bash scripts/handoff-publish.sh --workstream <workstream> --phase <phase> --file <scratchpad>/handoff.md \
     [--attach <design or notes file>]...
   ```

   Attach any working document the next phase needs that exists only in the scratchpad: design notes, lane rules, PR-body drafts.

6. **Deliver it.**
   - Send the handoff file to the owner (SendUserFile, attach).
   - Send a push notification, under 200 characters, leading with the outcome. For example: "Phase B done: 4.4/4.1/4.6/4.2 merged; handoff ready on claude/spec-007-pr4-workspace. Next: phase C."

7. **Stop.** Do not start the next phase in this thread. Cancel any check-ins you scheduled for this phase's work, unless something is still in flight, and say so in the handoff.

## Notes

- The handoff is a working note on the storage branch. Owner decisions belong in the durable records on master (for example a spec's decisions log), through normal PRs, before the phase ends.
- Never paste secrets or tokens into a handoff. Name where a credential lives instead.
