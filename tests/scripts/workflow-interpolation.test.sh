#!/usr/bin/env bash
# Tests for scripts/ci/verify-workflow-run-interpolation.py.
#
# WHY A TEST AND NOT JUST THE CHECK. A scanner that stops matching is indistinguishable from a
# repository with nothing to find: both print "clean" and exit 0. So the cases below feed it
# workflows it MUST reject, and only then is a clean result on the real tree worth anything.
#
# Run:  bash tests/scripts/workflow-interpolation.test.sh
# Pure bash + python3: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHECK="${ROOT}/scripts/ci/verify-workflow-run-interpolation.py"

# Resolve the interpreter ONCE and fail loudly if there is none. Without this, a missing python3
# makes every "must be refused" case below exit non-zero for the wrong reason and report ok - the
# check would look fully tested while never having run. Measured on a box with no python3: five
# assertions passed against the Microsoft Store stub.
# PROBE it, do not merely locate it. On Windows, `command -v python3` finds the App Execution
# Alias stub, which exists, is executable, and exits 49 telling you to visit the Microsoft Store.
# That is indistinguishable from "the check refused" to any assertion that only tests for a
# non-zero exit - measured here: five "must be refused" cases reported ok against the stub.
PY_BIN=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'print(1)' >/dev/null 2>&1; then
    PY_BIN="${candidate}"
    break
  fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "FAIL - no working python3 on PATH; this file cannot test anything and will not pretend to."
  exit 1
fi

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=10

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Build a throwaway repo containing exactly one workflow, and run the check against it.
# Echoes "<exit>|<output>".
scan_one() {
  local body="$1"
  local tmp; tmp="$(mktemp -d)"
  mkdir -p "${tmp}/.github/workflows"
  printf '%s\n' "${body}" > "${tmp}/.github/workflows/sample.yml"
  local out code
  out="$("${PY_BIN}" "${CHECK}" "${tmp}" 2>&1)"; code=$?
  rm -rf "${tmp}"
  printf '%s|%s' "${code}" "${out}"
}

echo "== it rejects the shape that let a branch name run as shell =="
r="$(scan_one 'jobs:
  a:
    steps:
      - run: |
          gh workflow run release.yml --ref "${{ github.event.pull_request.head.ref }}"')"
if [[ "${r%%|*}" != "0" ]] && grep -q 'head.ref' <<<"${r#*|}"; then
  ok "block scalar: pull_request.head.ref inside run: is refused"
else
  bad "block scalar: pull_request.head.ref inside run: is refused" "exit ${r%%|*}: ${r#*|}"
fi

r="$(scan_one 'jobs:
  a:
    steps:
      - run: echo "${{ github.head_ref }}"')"
[[ "${r%%|*}" != "0" ]] && ok "single-line run: is scanned too, not only block scalars" \
  || bad "single-line run: is scanned" "exit 0"

for ctx in "github.event.issue.title" "github.event.comment.body" "github.event.pull_request.title" "github.event.head_commit.message"; do
  r="$(scan_one "jobs:
  a:
    steps:
      - run: |
          echo \"\${{ ${ctx} }}\"")"
  if [[ "${r%%|*}" != "0" ]] && grep -q 'workflow-interpolation: ERROR' <<<"${r#*|}"; then
    ok "refuses ${ctx}"
  else
    bad "refuses ${ctx}" "exit ${r%%|*}: ${r#*|}"
  fi
done

echo "== it accepts the safe shape, so the fix is not itself flagged =="
r="$(scan_one 'jobs:
  a:
    steps:
      - name: dispatch
        env:
          HEAD_REF: ${{ github.event.pull_request.head.ref }}
        run: |
          gh workflow run release.yml --ref "${HEAD_REF}"')"
[[ "${r%%|*}" == "0" ]] && ok "the same value passed through env: is accepted" \
  || bad "env: passthrough is accepted" "exit ${r%%|*}: ${r#*|}"

r="$(scan_one 'jobs:
  a:
    steps:
      - name: label guard
        if: github.event.pull_request.head.ref == "main"
        run: |
          echo "${{ github.run_id }}"')"
[[ "${r%%|*}" == "0" ]] && ok "the same context in an if: (not a script) is not flagged" \
  || bad "if: is not flagged" "exit ${r%%|*}: ${r#*|}"

echo "== a scan that finds no workflows is a failure, not a pass =="
EMPTY="$(mktemp -d)"; mkdir -p "${EMPTY}/.github/workflows"
out="$("${PY_BIN}" "${CHECK}" "${EMPTY}" 2>&1)"; code=$?
rm -rf "${EMPTY}"
if [[ "${code}" != "0" ]] && grep -q 'proved nothing' <<<"${out}"; then
  ok "an empty workflow directory refuses instead of reporting clean"
else
  bad "an empty workflow directory refuses" "exit ${code}: ${out}"
fi

echo "== and the real tree is clean =="
out="$("${PY_BIN}" "${CHECK}" "${ROOT}" 2>&1)"; code=$?
[[ "${code}" == "0" ]] && ok "this repository's workflows keep outsider values out of run: scripts" \
  || bad "this repository is clean" "${out}"

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL - ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early or assertions were added without bumping"
  echo "       EXPECTED_ASSERTIONS. A partial run is not a pass."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
