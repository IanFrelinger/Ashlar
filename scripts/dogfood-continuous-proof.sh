#!/usr/bin/env bash
# Helper script for dogfood-continuous-proof.yml workflow.
# Runs autonomy loop sweeps on canary objectives and updates the ledger.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

CANARY_OBJECTIVE="rgb-hex-parse"
RESULTS_DIR="${ROOT}/test-results"
CAMPAIGN_DIR="${ROOT}/.ashlar/campaign"
LEDGER="${ROOT}/docs/dogfood-ledger.md"

# Run a single autonomy loop sweep on the canary objective.
# This is a simplified version of the autonomy-first-flight spike,
# adapted for CI and non-interactive execution.
run_canary_sweep() {
  local timestamp
  timestamp="$(date -u +%Y%m%d-%H%M%S)"
  local log_file="${RESULTS_DIR}/dogfood-sweep-${timestamp}.log"
  
  mkdir -p "${RESULTS_DIR}"
  mkdir -p "${CAMPAIGN_DIR}/${timestamp}"
  
  echo "== Dogfood continuous proof: canary sweep ==" | tee "${log_file}"
  echo "Timestamp: ${timestamp}" | tee -a "${log_file}"
  echo "Canary: ${CANARY_OBJECTIVE}" | tee -a "${log_file}"
  echo "" | tee -a "${log_file}"
  
  # NOTE: This is a stub implementation. The real implementation depends on:
  # 1. Autonomy loop host wiring (requires FirstFlight project or equivalent)
  # 2. Container engine availability for sandbox sessions
  #
  # PR #523 (Strict+Ed25519) is now on master. Remaining blockers:
  # - Real autonomy loop wiring (not just spike infrastructure)
  # - lim-9 status (may still be open)
  #
  # For now, we document the intended flow and mark the workflow as GAP until
  # the autonomy loop host wiring is complete.
  
  echo "⚠️  STUB: Real autonomy loop sweep not yet implemented" | tee -a "${log_file}"
  echo "" | tee -a "${log_file}"
  echo "Intended flow:" | tee -a "${log_file}"
  echo "  1. Seed ${CANARY_OBJECTIVE}.md and ${CANARY_OBJECTIVE}.witness.json" | tee -a "${log_file}"
  echo "  2. Run autonomy loop with Strict verification" | tee -a "${log_file}"
  echo "  3. Capture proposal → certify → admit outcome" | tee -a "${log_file}"
  echo "  4. Return 0 for CertifiedButHeld/CertifiedAndAdmitted, 1 for ExplainedFailure" | tee -a "${log_file}"
  echo "" | tee -a "${log_file}"
  echo "Remaining dependencies:" | tee -a "${log_file}"
  echo "  - Autonomy loop host (FirstFlight or CLI command)" | tee -a "${log_file}"
  echo "  - Docker available for sandbox sessions" | tee -a "${log_file}"
  echo "  - lim-9 status verification (may be open)" | tee -a "${log_file}"
  echo "" | tee -a "${log_file}"
  
  # TODO: Replace this stub with real sweep logic:
  #
  # mkdir -p .ashlar/runtime-studio/objectives/pending
  # cp "samples/autonomy-objectives/${CANARY_OBJECTIVE}.md" \
  #    "samples/autonomy-objectives/${CANARY_OBJECTIVE}.witness.json" \
  #    .ashlar/runtime-studio/objectives/pending/
  #
  # dotnet run --project spikes/autonomy-first-flight/FirstFlight/FirstFlight.csproj -- \
  #   --sweep \
  #   --max-objectives 1 \
  #   --campaign-dir "${CAMPAIGN_DIR}/${timestamp}" \
  #   --strict | tee -a "${log_file}"
  #
  # SWEEP_EXIT="${PIPESTATUS[0]}"
  # return "${SWEEP_EXIT}"
  
  echo "Stub implementation - marking as GAP" | tee -a "${log_file}"
  return 1  # Fail until real implementation lands
}

# Append a row to docs/dogfood-ledger.md
# Usage: append_ledger <date> <demo> <pass_fail> <gap> <owner> <repro>
append_ledger() {
  local date="${1:?date required}"
  local demo="${2:?demo required}"
  local pass_fail="${3:?pass_fail required}"
  local gap="${4:-}"
  local owner="${5:?owner required}"
  local repro="${6:?repro required}"
  
  if [[ ! -f "${LEDGER}" ]]; then
    echo "Error: Ledger file not found: ${LEDGER}" >&2
    return 1
  fi
  
  # Find the table and append a new row before any trailing content
  # The ledger table starts after "| Date | Demo | Pass/Fail | Gap | Owner | Repro |"
  # and ends at the first blank line or next heading.
  
  local temp_ledger="${LEDGER}.tmp"
  local in_table=false
  local table_ended=false
  local row_inserted=false
  
  while IFS= read -r line || [[ -n "${line}" ]]; do
    echo "${line}" >> "${temp_ledger}"
    
    # Detect table header
    if [[ "${line}" =~ ^\|[[:space:]]*Date[[:space:]]*\| ]]; then
      in_table=true
      continue
    fi
    
    # Detect table separator (|------|------|...)
    if ${in_table} && [[ "${line}" =~ ^\|[-[:space:]]+\| ]]; then
      continue
    fi
    
    # If we're in the table and hit a blank line or heading, insert the row
    if ${in_table} && ! ${row_inserted} && [[ -z "${line}" || "${line}" =~ ^## ]]; then
      echo "| ${date} | ${demo} | ${pass_fail} | ${gap} | ${owner} | ${repro} |" >> "${temp_ledger}"
      row_inserted=true
      table_ended=true
      in_table=false
    fi
  done < "${LEDGER}"
  
  # If we never found the end of the table (file ended while in table), append now
  if ${in_table} && ! ${row_inserted}; then
    echo "| ${date} | ${demo} | ${pass_fail} | ${gap} | ${owner} | ${repro} |" >> "${temp_ledger}"
    row_inserted=true
  fi
  
  if ! ${row_inserted}; then
    echo "Warning: Could not find ledger table to append row. Check ledger format." >&2
    rm -f "${temp_ledger}"
    return 1
  fi
  
  mv "${temp_ledger}" "${LEDGER}"
  echo "Appended row to ledger: ${date} | ${demo} | ${pass_fail}"
}

# Main dispatch
case "${1:-}" in
  run-canary-sweep)
    run_canary_sweep
    ;;
  append-ledger)
    shift
    append_ledger "$@"
    ;;
  *)
    echo "Usage: $0 {run-canary-sweep|append-ledger <date> <demo> <pass_fail> <gap> <owner> <repro>}" >&2
    exit 1
    ;;
esac
