#!/usr/bin/env bash
# Tests for scripts/lib/pipeline_gate_assertions.py, driven by what `ashlar pipeline run --format-json`
# actually printed.
#
# WHY A TEST AND NOT JUST THE GATE. kernel-gate-tier-b.sh (compat-gate Tier B) and ship-gate-tier-a.sh run
# the real CLI, and the real CLI's output satisfies a strict check and a vacuous one equally: a fail-closed
# check that accepted ANY state would stay green there. Only an input that must be refused can tell them
# apart, so every check below is driven with one, beside the real output it must accept.
#
# Fixtures in tests/scripts/fixtures/pipeline-gate-assertions/ are CLI stdout, not typed by hand:
#   unconfigured, test-hooks, configured-success, configured-fallback, resume-source,
#   resume-unconfigured, resume-target    the devtest container's compat-gate run at 08168352e
#                                         (#797), each run's log lines and its JSON payload
#   unconfigured-hybrid-root              the same gate's run at 275cce844: a Hybrid root stage falling
#                                         back from the deterministic placeholder to the agentic one
#   pre363-fabricated-success, pre363-fabricated-fallback
#                                         compat-gate CI run 32612595835 (2026-08-23, 29049b116): the
#                                         placeholder adapters BEFORE #363, reporting ok=true and
#                                         Completed for stages they never ran. The one edit: the GitHub
#                                         log prefix (job, step, timestamp) was cut from each line.
# The remaining cases change one field of a real fixture with sed, so each check fires on its own.
#
# COVERAGE. Every require() in the helper has a case below that is refused by that check and by no
# earlier one, so making any single check vacuous turns at least one case red. That was measured, not
# assumed: PR #798 ran scripts/mutation-check.sh once per require(), each replaced by `require(True or ...`.
# A new require() needs a new case here, and the same measurement.
#
# Run:  bash tests/scripts/pipeline-gate-assertions.test.sh
# Needs python (3.8+). No network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HELPER="${ROOT}/scripts/lib/pipeline_gate_assertions.py"
FX="${ROOT}/tests/scripts/fixtures/pipeline-gate-assertions"

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=45

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

# Probe by running, never by `command -v`: on Windows `python3` can be an App Execution Alias stub.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'import json, sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    PY_BIN="${cand}"; break
  fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "FAIL - no working python 3.8+ (tried python3, python, py); nothing here can be tested."
  exit 1
fi

# Non-vacuity control. A missing helper makes python exit 2 and a missing fixture reads as an unreadable
# log, which the helper also refuses, so every "must fail" case below would pass for the wrong reason.
if [[ ! -f "${HELPER}" ]]; then
  echo "FAIL - ${HELPER} does not exist; nothing here can be tested."
  exit 1
fi
FIXTURES=(unconfigured test-hooks unconfigured-hybrid-root configured-success configured-fallback resume-source
          resume-unconfigured resume-target pre363-fabricated-success pre363-fabricated-fallback)
for f in "${FIXTURES[@]}"; do
  if [[ ! -s "${FX}/${f}.stdout.txt" ]]; then
    echo "FAIL - fixture ${FX}/${f}.stdout.txt is missing or empty; the cases that use it would test nothing."
    exit 1
  fi
done

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

fx() { printf '%s' "${FX}/$1.stdout.txt"; }

# derive <name> <fixture> <sed-expression>: a copy of a real fixture with one field changed. Refuses an
# expression that changes nothing, so a case cannot silently test the unmodified fixture.
derive() {
  local out="${TMP}/$1.stdout.txt"
  sed -e "$3" "$(fx "$2")" > "${out}"
  if cmp -s "${out}" "$(fx "$2")"; then
    echo "FAIL - derive $1: '$3' changed nothing in $2; the case would test the real fixture." >&2
    exit 1
  fi
  printf '%s' "${out}"
}

