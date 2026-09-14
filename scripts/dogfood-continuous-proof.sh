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
#
# This drives the SAME host the first-flight spike drives — FirstFlight --sweep — against the
# real objective store. It is not a simulation of the loop; it is the loop, with admission held.
#
# The four knobs the loop reads are ENVIRONMENT VARIABLES, not CLI flags. An earlier draft of
# this function carried a commented-out invocation passing --max-objectives, --campaign-dir and
# --strict; none of those are parsed, and passing them would have been silently ignored.
#
#   ASHLAR_OBJECTIVES_ROOT    where the store lives            (required)
#   ASHLAR_SWEEP_MAX_OBJECTIVES  objectives attempted per sweep (we want exactly 1)
#   ASHLAR_CAMPAIGN_DIR       where per-run artefacts land
#   ASHLAR_SWEEP_PROPOSER     "ollama" for a live model; unset uses the RECORDED proposal
#                             committed beside the objective, so this needs no model and no
#                             network. Setting it is how the live-model lane turns on.
#
# Sandbox sessions are on (build AND execute inside an attested container), so this needs a
# working container engine and will pull an SDK image on a cold runner.
run_canary_sweep() {
  local timestamp
  timestamp="$(date -u +%Y%m%d-%H%M%S)"
  local log_file="${RESULTS_DIR}/dogfood-sweep-${timestamp}.log"
  local run_campaign_dir="${CAMPAIGN_DIR}/${timestamp}"
  local objectives_root="${ROOT}/.ashlar/runtime-studio/objectives"
  local samples="${ROOT}/samples/autonomy-objectives"

  mkdir -p "${RESULTS_DIR}" "${run_campaign_dir}"

  echo "== Dogfood continuous proof: canary sweep ==" | tee "${log_file}"
  echo "Timestamp: ${timestamp}" | tee -a "${log_file}"
  echo "Canary: ${CANARY_OBJECTIVE}" | tee -a "${log_file}"
  echo "" | tee -a "${log_file}"

  # --- preconditions, each reported by name so a GAP row can say which one failed -------------
  local objective="${samples}/${CANARY_OBJECTIVE}.md"
  local witness="${samples}/${CANARY_OBJECTIVE}.witness.json"
  local proposal="${samples}/${CANARY_OBJECTIVE}.proposal.json"

  local missing=0
  for f in "${objective}" "${witness}"; do
    if [[ ! -f "${f}" ]]; then
      echo "PRECONDITION FAILED: missing ${f}" | tee -a "${log_file}"
      missing=1
    fi
  done
  if [[ "${SWEEP_PROPOSER:-}" != "ollama" && ! -f "${proposal}" ]]; then
    echo "PRECONDITION FAILED: no recorded proposal at ${proposal}, and SWEEP_PROPOSER is not ollama" | tee -a "${log_file}"
    missing=1
  fi
  if ! command -v docker >/dev/null 2>&1; then
    echo "PRECONDITION FAILED: no container engine on PATH; sandbox sessions cannot start" | tee -a "${log_file}"
    missing=1
  fi
  if [[ "${missing}" -ne 0 ]]; then
    echo "SWEEP: refusing to run with unmet preconditions (above)" | tee -a "${log_file}"
    return 1
  fi

  # --- stage the canary into the store --------------------------------------------------------
  # A sweep CLAIMS a pending objective and moves it to in-progress, so the store is rebuilt from
  # the committed samples every run. That keeps the run reproducible and stops a half-finished
  # earlier sweep from deciding this one's verdict.
  rm -rf "${objectives_root}"
  mkdir -p "${objectives_root}/pending"
  cp "${objective}" "${witness}" "${objectives_root}/pending/"
  [[ -f "${proposal}" ]] && cp "${proposal}" "${objectives_root}/pending/"

  echo "Staged ${CANARY_OBJECTIVE} into ${objectives_root}/pending" | tee -a "${log_file}"
  echo "" | tee -a "${log_file}"

  # --- run the loop ---------------------------------------------------------------------------
  local sweep_exit=0
  ASHLAR_OBJECTIVES_ROOT="${objectives_root}" \
  ASHLAR_SWEEP_MAX_OBJECTIVES=1 \
  ASHLAR_CAMPAIGN_DIR="${run_campaign_dir}" \
  ${SWEEP_PROPOSER:+ASHLAR_SWEEP_PROPOSER="${SWEEP_PROPOSER}"} \
    dotnet run --project spikes/autonomy-first-flight/FirstFlight/FirstFlight.csproj \
      -c Release -- --sweep 2>&1 | tee -a "${log_file}" || true
  sweep_exit="${PIPESTATUS[0]}"

  echo "" | tee -a "${log_file}"
  echo "SWEEP exit code: ${sweep_exit}" | tee -a "${log_file}"

  # --- what the exit code means ---------------------------------------------------------------
  # SweepMode returns 0 when it ATTEMPTED at least one objective, and 1 when it attempted none
  # (empty store, or no objective was eligible because a witness or proposal was missing).
  # It does NOT encode the certification verdict: a held-but-certified iteration and an explained
  # failure both exit 0. The verdict is in the log the harness writes above, and the ledger row
  # records the sweep's completion, not its admission outcome. Do not read a PASS row as "the
  # candidate was admitted" - HoldAdmission is on, so nothing is ever admitted here.
  if [[ "${sweep_exit}" -ne 0 ]]; then
    echo "SWEEP: the loop attempted no objective - see the log above for which precondition the harness rejected" | tee -a "${log_file}"
  fi

  return "${sweep_exit}"
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
  local row_inserted=false
  
  while IFS= read -r line || [[ -n "${line}" ]]; do
    # Detect table header
    if [[ "${line}" =~ ^\|[[:space:]]*Date[[:space:]]*\| ]]; then
      echo "${line}" >> "${temp_ledger}"
      in_table=true
      continue
    fi
    
    # Detect table separator (|------|------|...)
    if ${in_table} && [[ "${line}" =~ ^\|[-[:space:]]+\| ]]; then
      echo "${line}" >> "${temp_ledger}"
      continue
    fi
    
    # If we're in the table and hit a blank line or heading, insert the row BEFORE the terminator
    if ${in_table} && ! ${row_inserted} && [[ -z "${line}" || "${line}" =~ ^## ]]; then
      echo "| ${date} | ${demo} | ${pass_fail} | ${gap} | ${owner} | ${repro} |" >> "${temp_ledger}"
      row_inserted=true
      in_table=false
    fi
    
    # Write the current line (including the terminating blank/heading after the row)
    echo "${line}" >> "${temp_ledger}"
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
