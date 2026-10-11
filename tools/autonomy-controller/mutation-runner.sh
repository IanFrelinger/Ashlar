#!/usr/bin/env bash
# ASHLAR_MUTATION_RUNNER for scripts/mutation-check.sh, for this Python package.
#
# mutation-check.sh clones a committed SHA, applies exactly one replacement, and runs this script
# twice: `<clone> red` with the mutation applied and `<clone> green` after `git checkout --` restored
# the file. It reads the verdict from a dotnet-shaped header and summary line, so this runner prints
# those two lines from the unittest result. Every summary it produces is marked runner=override by
# mutation-check.sh, which is honest: this is the Python suite on the host, not the container.
#
#   ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh" \
#   scripts/mutation-check.sh --id <name> --file tools/autonomy-controller/ashlar_controller/<f>.py \
#     --old '<text>' --new '<text>'
set -uo pipefail

CLONE="${1:?clone path}"
PHASE="${2:?phase (red|green)}"
SUITE="${CLONE}/tools/autonomy-controller"

PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'import sqlite3; print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
[[ -n "${PY_BIN}" ]] || { echo "mutation-runner: no python" >&2; exit 1; }

echo "Test run for ${SUITE}/ashlar_controller.dll (python-unittest)"
LOG="$(mktemp)"
trap 'rm -f "${LOG}"' EXIT
( cd "${SUITE}" && PYTHONDONTWRITEBYTECODE=1 PYTHONWARNINGS=ignore "${PY_BIN}" -m unittest discover \
    -s tests -t . -p 'test_*.py' -v ) >"${LOG}" 2>&1
rc=$?
grep -E '^(FAIL|ERROR):' "${LOG}" | sed 's/^/  /'
summary="$(grep -Eo '^Ran [0-9]+ tests?' "${LOG}" | tail -1 | grep -Eo '[0-9]+' || echo 0)"
failed="$(grep -Ec '^(FAIL|ERROR):' "${LOG}")"
skipped="$(grep -Eo 'skipped=[0-9]+' "${LOG}" | tail -1 | grep -Eo '[0-9]+' || echo 0)"
passed=$(( summary - failed - skipped ))
word="Passed"; [[ "${failed}" -gt 0 ]] && word="Failed"
echo "${word}!  - Failed: ${failed}, Passed: ${passed}, Skipped: ${skipped}, Total: ${summary}, Duration: 0 s - ashlar_controller.dll (python-unittest)"
echo "mutation-runner: phase=${PHASE} exit=${rc}"
exit "${rc}"
