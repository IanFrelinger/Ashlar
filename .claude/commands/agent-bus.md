# /agent-bus — read the agent-bus and reply

## Purpose

Read recent messages on the agent-bus coordination issue (Claude Code, Grok Bot, and the Cursor and
Codex agents), say what is outstanding, and post a reply.

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
   - **drift findings** — `About: drift-<pr>`, which are fixes for you to make in a follow-up PR;
   - **open claims** — `Kind: claim` with no later `done`, and no later `claim` saying `released`, on the
     same slug: someone else is working on that card or PR, so do not start it;
   - **cards nobody holds** — open issues labelled `agent-task` whose `card-<n>` slug has no open claim.

3. **Report** to the user: the last few messages in one line each, then the outstanding items. Say
   plainly if there is nothing outstanding.

4. **Draft a reply** when there is something to answer. Use the protocol header:

   ```
   From: claude
   To: grok|cursor|codex|both|all
   Kind: status|ask|handoff|block|done|claim
   About: <slug — reuse the slug you are replying to>
   ```

   Reuse the `About:` slug of the message you are answering so the thread stays matchable. Before
   building a card or a PR, post `Kind: claim` on its slug (`card-<n>` or `pr-<n>`) first.

5. **Post it.** Posting is an outward-facing action: show the user the draft and get a yes first, unless
   they have already told you to post without asking.

   ```bash
   gh issue comment 695 --body-file <path>
   ```

   Write the body to a file rather than passing it inline — the headers and markdown survive intact.

## Notes

- Never edit an older comment; always add a new one.
- One topic per comment.
- Claude does all development, including merges; a Cursor or Codex agent builds and merges the cards the
  owner assigns it, under the same rules. Grok audits every merge for drift and handles admin. Do not ask
  Grok to merge, and do not wait for Grok before committing your own work.
- An owner decision exists when it is a dated row in the spec's decisions log on master; land it in a
  records PR before the code that depends on it, and never write a row for a decision the owner did not make.
