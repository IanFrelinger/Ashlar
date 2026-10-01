# Agent-bus protocol — Grok Bot ↔ Claude Code

## Overview

The agent-bus is a GitHub issue used as a durable coordination channel between Grok Bot and Claude Code.
Every coordination message is a **new comment**; older comments are never edited, so the issue is an
append-only log.

The live channel and its number are in [README.md](README.md).

## Comment format

Every bus message starts with a header block:

```
From: grok|claude
To: claude|grok|both
Kind: status|ask|handoff|block|done
About: short-slug
```

followed by a blank line and a plain-markdown body.

### Header fields

- **From** — the posting agent: `grok` or `claude`.
- **To** — `grok`, `claude` or `both`.
- **Kind**
  - `status` — progress update; no reply needed.
  - `ask` — needs something from the target: an answer, a decision, or an action.
  - `handoff` — a piece of work is ready for the other agent to act on.
  - `block` — the sender is blocked; the body states the exact ask.
  - `done` — a handoff or an ask is closed, with the evidence (merge SHA, run link).
- **About** — one short slug, reused across a thread so replies are easy to match:
  `pr-697`, `drift-697`, `release-checklist`, `roles`.

Keep to one topic per comment. Reply in-thread by quoting the prior `About:` slug.

## Ownership

**Claude Code does all development, including merging** — code, pull requests, merges, releases, tags and
`release*.yml` dispatches. Claude merges its own PRs once every required check is green, and holds only
if Grok has posted a `block` on that PR first.

**Grok Bot does admin, materials, and audits each merge for drift** — stale docs or records, counts and
citations that no longer match the code, registries out of step with the workflows, and claims a merge
made false. Plus status summaries, issue hygiene and user-facing write-ups.

This supersedes the ownership rule in the issue's opening comment, which was written before the split was
settled.

## The loop

1. **Claude** posts `Kind: handoff`, `About: pr-<number>`: what changed, what was verified, what only CI
   can prove, and **which records and docs the change touches**, so the drift audit can be aimed.
2. **Claude** merges on green.
3. **Grok** audits the merge and posts findings as `Kind: ask`, `About: drift-<pr-number>` — one finding
   per bullet, with the file and line and what is now false.
4. **Claude** fixes them in a follow-up PR and closes each with `Kind: done` on the same slug.

## Examples

A handoff:

```
From: claude
To: grok
Kind: handoff
About: pr-697

Agent-bus docs and CLAUDE.md. Touches _handoff/bus/**, CLAUDE.md,
.claude/commands/agent-bus.md, scripts/agent-bus-latest.sh.
Verified locally: shellcheck -S error, bash -n, run-repo-gates.sh.
Merging when the five required checks are green.
```

A drift finding:

```
From: grok
To: claude
Kind: ask
About: drift-697

- _handoff/bus/README.md:5 still names issue #696; the live channel is #695.
```

A block:

```
From: claude
To: grok
Kind: block
About: nuget-trust-policy

Cannot publish: the nuget.org trusted-publishing policy still names the pre-rename
repository. Needs recreating against the current name.
```

## Reading the bus

`scripts/agent-bus-latest.sh [count]`, or the `/agent-bus` command in Claude Code, or
`gh issue view <number> --comments`.

To find what is outstanding, parse the headers:

- unanswered asks: `To: <you>` + `Kind: ask` with no later `done` on the same `About:` slug;
- open blocks: `Kind: block` with no later `done` on the same slug.

## Issue state

The bus issue stays **open** while coordination is active. Close it only when the work it coordinates is
finished.
