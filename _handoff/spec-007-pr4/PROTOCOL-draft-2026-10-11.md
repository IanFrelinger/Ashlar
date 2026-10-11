# Agent-bus protocol — the Ashlar agents

*DRAFT written 2026-10-11 in the cloud thread; proposed replacement for `_handoff/bus/PROTOCOL.md`. Not landed: the owner stopped the edit to write a handoff first. Land it as a small PR together with `bus-README-draft-2026-10-11.md`, the matching lines in `CLAUDE.md` (bus section: identities, `claim`, cards, "decisions log first"), `.claude/commands/agent-bus.md` (kinds line and title) and the comment of `scripts/agent-bus-latest.sh`. The GitHub label `agent-task` already exists (created 2026-10-11).*

## Overview

The agent-bus is a GitHub issue used as a durable coordination channel between the agents working on
Ashlar: Claude Code, Grok Bot, and the Cursor and Codex agents when the owner assigns them work. Every
coordination message is a **new comment**; older comments are never edited, so the issue is an append-only
log.

The live channel and its number are in [README.md](README.md).

## Comment format

Every bus message starts with a header block:

```
From: grok|claude|cursor|codex
To: grok|claude|cursor|codex|both|all
Kind: status|ask|handoff|block|done|claim
About: short-slug
```

followed by a blank line and a plain-markdown body.

### Header fields

- **From** — the posting agent: `grok`, `claude`, `cursor` or `codex`. An agent posts under its own name,
  also when it posts from the owner's account.
- **To** — one agent, `both` (the two agents in the thread) or `all`.
- **Kind**
  - `status` — progress update; no reply needed.
  - `ask` — needs something from the target: an answer, a decision, or an action.
  - `handoff` — a piece of work is ready for the other agent to act on.
  - `block` — the sender is blocked; the body states the exact ask.
  - `done` — a handoff, an ask or a claim is closed, with the evidence (merge SHA, run link).
  - `claim` — the sender takes a task card or a PR before touching it. Nobody else works on that slug until
    a `done`, or a `claim` whose body says `released`, follows on the same slug.
- **About** — one short slug, reused across a thread so replies are easy to match:
  `pr-697`, `drift-697`, `card-760`, `release-checklist`, `roles`.

Keep to one topic per comment. Reply in-thread by quoting the prior `About:` slug.

## Before starting any work

Every agent, every time, before it builds or merges anything:

1. fetch the newest handoff of the workstream (`scripts/handoff-fetch.sh --workstream <name>`) and read it;
   a handoff is superseded by any newer one on the storage branch, and nothing is built from a superseded
   handoff;
2. read the bus since its last post: open claims, asks addressed to it, blocks;
3. read the decisions log of the spec it works on (for SPEC-007,
   `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`);
4. post `Kind: claim` on the card or PR it is about to work on.

Two agents built the same three PRs in October 2026 because one of them skipped steps 1 and 4. The claim
is what prevents that.

## Decisions land in the log first

An owner decision exists when it is a dated row in the spec's decisions log on master, and not before.

- Whoever receives a decision from the owner lands it as a row in a small records PR **before** any code
  that depends on it. The row says what was decided, why, and its cost, and names where the owner said it
  (a PR, an issue comment, or the thread, by date).
- An agent never writes a row for a decision the owner did not make. A default an agent takes because the
  owner has not decided is an **integrator decision**: it is recorded in the PR body and, where it changes
  a record, as a Known limit, and it is listed in the next handoff as open. The owner can turn it into a
  row later.
- A handoff points at the rows; it does not restate them.

## Task cards

A unit of work that can be handed between agents is a GitHub issue labelled **`agent-task`**.

- The body states what done looks like (acceptance criteria), the records the work touches, and the
  decisions-log rows and handoff it relies on.
- The bus slug for a card is `card-<issue number>`.
- An agent takes a card by posting `Kind: claim` on the bus and assigning itself on the card (or adding a
  `claimed:<agent>` label when it cannot be assigned). An owner mention on the card (`@cursor`, `@codex`)
  is a request to claim, not a claim.
- A card is closed by the `done` that names the merge SHA, or returned with a `claim` whose body says
  `released` and why.
- Cards are the board; the bus is the log. Nothing is claimed in a card comment alone.

## Ownership

**Claude Code does all development, including merging** — code, pull requests, merges, releases, tags and
`release*.yml` dispatches. Claude merges its own PRs once every required check is green, and holds only
if Grok has posted a `block` on that PR first.

**When the owner assigns a card to the Cursor or Codex agent, that agent builds and merges it** and posts
the same `claim`, `handoff` and `done` for its own work. The evidence rules of `CLAUDE.md` (a check that was
never observed failing is not evidence; every behavioural change mutation-checked, with the verbatim
summary lines in the PR body) bind every developing agent alike.

**Grok Bot does admin, materials, and audits each merge for drift**, whoever merged it — stale docs or
records, counts and citations that no longer match the code, registries out of step with the workflows,
and claims a merge made false. Plus status summaries, issue hygiene and user-facing write-ups.

This supersedes the ownership rule in the issue's opening comment, which was written before the split was
settled.

## The loop

0. The developing agent posts `Kind: claim` on the card or PR (see above).
1. It posts `Kind: handoff`, `About: pr-<number>`: what changed, what was verified, what only CI can prove,
   and **which records and docs the change touches**, so the drift audit can be aimed.
2. It merges on green.
3. **Grok** audits the merge and posts findings as `Kind: ask`, `About: drift-<pr-number>` — one finding
   per bullet, with the file and line and what is now false.
4. The developing agent fixes them in a follow-up PR and closes each with `Kind: done` on the same slug.

## Examples

A claim:

```
From: cursor
To: all
Kind: claim
About: card-760

Taking card #760 (release notes for the AirGapped/SecureWorkstation switch).
Base master 7cb13289; relies on decisions-log rows 2026-10-05 Q1 and 2026-10-06 (4.5).
```

A handoff, a drift finding and a block: as in the current PROTOCOL.md.

## Reading the bus

`scripts/agent-bus-latest.sh [count]`, or the `/agent-bus` command in Claude Code, or
`gh issue view <number> --comments`.

To find what is outstanding, parse the headers:

- unanswered asks: `To: <you>` + `Kind: ask` with no later `done` on the same `About:` slug;
- open blocks: `Kind: block` with no later `done` on the same slug;
- open claims: `Kind: claim` with no later `done`, and no later `claim` saying `released`, on the same slug;
- cards nobody holds: open `agent-task` issues whose slug has no open claim.

## Issue state

The bus issue stays **open** while coordination is active. Close it only when the work it coordinates is
finished.
