#!/usr/bin/env bash
# The saved Workflow scripts in .claude/workflows/ parse and follow the Workflow tool's rules
# (scripts/phase-end/check_workflows.js). `/handoff` runs .claude/workflows/project-audit.js at the end of every
# phase; a script that does not parse would otherwise fail only then, in the middle of ending a phase.
# Discovered and run bare by scripts/ci/run-repo-gates.sh in the required shell-lint check.
#
# The checker is proven both ways: every committed workflow must pass, and five broken fixtures (an unmatched
# phase and Date.now, a meta naming a variable, a meta calling a builtin, a malformed meta literal, a body that
# does not parse) must each be refused for their own reason. A checker that passed everything would fail here.
# Every saved workflow is also LF, pinned by .gitattributes: the Workflow tool refuses a script holding a CR.
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
# The Workflow tool refuses a script that holds a control character, and a CR is one. A CRLF checkout of a saved
# workflow (core.autocrlf=true on Windows) therefore cannot be run by name, so .gitattributes pins them to LF, and
# no checked-out copy may hold a CR. Linux CI checks the attribute; a Windows checkout made before the attribute
# landed is caught by the byte check. The byte check compares the file with itself minus every CR, because Git for
# Windows' grep drops a CR before a line end and so cannot see one.
for f in "${ROOT}"/.claude/workflows/*.js; do
  rel=".claude/workflows/$(basename "${f}")"
  eol="$(git -C "${ROOT}" check-attr eol -- "${rel}" | sed 's/^.*: eol: //')"
  if [[ "${eol}" != "lf" ]]; then
    echo "claude-workflows.test: FAIL: ${rel} is not pinned to LF in .gitattributes (eol: ${eol})." >&2
    fail=1
  fi
  if ! tr -d '\r' <"${f}" | cmp -s - "${f}"; then
    echo "claude-workflows.test: FAIL: ${rel} holds a CR in this checkout, so it cannot be run by name; check it out again: rm '${rel}' && git checkout -- '${rel}'" >&2
    fail=1
  fi
done

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
cat >"${TMP}/malformed.js" <<'JS'
export const meta = { name: 'x' description: 'y' }
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
# Made only of allowed tokens, so the source scan passes it; evaluating it is what refuses it.
expect_refused malformed.js "meta does not evaluate as a literal"
expect_refused noparse.js "the body does not parse"

if [[ "${fail}" -ne 0 ]]; then
  echo "claude-workflows.test: FAIL" >&2
  exit 1
fi
echo "claude-workflows.test: ${count} workflow script(s) pass and are LF; 7 refusals observed"
