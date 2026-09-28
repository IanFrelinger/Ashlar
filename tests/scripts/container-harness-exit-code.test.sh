#!/usr/bin/env bash
# Tests the EXIT trap in scripts/test-in-container.sh.
#
# WHY THIS EXISTS. That harness is how every dotnet build and test in this repository is run on a
# developer machine, so its exit code is the primary signal for "did that pass". It was lying.
# `set -euo pipefail` is active, and under `set -e` an EXIT trap whose LAST command fails makes bash
# exit 1 and discard the status the script was about to exit with. The trap was
# `[[ -n "${PATCH_DIR}" ]] && rm -rf "${PATCH_DIR}"`, and PATCH_DIR is only set by --dirty, so on
# every run WITHOUT --dirty the trap ended on a failing test.
#
# Measured on 2026-09-27, and the damage was in both directions:
#   - a fully passing run exited 1
#   - a genuine inner `exit 3` also exited 1, so a real failure's code was destroyed too
# Only the second of those is obvious once you know; the first is what teaches people to ignore the
# harness's exit code, and a signal everyone ignores is not a signal.
#
# The fix is that the trap's last construct cannot fail: an `if` returns 0 when its condition is
# false. These assertions run the REAL function extracted from the script rather than a copy, so
# reverting to the && form reddens them instead of quietly restoring the defect.
#
# Run:  bash tests/scripts/container-harness-exit-code.test.sh
# Pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/test-in-container.sh"

EXPECTED_ASSERTIONS=6

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# --- extract the real cleanup function -------------------------------------------------------
# Both shapes are handled on purpose. If someone reverts to the single-line && form, extraction must
# still succeed so that the BEHAVIOURAL assertions below are what fails — "extraction broke" would
# send the next reader to the wrong place.
FUNC=""
FIRST="$(grep -n '^cleanup()' "${SCRIPT}" | head -1 | cut -d: -f1 || true)"
if [[ -n "${FIRST}" ]]; then
  HEAD_LINE="$(sed -n "${FIRST}p" "${SCRIPT}")"
  if [[ "${HEAD_LINE}" == *"}"* ]]; then
    FUNC="${HEAD_LINE}"
  else
    FUNC="$(sed -n "${FIRST},/^}/p" "${SCRIPT}")"
  fi
fi

if [[ -n "${FUNC}" && "${FUNC}" == *"PATCH_DIR"* ]]; then
  ok "found the cleanup function in $(basename "${SCRIPT}")"
else
  bad "found the cleanup function in $(basename "${SCRIPT}")" \
      "could not extract a cleanup() mentioning PATCH_DIR; the assertions below would prove nothing"
fi

if grep -qF 'trap cleanup EXIT' "${SCRIPT}"; then
  ok "cleanup is still registered on EXIT"
else
  bad "cleanup is still registered on EXIT" \
      "without the trap, --dirty leaves its mktemp -d behind on every run"
fi

# --- run it the way the script does: set -e active, trap on EXIT ------------------------------
# Returns the exit status a script would produce with this trap installed.
status_with() {
  local patchdir="$1" body="$2"
  bash -c "set -euo pipefail
${FUNC}
PATCH_DIR='${patchdir}'
trap cleanup EXIT
${body}" >/dev/null 2>&1
  printf '%s' "$?"
}

GOT="$(status_with "" "true")"
if [[ "${GOT}" == "0" ]]; then
  ok "a passing run without --dirty exits 0"
else
  bad "a passing run without --dirty exits 0" \
      "got ${GOT}. Under set -e the trap's last command must not be able to fail, or every run that
         did not pass --dirty reports failure while passing"
fi

GOT="$(status_with "" "exit 3")"
if [[ "${GOT}" == "3" ]]; then
  ok "a real failure's exit code survives the trap (3 stays 3)"
else
  bad "a real failure's exit code survives the trap (3 stays 3)" \
      "got ${GOT}. The trap is overwriting the status the script meant to exit with, so the harness
         cannot report what the command it ran actually returned"
fi

# --- the --dirty path must still clean up, and still exit cleanly ----------------------------
TMP="$(mktemp -d)"
GOT="$(status_with "${TMP}" "true")"
if [[ "${GOT}" == "0" ]]; then
  ok "a passing run with PATCH_DIR set exits 0"
else
  bad "a passing run with PATCH_DIR set exits 0" "got ${GOT}"
fi

if [[ ! -d "${TMP}" ]]; then
  ok "PATCH_DIR is actually removed, so --dirty does not leak temp directories"
else
  bad "PATCH_DIR is actually removed, so --dirty does not leak temp directories" \
      "${TMP} still exists; the trap ran but did not clean up"
  rm -rf "${TMP}"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL — ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early or assertions were added without bumping"
  echo "       EXPECTED_ASSERTIONS. A partial run is not a pass."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
