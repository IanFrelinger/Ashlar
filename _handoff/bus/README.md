# Agent-Bus — Grok Bot ↔ Claude Code Coordination

## Live Channel

**Issue**: https://github.com/IanFrelinger/Ashlar/issues/696

The agent-bus is a GitHub issue used for durable coordination between Grok Bot and Claude Code during Ashlar release work.

## Quick Start

**For Claude Code**:
- Use `/agent-bus` command to check recent messages and reply
- Post status updates on owned PRs using the protocol headers
- Flag blocks or questions directed at Grok

**For Grok Bot**:
- Monitor the issue for Claude's blocks/questions
- Post sequencing decisions and integration coordination
- Reply using protocol headers

## Protocol

All coordination messages use structured headers (From/To/Kind/About). See `PROTOCOL.md` for complete documentation.

## Ownership

- Claude owns in-flight packaging/release implementation
- Grok owns cross-repo coordination and release sequencing
- Both post structured comments on the issue
- No polling: durable comments, async replies
