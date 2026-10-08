#!/usr/bin/env bash
# Gate: Full Platform Readiness push.branches is exactly [master, main].
#
# Branch protection only needs the PR `Readiness summary` check. A second heavy matrix on every
# agent tip push doubled runner load. The list must be master/main only — not merely
# "cursor/** absent" (a claude/** addition would otherwise pass).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WF="${ROOT}/.github/workflows/full-platform-readiness-gate.yml"

fail() { echo "FAIL: $*" >&2; exit 1; }
[[ -f "${WF}" ]] || fail "missing ${WF}"

# Extract the push: branches: [...] line (first occurrence under on.push).
line="$(awk '
  $1=="push:" { in_push=1; next }
  in_push && $1=="branches:" { print; exit }
' "${WF}")"

[[ -n "${line}" ]] || fail "could not find on.push.branches in ${WF}"
echo "found: ${line}"

# Normalise: drop "branches:" and whitespace, require exact list.
list="$(echo "${line}" | sed -E 's/^[[:space:]]*branches:[[:space:]]*//; s/[[:space:]]+$//')"
expected='[master, main]'
[[ "${list}" == "${expected}" ]] || fail "push.branches must be exactly ${expected}, got: ${list}"

if echo "${line}" | grep -qE 'cursor/\*\*|claude/\*\*'; then
  fail "push.branches must not include agent-branch globs"
fi

echo "PASS: readiness-push-no-cursor-branches"
