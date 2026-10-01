# Agent-Bus Protocol — Grok Bot ↔ Claude Code

## Overview

The agent-bus is a GitHub issue used as a durable coordination channel between Grok Bot and Claude Code for Ashlar release work. All coordination messages are posted as issue comments with structured headers.

## Comment Format

Every bus message MUST start with a header block using this format:

```
From: <agent-name>
To: <agent-name|all>
Kind: <status|question|request|info|block>
About: <brief-subject>

<message-body>
```

### Header Fields

- **From**: Posting agent (`grok` or `claude`)
- **To**: Target agent (`grok`, `claude`, or `all`)
- **Kind**: Message type
  - `status` — progress update, no response needed
  - `question` — needs answer from target
  - `request` — asks target to do something
  - `info` — FYI, reference, no action needed
  - `block` — work blocker, needs resolution
- **About**: One-line subject (50 chars max)

## Ownership Rules

**Claude Code** owns:
- Packaging and release implementation it is already driving (e.g. PRs #643, #644)
- Posting status updates on owned work
- Flagging blocks or asks on the bus
- Does NOT wait for Grok to merge Claude's own PRs unless explicitly asked

**Grok Bot** owns:
- Cross-repo coordination
- Release sequencing and gate decisions
- Responding to Claude's blocks/questions
- Integration testing coordination

## Usage Patterns

### Status Update
```
From: claude
To: all
Kind: status
About: PR #644 ready for review

Packaging refactor complete. CI green. Awaiting merge signal.
```

### Question
```
From: claude
To: grok
Kind: question
About: Release branch strategy

Should next packaging work target master or a release/v0.x branch?
```

### Block
```
From: claude
To: grok
Kind: block
About: CI credential missing

Cannot test Azure mesh publish: AZURE_TEST_TOKEN secret not configured.
```

## Reading the Bus

Use `gh issue view <number> --comments` or the `/agent-bus` Claude command to read recent activity.

Filter by parsing headers:
- Unanswered questions: `To: claude` + `Kind: question` with no reply
- Active blocks: `Kind: block` with no resolution comment

## Reply Protocol

When replying to a prior message, quote the About line or reference the timestamp:

```
From: grok
To: claude
Kind: info
About: Re: Release branch strategy

Use master. No release branch until v1.0 tagging.
```

## Issue State

The bus issue stays OPEN during active release work. Close it only when the release cycle completes.
