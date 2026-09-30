#!/usr/bin/env bash
# Write generation-safety results to GITHUB_STEP_SUMMARY (or stdout locally).
#
# Exits 1 when there is nothing to summarise - no TRX, or a TRX whose <Counters> cannot be read. It
# used to exit 0 with a "Tests may not have run" note, which painted a green step beside a run that
# produced no results. Every run that reaches this without a TRX has already failed in
# run-cert-gate.sh (its guards refuse a missing TRX), so exiting 1 here changes no verdict; it only
# stops this step contradicting it.
#
# Informational otherwise: the gate itself is run-cert-gate.sh and its guards, not this table.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/cert-gate-config.sh
source "${ROOT}/scripts/cert-gate-config.sh"

TRX="${1:-test-results/cert-gate.trx}"
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

if [[ ! -f "${TRX}" ]]; then
  {
    echo "## cert-gate — generation safety"
    echo ""
    echo "TRX not found (\`${TRX}\`). No results to report: the tests did not run, or ran and wrote nothing."
  } >> "${SUMMARY}"
  echo "cert-gate summary: TRX not found: ${TRX}" >&2
  exit 1
fi

outcome_for() {
  local short_name="$1"
  local line
  line="$(grep -F "testName=\"Ashlar.Tests.Infrastructure.Tests.Adaptation.GenerationSafetyTests.${short_name}\"" "${TRX}" | head -1 || true)"
  if [[ -z "${line}" ]]; then
    echo "not found"
    return
  fi
  if grep -q 'outcome="Passed"' <<< "${line}"; then
    echo "✅ Pass"
  elif grep -q 'outcome="Failed"' <<< "${line}"; then
    echo "❌ Fail"
  else
    grep -oE 'outcome="[^"]+"' <<< "${line}" | head -1 | sed 's/outcome="//;s/"//'
  fi
}

# Plain assignments, so an unreadable <Counters> stops the script under `set -e`.
TOTAL="$(cert_gate_trx_counter "${TRX}" total)"
EXECUTED="$(cert_gate_trx_counter "${TRX}" executed)"
SKIPPED=$((TOTAL - EXECUTED))

{
  echo "## cert-gate — generation safety"
  echo ""
  echo "| Test | Result |"
  echo "|------|--------|"
  echo "| **KEY: gate catches a wrong generation** — \`BuggyGeneration_StrongWitness_Rejects\` | $(outcome_for "BuggyGeneration_StrongWitness_Rejects") |"
  echo "| \`GoodGeneration_StrongWitness_Admits_WithZeroEscapeRate\` | $(outcome_for "GoodGeneration_StrongWitness_Admits_WithZeroEscapeRate") |"
  echo "| \`GoodGeneration_WeakWitness_Rejects_WithTeeth\` | $(outcome_for "GoodGeneration_WeakWitness_Rejects_WithTeeth") |"
  echo "| \`DependencyLeakGeneration_Rejects_DependencyCheck\` | $(outcome_for "DependencyLeakGeneration_Rejects_DependencyCheck") |"
  echo ""
  echo "cert-gate tests: **${EXECUTED}** executed of **${TOTAL}** reported, **${SKIPPED}** skipped."
  echo "Reported must be ≥ the \`--list-tests\` count for the cert-gate filter, and skipped must equal \`scripts/cert-gate-skipped.baseline\`."
} >> "${SUMMARY}"
