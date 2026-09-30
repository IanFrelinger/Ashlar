#!/usr/bin/env bash
# Fail when the number of SKIPPED cert-gate tests differs from the committed baseline, in either
# direction.
#
# WHY. `dotnet test` exits 0 when a test is skipped, and the TRX counts a skipped test in its
# `total`. cert-gate-zero-test-guard.sh floors that `total` against `dotnet test --list-tests`,
# which lists a skipped test as well, so a skip moves both sides of the floor together. Measured
# 2026-09-30 by putting [Fact(Skip = "...")] on one Certification test: run-cert-gate.sh exited 0
# and the zero-test guard printed "cert-gate reported 1288 tests (expected>=1283, derived from
# --list-tests).", while the TRX read total=1288 executed=1287.
#
# WHAT. `total - executed` from the TRX <Counters> element - the results the run reports but did
# not execute - must EQUAL the integer in scripts/cert-gate-skipped.baseline. Not the `notExecuted`
# attribute: in that same measured run it read 0 beside the skipped result, so it cannot see a skip.
#   more skipped than the baseline:  red, naming the skipped tests. If the skip is deliberate, raise
#                                    the baseline in the same change, so it is a reviewed diff.
#   fewer skipped than the baseline: red too. Lower the baseline, so slack left behind cannot absorb
#                                    a later skip without anyone seeing it.
#
# FAILS CLOSED. A missing TRX, a missing or malformed baseline, or a TRX without exactly one readable
# <Counters> element is a failure, never a pass.
#
# NOT COVERED. The count is what is enforced, so un-skipping one test and skipping another in the
# same change keeps it equal; both edits are still visible in that change's C# diff. Test failures
# are not this guard's business - `executed` includes them and run-cert-gate.sh exits with the
# `dotnet test` status.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/cert-gate-config.sh
source "${ROOT}/scripts/cert-gate-config.sh"

TRX="${1:-test-results/cert-gate.trx}"
BASELINE_REL="scripts/cert-gate-skipped.baseline"
BASELINE_FILE="${ROOT}/${BASELINE_REL}"

if [[ ! -f "${TRX}" ]]; then
  echo "cert-gate skip guard: TRX not found: ${TRX}"
  exit 1
fi

if [[ ! -f "${BASELINE_FILE}" ]]; then
  echo "cert-gate skip guard: ${BASELINE_REL} is missing, so the skipped-test count cannot be checked."
  exit 1
fi

# One integer; blank lines and lines starting with # are ignored. Trimming trailing whitespace also
# removes the CR a Windows checkout leaves (the file is text=auto, so CRLF there).
BASELINE_RAW="$(sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' < "${BASELINE_FILE}" | grep -vE '^(#|$)' || true)"
if [[ ! "${BASELINE_RAW}" =~ ^[0-9]+$ ]]; then
  echo "cert-gate skip guard: ${BASELINE_REL} must hold exactly one non-negative integer (lines starting with # are comments)."
  echo "  found: ${BASELINE_RAW:-<nothing>}"
  exit 1
fi
BASELINE=$((10#${BASELINE_RAW}))

TOTAL="$(cert_gate_trx_counter "${TRX}" total)"
EXECUTED="$(cert_gate_trx_counter "${TRX}" executed)"

if (( EXECUTED > TOTAL )); then
  echo "cert-gate skip guard: the TRX says executed=${EXECUTED} but total=${TOTAL}; it cannot be read as a run summary."
  exit 1
fi

SKIPPED=$((TOTAL - EXECUTED))

# The names are for the reader. The count above is what is enforced.
skipped_names() {
  tr '\r\n' '  ' < "${TRX}" \
    | grep -oE '<UnitTestResult [^>]*outcome="NotExecuted"[^>]*>' \
    | sed -E 's/.*[[:space:]]testName="([^"]*)".*/\1/' \
    | sed -e 's/&quot;/"/g' -e "s/&apos;/'/g" -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&amp;/\&/g' \
    | sort || true
}

print_skipped() {
  local names
  names="$(skipped_names)"
  if [[ -n "${names}" ]]; then
    printf '%s\n' "${names}" | sed 's/^/    /'
  else
    echo "    (no result in the TRX is marked NotExecuted; the counters alone say ${SKIPPED})"
  fi
}

if (( SKIPPED > BASELINE )); then
  echo "cert-gate skip guard: ${SKIPPED} cert-gate test(s) were SKIPPED; ${BASELINE_REL} records ${BASELINE}."
  echo "  total=${TOTAL} executed=${EXECUTED}. dotnet test reports a skipped test as success, so a skip"
  echo "  on a cert-gate test is a merge-blocking assertion that no longer blocks anything."
  echo "  If the skip is deliberate, set ${BASELINE_REL} to ${SKIPPED} in the same change so the"
  echo "  decision is reviewed. Otherwise remove the Skip. Skipped in this run:"
  print_skipped
  exit 1
fi

if (( SKIPPED < BASELINE )); then
  echo "cert-gate skip guard: only ${SKIPPED} cert-gate test(s) were skipped; ${BASELINE_REL} records ${BASELINE}."
  echo "  total=${TOTAL} executed=${EXECUTED}. Lower ${BASELINE_REL} to ${SKIPPED} in this change."
  echo "  Slack left in the baseline is room for a later skip to land without anyone seeing it."
  exit 1
fi

echo "cert-gate skip guard: ${SKIPPED} skipped (total=${TOTAL} executed=${EXECUTED}), matching ${BASELINE_REL}."
if (( SKIPPED > 0 )); then
  echo "  Skipped by recorded decision:"
  print_skipped
fi
