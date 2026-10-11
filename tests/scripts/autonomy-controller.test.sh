#!/usr/bin/env bash
# Runs the budgeted-autonomy controller's unit tests (tools/autonomy-controller/tests).
#
# WHY A WRAPPER. scripts/ci/run-repo-gates.sh discovers tests/scripts/*.test.sh and runs each one
# bare; it does not discover Python files, so a Python suite only reaches the required `shell-lint`
# check through a file like this one. The suite proves the controller's budget and recovery rules:
# duplicate events allocate no second worker, a reservation survives a restart, an expired lease is
# not taken until the previous holder is confirmed stopped, missing or corrupt state stops dispatch,
# a worker-side policy edit cannot raise active limits, a dry run writes nothing and calls no adapter,
# quota exhaustion waits without a paid fallback, and an empty queue makes zero adapter calls.
#
# THE FLOOR. A discovery that finds no tests exits 0 ("Ran 0 tests ... OK"), which is the exact
# green-by-running-nothing shape the repo gates exist to prevent, so this wrapper requires at least
# MINIMUM_TESTS. Raise it when you add tests; never lower it to make the gate pass.
#
# Run:  bash tests/scripts/autonomy-controller.test.sh
# Python 3.8+ and sqlite3 only: no network, no dotnet, no container.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SUITE_ROOT="${ROOT}/tools/autonomy-controller"
MINIMUM_TESTS=360

# Probe by RUNNING the interpreter, not `command -v`: on Windows an App Execution Alias stub answers
# `command -v python3` and exits 49 without running anything.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'import sqlite3; print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "autonomy-controller.test: ERROR: no working python interpreter with sqlite3 (tried python3, python, py)." >&2
  exit 1
fi

LOG="$(mktemp)"
trap 'rm -f "${LOG}"' EXIT

( cd "${SUITE_ROOT}" && PYTHONDONTWRITEBYTECODE=1 PYTHONWARNINGS=ignore "${PY_BIN}" -m unittest discover \
    -s tests -t . -p 'test_*.py' ) >"${LOG}" 2>&1
rc=$?
cat "${LOG}"

RAN="$(grep -Eo '^Ran [0-9]+ tests?' "${LOG}" | tail -1 | grep -Eo '[0-9]+' || true)"
if [[ -z "${RAN}" ]]; then
  echo "autonomy-controller.test: ERROR: no 'Ran N tests' summary in the output; the suite did not run." >&2
  exit 1
fi
if [[ "${RAN}" -lt "${MINIMUM_TESTS}" ]]; then
  echo "autonomy-controller.test: ERROR: ran ${RAN} test(s), expected at least ${MINIMUM_TESTS}." >&2
  echo "            Discovery has stopped reaching the suite. Fix the discovery; do not lower the floor." >&2
  exit 1
fi
if [[ "${rc}" -ne 0 ]]; then
  echo "autonomy-controller.test: FAIL (exit ${rc}, ${RAN} tests ran)" >&2
  exit "${rc}"
fi
echo "autonomy-controller.test: ${RAN} tests passed"
