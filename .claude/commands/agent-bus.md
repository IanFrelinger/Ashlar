# /agent-bus — read the agent-bus and reply

## Purpose

Read recent messages on the agent-bus coordination issue (Grok Bot ↔ Claude Code), say what is
outstanding, and post a reply.

The channel is issue **695**; see `_handoff/bus/README.md`. The message format is in
`_handoff/bus/PROTOCOL.md`.

## Steps

1. **Fetch** the recent comments:

   ```bash
   bash scripts/agent-bus-latest.sh 10
   ```

2. **Work out what is outstanding.** Parse the header block of each comment:
   - **asks for you** — `To: claude` (or `both`) with `Kind: ask`, and no later `Kind: done` from you on
     the same `About:` slug;
   - **open blocks** — `Kind: block` with no later `done` on the same slug;
   - **handoffs** — `Kind: handoff` addressed to you that you have not answered;
   - **drift findings** — `About: drift-<pr>`, which are fixes for you to make in a follow-up PR.

3. **Report** to the user: the last few messages in one line each, then the outstanding items. Say
   plainly if there is nothing outstanding.

4. **Draft a reply** when there is something to answer. Use the protocol header:

   ```
   From: claude
   To: grok
   Kind: status|ask|handoff|block|done
   About: <slug — reuse the slug you are replying to>
   ```

   Reuse the `About:` slug of the message you are answering so the thread stays matchable.

5. **Post it.** Posting is an outward-facing action: show the user the draft and get a yes first, unless
   they have already told you to post without asking.

   ```bash
   gh issue comment 695 --body-file <path>
   ```

   Write the body to a file rather than passing it inline — the headers and markdown survive intact.

## Notes

- Never edit an older comment; always add a new one.
- One topic per comment.
- Claude does all development, including merges. Grok audits merges for drift and handles admin. Do not
  ask Grok to merge, and do not wait for Grok before committing your own work.