# Echoes "<exit>|<output>".
run() {
  local out code
  out="$("${PY_BIN}" "${HELPER}" "$@" 2>&1)"; code=$?
  printf '%s|%s' "${code}" "${out}"
}

expect() {  # label want_rc needle got
  local label="$1" want="$2" needle="$3" got="$4"
  local code="${got%%|*}" out="${got#*|}"
  if [[ "${code}" == "${want}" ]] && grep -qF -- "${needle}" <<<"${out}"; then
    ok "${label}"
  else
    bad "${label}" "wanted exit ${want} and '${needle}'; got exit ${code}: ${out}"
  fi
}

echo "== fail-closed: the unconfigured placeholder must fail, and say so =="
expect "the real unconfigured run is accepted" 0 \
  "PASS fail-closed [unconfigured]" "$(run fail-closed "$(fx unconfigured)" 2 unconfigured)"
expect "the real unconfigured run with test hooks on is accepted" 0 \
  "PASS fail-closed [test-hooks]" "$(run fail-closed "$(fx test-hooks)" 2 test-hooks)"
expect "the pre-#363 fabricated success is refused" 1 \
  "a run that did no work exited as a success" "$(run fail-closed "$(fx pre363-fabricated-success)" 0 x)"
expect "the pre-#363 fabricated success is refused on ok even with a non-zero exit" 1 \
  "ok=True, expected false" "$(run fail-closed "$(fx pre363-fabricated-success)" 2 x)"
expect "an unconfigured run that exits 0 is refused" 1 \
  "exit code 0" "$(run fail-closed "$(fx unconfigured)" 0 x)"
expect "a payload without ok is refused (ok must be false, not merely falsy)" 1 \
  "ok=None, expected false" \
  "$(run fail-closed "$(derive no-ok unconfigured 's/"ok":false,//')" 2 x)"
expect "state Completed with ok=false is refused" 1 \
  "state='Completed', expected 'Failed'" \
  "$(run fail-closed "$(derive state-completed unconfigured 's/"state":"Failed","startedAt"/"state":"Completed","startedAt"/')" 2 x)"
expect "an ingest error that does not name the placeholder is refused" 1 \
  "ingest error does not name the unconfigured deterministic placeholder" \
  "$(run fail-closed "$(derive other-error unconfigured 's/No deterministic pipeline adapter is configured/Disk full/')" 2 x)"
expect "a stage reported Completed with no adapter configured is refused" 1 \
  "stages reported Completed with no adapter configured: ['hybrid']" \
  "$(run fail-closed "$(derive hybrid-ran unconfigured 's/{"stageId":"hybrid","state":"Pending","attempt":0}/{"stageId":"hybrid","state":"Completed","attempt":1}/')" 2 x)"
expect "a log with no JSON payload is refused" 1 \
  "no JSON payload" "$(run fail-closed "$(derive no-json unconfigured '/^{/d')" 2 x)"
expect "an ingest stage that did not fail is refused, though its error names the placeholder" 1 \
  "ingest state='Skipped', expected 'Failed'" \
  "$(run fail-closed "$(derive ingest-skipped unconfigured 's/"stageId":"ingest","state":"Failed"/"stageId":"ingest","state":"Skipped"/')" 2 x)"
expect "an ingest failure on the wrong worker type is refused" 1 \
  "ingest workerType='Agentic', expected 'Deterministic'" \
  "$(run fail-closed "$(derive ingest-agentic unconfigured 's/"workerType":"Deterministic"/"workerType":"Agentic"/')" 2 x)"
expect "a payload listing the ingest stage twice is refused" 1 \
  "expected exactly one 'ingest' stage, found 2" \
  "$(run fail-closed "$(derive ingest-twice unconfigured 's/"stages":\[/"stages":[{"stageId":"ingest","state":"Failed","attempt":1,"workerType":"Deterministic","workerId":"deterministic-default","error":"No deterministic pipeline adapter is configured; the default placeholder performs no work."},/')" 2 x)"
