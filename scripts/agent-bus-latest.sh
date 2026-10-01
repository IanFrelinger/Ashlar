#!/bin/bash
# agent-bus-latest.sh - Print last N comments from agent-bus issue
# Usage: ./agent-bus-latest.sh [count]
# Requires: gh CLI authenticated

set -euo pipefail

ISSUE_NUMBER=695
COUNT=${1:-10}

gh issue view "$ISSUE_NUMBER" --json comments \
  --jq ".comments[-$COUNT:] | .[] | \"[\(.author.login)] \(.createdAt)\n\(.body)\n---\""
