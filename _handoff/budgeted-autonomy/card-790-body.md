Task card (`agent-task`, see `_handoff/bus/PROTOCOL.md`). Bus slug: `card-<this number>`. Workstream: **budgeted-autonomy**. Filed after the PR 5 live run of 2026-10-13. It is not built in phase F (owner decision, decisions log 2026-10-13).

**What the live run showed.** The Claude adapter passes `--strict-mcp-config` and `--no-chrome`, so a worker gets none of the operator's MCP servers. It still loads the operator's user-level configuration.

All three Claude runs' `stdout.json` (runs `run-effc72e96849`, `run-5c155cbe6f82`, `run-a062813c1dae`) show the worker loading:
- the `SessionStart` hook (`system/hook_started` and `hook_response` before `system/init`);
- 19 skills, 9 agents and 58 slash commands;
- auto-memory;
- a `session_context` attachment carrying the account email.

The workers also inherited the Codex runtime's `pwsh` on PATH. An unattended worker should not run the operator's hooks, nor be steered by the operator's skills, agents or memory.

**Done when:**
- `adapters/claude.py` launches a worker isolated from the operator's user-level configuration: no user hooks, skills, agents, slash commands or auto-memory. Use only flags or settings that a probe of the installed CLI shows to work, and declare only what the probe observed (the way the phase F Codex probe used `codex debug prompt-input`).
- Before declaring the isolation, the probe is saved and named in the module docstring.
- A test asserts the isolating argv on a fresh launch and on a resume.
- Each behaviour is mutation-checked with `scripts/mutation-check.sh`.
- The README's "Claude adapter" says what is isolated and what is not.
- Nothing outside `tools/autonomy-controller/` changes.

**Why:** the same reason Codex runs now pass `-c features.plugins=false`. A worker under `acceptEdits`, with prompts off, runs unattended.

**Decisions this relies on:** the decisions log row of 2026-10-13, "Claude workers".