expect "a payload with an empty stage list is refused" 1 \
  "payload lists no stages" \
  "$(run fail-closed "$(derive no-stages unconfigured 's/"stages":\[.*\]}}/"stages":[]}}/')" 2 x)"
expect "a payload whose data is null is refused" 1 \
  "payload has no data object" \
  "$(run fail-closed "$(derive null-data unconfigured 's/"data":{.*}}/"data":null}/')" 2 x)"

echo "== fail-closed-agentic: a Hybrid root must fall back to the agentic placeholder, and it must fail =="
expect "the real unconfigured Hybrid-root run is accepted" 0 \
  "PASS fail-closed-agentic [unconfigured-hybrid-root]" \
  "$(run fail-closed-agentic "$(fx unconfigured-hybrid-root)" 2 unconfigured-hybrid-root)"
expect "the gate-demo run, whose Hybrid stage never ran, is refused" 1 \
  "hybrid state='Pending', expected 'Failed'" "$(run fail-closed-agentic "$(fx unconfigured)" 2 x)"
expect "a Hybrid failure that stopped on the deterministic worker is refused" 1 \
  "hybrid workerType='Deterministic', expected 'Agentic'" \
  "$(run fail-closed-agentic "$(derive hybrid-deterministic unconfigured-hybrid-root 's/"workerType":"Agentic"/"workerType":"Deterministic"/')" 2 x)"
expect "a Hybrid error that does not name the agentic placeholder is refused" 1 \
  "hybrid error does not name the unconfigured agentic placeholder" \
  "$(run fail-closed-agentic "$(derive agentic-other-error unconfigured-hybrid-root 's/No agentic pipeline adapter is configured/No deterministic pipeline adapter is configured/')" 2 x)"

echo "== configured-success: the configured stub must complete, on the stub's worker =="
expect "the real configured run is accepted" 0 \
  "PASS configured-success [configured]" "$(run configured-success "$(fx configured-success)" 0 configured)"
expect "a configured run that exits non-zero is refused" 1 \
  "exit code 1, expected 0" "$(run configured-success "$(fx configured-success)" 1 x)"
expect "the pre-#363 success, done by the placeholder and not the stub, is refused" 1 \
  "ingest workerId='deterministic-default'" "$(run configured-success "$(fx pre363-fabricated-success)" 0 x)"
expect "an unconfigured run is not a configured success" 1 \
  "ok=False, expected true" "$(run configured-success "$(fx unconfigured)" 0 x)"
expect "a stage left Pending is refused" 1 \
  "stages not Completed: ['hybrid']" \
  "$(run configured-success "$(derive hybrid-pending configured-success 's/"stageId":"hybrid","state":"Completed"/"stageId":"hybrid","state":"Pending"/')" 0 x)"
expect "a run whose own state is Failed is refused, though every stage Completed" 1 \
  "state='Failed', expected 'Completed'" \
  "$(run configured-success "$(derive run-failed configured-success 's/"state":"Completed","startedAt"/"state":"Failed","startedAt"/')" 0 x)"

echo "== fallback: the Hybrid stage must fall back to the Agentic stub =="
expect "the real configured fallback run is accepted" 0 \
  "PASS fallback [configured]" "$(run fallback "$(fx configured-fallback)" 0 configured)"
expect "a run whose Hybrid stage stayed Deterministic is refused" 1 \
  "the fallback chain did not switch to Agentic" "$(run fallback "$(fx configured-success)" 0 x)"
expect "the pre-#363 fallback, done by the placeholder and not the stub, is refused" 1 \
  "hybrid workerId='agentic-default'" "$(run fallback "$(fx pre363-fabricated-fallback)" 0 x)"
expect "a fallback run that exits non-zero is refused" 1 \
  "exit code 2, expected 0" "$(run fallback "$(fx configured-fallback)" 2 x)"
