---
description: Start a phase from its handoff. Fetch the handoff, read it, check it against the live repo, restate the phase, then execute it. Usage - /start-phase <workstream>
---

# /start-phase: begin a phase from the previous phase's handoff

The owner's rule is one thread per phase (`_handoff/phases/README.md`). This command starts one.

The argument is `$ARGUMENTS`: `<workstream>`, for example `spec-007-pr4`. If the owner attached a handoff file, it takes precedence over the storage branch. If the two differ, say so.

## Steps

1. **Get the handoff.** Use the attached file, or:

   ```bash
   bash scripts/handoff-fetch.sh --workstream <workstream> --out <scratchpad>/handoff.md
   bash scripts/handoff-fetch.sh --workstream <workstream> --list   # what else is stored
   ```

   Save every attachment the handoff names into the scratchpad as well, with `git show origin/claude/<workstream>-workspace:_handoff/<workstream>/<file>`.

2. **Read it in full**, starting with §0, then everything its "Read these first" section names. `CLAUDE.md` is already loaded.

3. **Check it against the live repo** before acting:
   - `git fetch origin`. Master's SHA, and the merges the handoff lists.
   - Each branch the handoff lists exists, at the SHA it gives or later.
   - Each PR it lists is in the state it gives.
   - The agent-bus (#695) has nothing new that changes the plan.
   - The environment works: docker answers and the devtest image exists. If not, run `CLAUDE_CODE_REMOTE=true bash .claude/hooks/session-start.sh`.

   Report every difference in one short list. If a difference changes the plan, resolve it, or ask the owner, before starting.

4. **Restate the phase in a few lines:**
   - its scope;
   - its order;
   - its exit criteria;
   - the decisions it relies on;
   - what is out of scope.

   If it needs an owner decision that is not recorded, ask now (AskUserQuestion, recommended option first). Do not choose for the owner.

5. **Execute the phase**, following `CLAUDE.md` and the handoff's routine steps.

6. **End with `/handoff <workstream> <phase>`.** Do not start the following phase in this thread.
