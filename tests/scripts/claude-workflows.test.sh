#!/usr/bin/env bash
# The saved Workflow scripts in .claude/workflows/ parse and follow the Workflow tool's rules
# (scripts/phase-end/check_workflows.js). `/handoff` runs .claude/workflows/project-audit.js at the end of every
# phase; a script that does not parse would otherwise fail only then, in the middle of ending a phase.
# Discovered and run bare by scripts/ci/run-repo-gates.sh in the required shell-lint check.
#
# The checker is proven both ways: every committed workflow must pass, and four broken fixtures (an unmatched
# phase and Date.now, a meta naming a variable, a meta calling a builtin, a body that does not parse) must each
# be refused for their own reason. A checker that passed everything would fail here.
#
# Needs node (preinstalled on the hosted ubuntu runner shell-lint uses). No node is a failure, not a skip.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHECK="${ROOT}/scripts/phase-end/check_workflows.js"
fail=0

if ! node -e 'process.exit(0)' >/dev/null 2>&1; then
  echo "claude-workflows.test: ERROR: node is not available; the workflow scripts cannot be checked." >&2
  exit 1
fi

count="$(ls -1 "${ROOT}"/.claude/workflows/*.js 2>/dev/null | wc -l | tr -d ' ')"
if [[ "${count}" -lt 2 ]]; then
  echo "claude-workflows.test: ERROR: expected at least 2 workflow scripts in .claude/workflows, found ${count}." >&2
  exit 1
fi
if ! out="$(node "${CHECK}")"; then
  echo "${out}"
  echo "claude-workflows.test: FAIL: a committed workflow script is broken (above)." >&2
  fail=1
else
  echo "${out}"
fi
grep -q "ok   .*project-audit.js" <<<"${out}" || { echo "claude-workflows.test: FAIL: project-audit.js was not checked." >&2; fail=1; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

cat >"${TMP}/unmatched.js" <<'JS'
export const meta = { name: 'x', description: 'a } inside a string', phases: [{ title: 'A' }] }
phase('B')
const t = Date.now()
return 1
JS
cat >"${TMP}/impure.js" <<'JS'
const v = 1
export const meta = { name: v, description: 'y' }
return 1
JS
cat >"${TMP}/builtin.js" <<'JS'
export const meta = { name: 'x', description: String(3) }
return 1
JS
cat >"${TMP}/noparse.js" <<'JS'
export const meta = {
  name: 'x',
  description: 'y',
}
const r = await agent('hi')
return 1 +
JS

expect_refused() {   # <fixture> <text the refusal must contain>
  local f="$1" want="$2" got
  if got="$(node "${CHECK}" "${TMP}/${f}")"; then
    echo "claude-workflows.test: FAIL: the checker accepted the broken fixture ${f}." >&2
    fail=1
  elif ! grep -qF -- "${want}" <<<"${got}"; then
    echo "${got}"
    echo "claude-workflows.test: FAIL: ${f} was refused, but not for: ${want}" >&2
    fail=1
  else
    echo "refused as expected: ${f} (${want})"
  fi
}
expect_refused unmatched.js "phase('B') has no meta.phases entry"
expect_refused unmatched.js "meta.phases entry 'A' is never started"
expect_refused unmatched.js "uses Date.now()"
expect_refused impure.js "meta is not a pure literal: the identifier v is not a literal"
# A builtin call evaluates in a fresh vm context, so the purity check must read the source, not run it.
expect_refused builtin.js "meta is not a pure literal"
expect_refused noparse.js "the body does not parse"

if [[ "${fail}" -ne 0 ]]; then
  echo "claude-workflows.test: FAIL" >&2
  exit 1
fi
echo "claude-workflows.test: ${count} workflow script(s) pass; 6 refusals observed"
