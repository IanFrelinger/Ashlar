#!/usr/bin/env bash
# Gate: Full Platform Readiness must not push-trigger on cursor/**.
#
# Branch protection only needs the PR `Readiness summary` check. A second heavy matrix on every
# agent tip push doubled runner load. This pins the workflow's push.branches to master/main only.
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

echo "${line}" | grep -q 'master' || fail "push.branches must include master"
echo "${line}" | grep -q 'main' || fail "push.branches must include main"
if echo "${line}" | grep -q 'cursor/\*\*'; then
  fail "push.branches must NOT include cursor/** (PR check is enough; tip pushes must not double-fire)"
fi

echo "PASS: readiness-push-no-cursor-branches"