# AllowNonCriticalStageFailures (which this run uses) lets a Hybrid stage that failed outright still leave
# ok=true, Completed and exit 0, so only the stage's own state says whether the fallback worked.
expect "a Hybrid stage that failed on its Agentic fallback is refused, though the run reads ok" 1 \
  "hybrid state='Failed', expected 'Completed'" \
  "$(run fallback "$(derive hybrid-failed configured-fallback 's/"stageId":"hybrid","state":"Completed"/"stageId":"hybrid","state":"Failed"/')" 0 x)"
expect "a fallback run whose own state is Failed is refused" 1 \
  "state='Failed', expected 'Completed'" \
  "$(run fallback "$(derive fallback-run-failed configured-fallback 's/"state":"Completed","startedAt"/"state":"Failed","startedAt"/')" 0 x)"
expect "a fallback run with ok=false is refused" 1 \
  "ok=False, expected true" \
  "$(run fallback "$(derive fallback-not-ok configured-fallback 's/"ok":true/"ok":false/')" 0 x)"

echo "== resume: the target must come from the persisted source =="
expect "an unconfigured resume that stays Failed is accepted" 0 \
  "PASS resume [unconfigured]" \
  "$(run resume "$(fx resume-source)" 2 "$(fx resume-unconfigured)" 2 failed unconfigured)"
expect "a configured resume that completes is accepted" 0 \
  "PASS resume [configured]" \
  "$(run resume "$(fx resume-source)" 2 "$(fx resume-target)" 0 completed configured)"
expect "a completed resume is refused where the run must stay Failed" 1 \
  "a run that did no work exited as a success" \
  "$(run resume "$(fx resume-source)" 2 "$(fx resume-target)" 0 failed x)"
expect "an unconfigured resume is refused where the run must complete" 1 \
  "exit code 2, expected 0" \
  "$(run resume "$(fx resume-source)" 2 "$(fx resume-unconfigured)" 2 completed x)"
expect "a fresh run (ingest attempt 1) is not a resume" 1 \
  "the target did not resume from the persisted run" \
  "$(run resume "$(fx resume-source)" 2 "$(fx configured-success)" 0 completed x)"
expect "a target that did not find its source is refused" 1 \
  "did not find the persisted source run" \
  "$(run resume "$(fx resume-source)" 2 "$(derive lost-source resume-target '1i Resume requested for run gate-resume-source, but no prior run was found. Starting fresh run.')" 0 completed x)"
expect "a source that exited 0 is refused" 1 \
  "source run exited 0" \
  "$(run resume "$(fx resume-source)" 0 "$(fx resume-target)" 0 completed x)"
expect "a source that did not fail is refused" 1 \
  "source state='Completed', expected 'Failed'" \
  "$(run resume "$(fx configured-success)" 2 "$(fx resume-target)" 0 completed x)"
expect "a source whose ingest stage did not fail is refused, though the run Failed" 1 \
  "source ingest stage did not fail" \
  "$(run resume "$(derive source-ingest-ok resume-source 's/"stageId":"ingest","state":"Failed"/"stageId":"ingest","state":"Completed"/')" 2 "$(fx resume-target)" 0 completed x)"
expect "a source whose ingest never ran (attempt 0) is refused" 1 \
  "source ingest attempt=0, expected an integer >= 1" \
  "$(run resume "$(derive source-attempt-0 resume-source 's/"stageId":"ingest","state":"Failed","attempt":3/"stageId":"ingest","state":"Failed","attempt":0/')" 2 "$(fx resume-target)" 0 completed x)"

echo "== usage =="
expect "no arguments is a usage error, not a pass" 2 "pipeline_gate_assertions.py fail-closed" "$(run)"
expect "a wrong argument count is a usage error" 2 "usage error" "$(run fail-closed "$(fx unconfigured)" 2)"
expect "an unknown resume mode is refused" 1 \
  "is not 'completed' or 'failed'" \
  "$(run resume "$(fx resume-source)" 2 "$(fx resume-target)" 0 done x)"

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
