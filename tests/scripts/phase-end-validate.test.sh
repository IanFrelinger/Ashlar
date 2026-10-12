#!/usr/bin/env bash
# Unit tests of scripts/phase-end/validate.py, the phase-end validation `/handoff` runs before every handoff
# (.claude/commands/handoff.md). Discovered and run bare by scripts/ci/run-repo-gates.sh in the required
# shell-lint check. No test touches git, gh, bash or the network: every command goes through a fake runner.
#
# THE FLOOR. A discovery that finds no tests exits 0 ("Ran 0 tests ... OK"), the green-by-running-nothing
# shape the repo gates exist to prevent, so this wrapper requires at least MINIMUM_TESTS. Raise it when you
# add tests; never lower it to make the gate pass.
#
# Run:  bash tests/scripts/phase-end-validate.test.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MINIMUM_TESTS=34

# Probe by RUNNING the interpreter, not `command -v`: on Windows an App Execution Alias stub answers
# `command -v python3` and exits 49 without running anything.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'import json; print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "phase-end-validate.test: ERROR: no working python interpreter (tried python3, python, py)." >&2
  exit 1
fi

LOG="$(mktemp)"
trap 'rm -f "${LOG}"' EXIT

( cd "${ROOT}" && PYTHONDONTWRITEBYTECODE=1 "${PY_BIN}" -m unittest discover -s scripts/phase-end -p 'test_*.py' ) \
  >"${LOG}" 2>&1
rc=$?
cat "${LOG}"

RAN="$(grep -Eo '^Ran [0-9]+ tests?' "${LOG}" | tail -1 | grep -Eo '[0-9]+' || true)"
if [[ -z "${RAN}" ]]; then
  echo "phase-end-validate.test: ERROR: no 'Ran N tests' summary in the output; the suite did not run." >&2
  exit 1
fi
if [[ "${RAN}" -lt "${MINIMUM_TESTS}" ]]; then
  echo "phase-end-validate.test: ERROR: ran ${RAN} test(s), expected at least ${MINIMUM_TESTS}." >&2
  echo "            Discovery has stopped reaching the suite. Fix the discovery; do not lower the floor." >&2
  exit 1
fi
if [[ "${rc}" -ne 0 ]]; then
  echo "phase-end-validate.test: FAIL (exit ${rc}, ${RAN} tests ran)" >&2
  exit "${rc}"
fi
echo "phase-end-validate.test: ${RAN} tests passed"
