# Claude Code Instructions — Ashlar

This file is read automatically by Claude Code. It provides project-specific guidance.

## Product Continuity

When resuming work or understanding product context:
- Read `CONTINUITY.md` for full product history and what is done
- Read `_handoff/readiness/README.md` for release-readiness convergence work

## Multi-Agent Release Coordination

For release work involving Grok Bot, use the **agent-bus** coordination channel:

- **Channel**: GitHub issue (see `_handoff/bus/README.md` for link)
- **Protocol**: Structured comment headers per `_handoff/bus/PROTOCOL.md`
- **Command**: `/agent-bus` to check and reply to messages

### Ownership

**Claude Code owns**:
- Packaging and release PRs already in flight (e.g. #643, #644)
- Posting status updates, blocks, and questions on the bus
- Does NOT wait for Grok to merge Claude's own work unless explicitly asked

**Grok Bot owns**:
- Cross-repo coordination
- Release sequencing and gate decisions
- Integration testing coordination

### When to Use the Bus

- Post `Kind: status` updates when owned PRs reach milestones (ready for review, CI green)
- Post `Kind: block` if work is blocked by missing config, credentials, or decisions
- Post `Kind: question` when you need Grok's input on release strategy or sequencing
- Check the bus at task start and before finalizing owned work

### When NOT to Use the Bus

- Do not wait for approval to push/commit your own work
- Do not ask Grok to merge PRs you created unless there is a dependency or gate issue
- Do not post routine implementation details; keep messages high-level

Use `/agent-bus` to read recent activity and draft replies.
