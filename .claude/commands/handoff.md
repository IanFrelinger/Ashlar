---
description: End the current phase. Push in-flight work, validate and audit the whole project, write the handoff from the template with the project status in it, publish it to the workstream's storage branch, send it to the owner and notify them. Usage - /handoff <workstream> <phase>
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

4. **Validate and audit the whole project.** This runs every phase, before the handoff is written, and covers the whole project, not only this workstream. Start the audit early, because it takes about 25 minutes. Gather the facts of step 3 while it runs.
   1. **Validation** (deterministic, about two minutes; needs git, bash and an authenticated `gh`):

      ```bash
      python scripts/phase-end/validate.py --workstream <workstream> --phase <phase> \
        --json <scratchpad>/validation-<phase>.json --markdown <scratchpad>/validation-<phase>.md
      ```

      Exit 1 means at least one check is `fail` or `error`. That is a finding for the handoff, not a reason to stop. Every `fail`, `error` and `warn` is named in the handoff. On Windows, if `bash` resolves to WSL, set `ASHLAR_BASH` to Git Bash.
   2. **The previous status**, absent on a workstream's first audit:

      ```bash
      git show origin/claude/<workstream>-workspace:_handoff/<workstream>/project-status.json \
        > <scratchpad>/previous-status.json
      ```

   3. **The audit.** Run the saved workflow `project-audit` (Workflow tool, `name: "project-audit"`, in the background). Pass these `args` as JSON values, not as strings:
      - `workstream`, `phase`, and `date` (today, `YYYY-MM-DD`);
      - `masterSha` (master's full SHA);
      - `validation`: the parsed contents of `validation-<phase>.json`;
      - `previous`: the parsed contents of `previous-status.json`, or `null`.

      The workflow is read-only and uses nine agents: seven area readers, a critic and a synthesis. Invoking `/handoff` is the opt-in for running it.
   4. **Save its return value:**
      - `status` goes to `<scratchpad>/project-status.json`, and a copy to `<scratchpad>/project-status-<phase>.json`;
      - the whole return goes to `<scratchpad>/project-audit-<phase>.json`.
   5. **If the audit cannot run** (no Workflow tool in this session, the run is refused, or it fails), write the validation results alone into the status section. Say that the audit did not run, and why. Never write the status from memory, and never from an earlier phase's audit.

5. **Write the handoff from `_handoff/phases/HANDOFF-TEMPLATE.md`.**
   - Fill every section. Carry forward everything still true from the previous handoff, and add what this phase learned.
   - **§1 "Project status at the end of phase <phase>":** paste `status.markdown` verbatim. Then add one line naming the attachments (`validation-<phase>.md`, `project-status-<phase>.json`, `project-audit-<phase>.json`).
   - Copy `status.workstream_findings` into §7, under queued work.
   - Copy any of `status.owner_decisions` that this workstream depends on into §3, under open questions.
   - Spell out the next phase: its scope, order and exit criteria.
   - Write the §9 starting prompt for it.
   - Write the file to the scratchpad, for example `<scratchpad>/handoff.md`.

6. **Publish it.**

   ```bash
   bash scripts/handoff-publish.sh --workstream <workstream> --phase <phase> --file <scratchpad>/handoff.md \
     --attach <scratchpad>/validation-<phase>.json --attach <scratchpad>/validation-<phase>.md \
     --attach <scratchpad>/project-status.json --attach <scratchpad>/project-status-<phase>.json \
     --attach <scratchpad>/project-audit-<phase>.json [--attach <design or notes file>]...
   ```

   `project-status.json` is overwritten every phase: it is what the next phase's audit compares against. Earlier versions stay in the storage branch's history and in `project-status-<phase>.json`. Attach any other working document the next phase needs that exists only in the scratchpad: design notes, lane rules, PR-body drafts.

7. **Deliver it.**
   - Send the handoff file to the owner (SendUserFile, attach).
   - Send a push notification, under 200 characters, leading with the outcome and one clause of project status. For example: "Phase B done: 4.4/4.1/4.6/4.2 merged; project: 0.3.0 partway, 2 gates red. Handoff on claude/spec-007-pr4-workspace. Next: phase C."

8. **Stop.** Do not start the next phase in this thread. Cancel any check-ins you scheduled for this phase's work, unless something is still in flight, and say so in the handoff.

## Notes

- The handoff is a working note on the storage branch. Owner decisions belong in the durable records on master (for example a spec's decisions log), through normal PRs, before the phase ends.
- Never paste secrets or tokens into a handoff. Name where a credential lives instead.
- The audit reports; it does not fix. A finding that belongs to this workstream goes into §7. A finding about another workstream goes to that workstream's owner through the agent-bus, when it is worth an `ask`.
