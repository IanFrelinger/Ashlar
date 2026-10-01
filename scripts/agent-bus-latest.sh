#!/usr/bin/env bash
# Print the last N comments from the agent-bus issue (Grok Bot <-> Claude Code).
#
#   scripts/agent-bus-latest.sh [count]        # default 10
#   ASHLAR_BUS_ISSUE=701 scripts/agent-bus-latest.sh 5
#
# The channel and the message format are documented in _handoff/bus/README.md and
# _handoff/bus/PROTOCOL.md. Requires an authenticated `gh`.
set -euo pipefail

ISSUE="${ASHLAR_BUS_ISSUE:-695}"
COUNT="${1:-10}"

if ! [[ "${COUNT}" =~ ^[1-9][0-9]*$ ]]; then
  echo "usage: $0 [count]   (count must be a positive integer, got '${COUNT}')" >&2
  exit 2
fi

gh issue view "${ISSUE}" --json comments \
  --jq ".comments[-${COUNT}:] | .[] | \"[\(.author.login)] \(.createdAt)\n\(.body)\n---\""
