# /agent-bus — Check and Reply to Agent-Bus

## Purpose

Read recent messages from the agent-bus coordination issue (Grok Bot ↔ Claude Code) and draft/post a reply.

## Instructions

When this command is invoked:

1. **Fetch recent comments** from the agent-bus issue using `gh`:
   ```bash
   gh issue view 695 --json comments --jq '.comments[-10:] | .[] | "[\(.author.login)] \(.createdAt)\n\(.body)\n"'
   ```
   
2. **Parse and summarize**:
   - Look for messages with `To: claude` in the header
   - Identify unanswered questions (`Kind: question`)
   - Identify active blocks (`Kind: block`)
   - Note any `Kind: request` items directed at Claude
   
3. **Present summary** to the user:
   - Show the last 5-10 comments (or use `scripts/agent-bus-latest.sh 10` if available)
   - Highlight items needing Claude's attention
   - List unanswered questions and unresolved blocks
   
4. **Draft reply** (if needed):
   - Ask user what to reply or draft based on context
   - Format reply with protocol headers:
     ```
     From: claude
     To: <grok|all>
     Kind: <status|info|request>
     About: <subject>
     
     <body>
     ```
   
5. **Post reply** (if user confirms):
   ```bash
   gh issue comment 695 --body "<reply-text>"
   ```

## Protocol Reference

See `_handoff/bus/PROTOCOL.md` for complete protocol documentation.

## Quick Checks

- **Unanswered to Claude**: Comments with `To: claude` that have no subsequent reply from Claude
- **Active blocks**: `Kind: block` messages with no resolution follow-up
- **Recent status**: Latest `Kind: status` from Grok about sequencing or gates

## Example Usage

User types: `/agent-bus`

Claude responds with:
- Summary of last 10 comments
- "You have 2 unanswered questions from Grok"
- Draft reply if action is clear, or ask user what to say
