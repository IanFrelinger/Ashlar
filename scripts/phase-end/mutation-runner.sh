#!/usr/bin/env bash
# ASHLAR_MUTATION_RUNNER for scripts/mutation-check.sh, for the phase-end validation and the workflow checker.
#
# mutation-check.sh clones a committed SHA, applies exactly one replacement, and runs this script twice:
# `<clone> red` with the mutation applied and `<clone> green` after the file is restored. It reads the verdict
# from a dotnet-shaped header and summary line, so this runner prints those two lines. The count is the
# unittest suite of scripts/phase-end/test_validate.py plus one for tests/scripts/claude-workflows.test.sh,
# which proves the workflow checker both ways. Every summary is marked runner=override by mutation-check.sh.
#
#   ASHLAR_MUTATION_RUNNER="bash scripts/phase-end/mutation-runner.sh" \
#   scripts/mutation-check.sh --id <name> --file scripts/phase-end/<f> --old '<text>' --new '<text>'
set -uo pipefail

CLONE="${1:?clone path}"
PHASE="${2:?phase (red|green)}"

PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'import json; print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
[[ -n "${PY_BIN}" ]] || { echo "mutation-runner: no python" >&2; exit 1; }

echo "Test run for ${CLONE}/scripts/phase-end/phase_end.dll (python-unittest + claude-workflows)"
LOG="$(mktemp)"
WF="$(mktemp)"
trap 'rm -f "${LOG}" "${WF}"' EXIT
( cd "${CLONE}" && PYTHONDONTWRITEBYTECODE=1 PYTHONWARNINGS=ignore "${PY_BIN}" -m unittest discover \
    -s scripts/phase-end -p 'test_*.py' -v ) >"${LOG}" 2>&1
rc=$?
( cd "${CLONE}" && bash tests/scripts/claude-workflows.test.sh ) >"${WF}" 2>&1
wrc=$?
grep -E '^(FAIL|ERROR):' "${LOG}" | sed 's/^/  /'
if [[ "${wrc}" -ne 0 ]]; then
  echo "FAIL: tests/scripts/claude-workflows.test.sh"
  grep -E 'FAIL|ERROR' "${WF}" | sed 's/^/  /'
fi
ran="$(grep -Eo '^Ran [0-9]+ tests?' "${LOG}" | tail -1 | grep -Eo '[0-9]+' || echo 0)"
failed="$(grep -Ec '^(FAIL|ERROR):' "${LOG}")"
skipped="$(grep -Eo 'skipped=[0-9]+' "${LOG}" | tail -1 | grep -Eo '[0-9]+' || echo 0)"
total=$(( ran + 1 ))
[[ "${wrc}" -ne 0 ]] && failed=$(( failed + 1 ))
passed=$(( total - failed - skipped ))
word="Passed"; [[ "${failed}" -gt 0 ]] && word="Failed"
echo "${word}!  - Failed: ${failed}, Passed: ${passed}, Skipped: ${skipped}, Total: ${total}, Duration: 0 s - phase_end.dll (python-unittest)"
echo "mutation-runner: phase=${PHASE} exit=${rc}/${wrc}"
[[ "${rc}" -eq 0 && "${wrc}" -eq 0 ]]
