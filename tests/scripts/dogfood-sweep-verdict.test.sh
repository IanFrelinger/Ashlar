#!/usr/bin/env bash
# Tests for classify_sweep_log / sweep_failure_reason in scripts/dogfood-continuous-proof.sh.
#
# WHY THIS EXISTS. On 2026-09-14 the dogfood workflow wrote **PASS** to a ledger that gates an
# autonomy marketing claim, for a sweep in which nothing ran: the sandbox image was absent, the
# single objective died at `docker run` with "No such image", AutonomyLoopService caught it and
# kept sweeping, SweepMode reported "attempted 1 objective(s)" and exited 0, and the workflow
# mapped exit 0 to PASS. Run 34889059104. The row never reached git only because the workflow
# cannot push to a protected branch.
#
# So the verdict logic is the part of this script that most needs proving and was, until it was
# split out, the part that could not be run at all without dotnet, a container engine and an SDK
# image. It is now a pair of pure functions, and case 3 below replays that exact log.
#
# Run:  bash tests/scripts/dogfood-sweep-verdict.test.sh
# Pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/dogfood-continuous-proof.sh"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Probe in a SUBSHELL first. If the dispatch guard at the foot of that script is ever removed,
# sourcing falls through to its usage branch, and that branch's `exit 1` terminates whatever shell
# is running it — so sourcing directly would kill this file before it could print why. In a
# subshell the exit is contained and the failure can be reported.
# shellcheck disable=SC1090
if ! ( source "${SCRIPT}" >/dev/null 2>&1 && declare -F classify_sweep_log >/dev/null 2>&1 ); then
  echo "FAIL — ${SCRIPT} is not sourceable, or no longer defines classify_sweep_log."
  echo "       Is the BASH_SOURCE dispatch guard still at the foot of that script?"
  exit 1
fi

# shellcheck disable=SC1090
source "${SCRIPT}" >/dev/null 2>&1 || true

# The script sets `-euo pipefail`, and sourcing applies that to THIS shell. Under -e the first
# assertion whose command returns nonzero — which is most of them, since they are testing for
# nonzero — aborts the run mid-file. That is not hypothetical: it happened while this file was
# being written. The run stopped partway through having printed nothing but passes; the exit code
# was nonzero so CI would have caught it, but nothing on screen said "truncated". Hence the
# assertion-count check at the foot of this file.
set +e

if ! declare -F classify_sweep_log >/dev/null; then
  echo "FAIL — classify_sweep_log is not defined after sourcing ${SCRIPT}"
  echo "       (is the BASH_SOURCE dispatch guard still at the foot of that script?)"
  exit 1
fi

# A test file that stops early must fail LOUDLY rather than just exit nonzero, for the same reason
# the sweep must not report a fault as a pass. Bump this when assertions are added.
EXPECTED_ASSERTIONS=9

mklog() { local f; f="$(mktemp)"; printf '%s\n' "$1" > "${f}"; printf '%s' "${f}"; }

verdict_of() {
  local rc=0
  classify_sweep_log "$1" "$2" || rc=$?
  printf '%s' "${rc}"
}

echo "== classify_sweep_log =="

# 1. The happy path: the loop attempted an objective and the iteration reached a verdict.
L="$(mklog 'SWEEP: attempted 1 objective(s) in 41.2s
SWEEP: complete (see the iteration outcome logged above)')"
v="$(verdict_of 0 "${L}")"
[[ "${v}" == "0" ]] && ok "completed iteration -> 0 (PASS)" || bad "completed iteration -> 0" "got ${v}"
rm -f "${L}"

# 2. Nothing attempted: SweepMode already exits nonzero, and that stays a refusal.
L="$(mklog 'SWEEP: no objective was eligible — check for a witness and proposal beside each one')"
v="$(verdict_of 1 "${L}")"
[[ "${v}" == "1" ]] && ok "nothing attempted -> 1 (GAP)" || bad "nothing attempted -> 1" "got ${v}"
rm -f "${L}"

# 3. THE REGRESSION. Verbatim shape of run 34889059104: exit 0, "attempted 1", and an iteration
# that never started because the sandbox image was missing. This MUST NOT be 0.
REAL_LOG='warn: Ashlar.BackgroundAgents.Autonomy.AutonomyLoopService[0] Objective rgb-hex-parse failed (/home/runner/work/Ashlar/Ashlar/.ashlar/runtime-studio/objectives/pending/rgb-hex-parse.md); continuing the sweep System.InvalidOperationException: Sandbox session '"'"'ashlar-session-a208107fb7b2444f8260b15420ed7523'"'"' failed to start (exit 125): docker: Error response from daemon: No such image: mcr.microsoft.com/dotnet/sdk:10.0  Run '"'"'docker run --help'"'"' for more information     at Ashlar.Infrastructure.Execution.Sandbox.DockerSandboxedSessionRunner.StartAsync(SandboxSpec spec, CancellationToken cancellationToken) in /_/src/Ashlar.Infrastructure/Execution/Sandbox/DockerSandboxedSessionRunner.cs:line 82    at Ashlar.BackgroundAgents.Autonomy.AutonomyLoopService.SweepAsync(CancellationToken cancellationToken) in /_/src/Ashlar.BackgroundAgents/Autonomy/AutonomyLoopService.cs:line 254
SWEEP: attempted 1 objective(s) in 0.2s
SWEEP: complete (see the iteration outcome logged above)'
L="$(mklog "${REAL_LOG}")"
v="$(verdict_of 0 "${L}")"
if [[ "${v}" == "2" ]]; then
  ok "run 34889059104's log -> 2 (GAP), not 0"
else
  bad "run 34889059104's log -> 2 (GAP), not 0" "got ${v} — a failed sweep would be recorded as a pass again"
fi

echo "== sweep_failure_reason =="

why="$(sweep_failure_reason "${L}")"
grep -q 'No such image' <<<"${why}" \
  && ok "names the actual cause" || bad "names the actual cause" "got '${why}'"
grep -q 'InvalidOperationException' <<<"${why}" \
  && ok "keeps the exception type" || bad "keeps the exception type" "got '${why}'"
# A ledger cell is read by a human. Stack frames belong in the attached log, not in the table.
if grep -q '   at Ashlar' <<<"${why}"; then
  bad "strips the stack frames" "stack trace leaked into the ledger reason: '${why}'"
else
  ok "strips the stack frames"
fi
rm -f "${L}"

# The cap needs a reason LONGER than the cap to mean anything. Asserting it against the run
# 34889059104 message would be vacuous — that one is well under 400 characters, so deleting the
# cap entirely leaves the assertion green. Exceptions from a build or test failure inside the
# sandbox carry compiler output and run much longer than this.
LONG="$(printf 'x%.0s' $(seq 1 900))"
L="$(mklog "warn: AutonomyLoopService[0] Objective rgb-hex-parse failed (/x.md); continuing the sweep System.Exception: ${LONG}")"
why="$(sweep_failure_reason "${L}")"
if [[ "${#why}" -eq 400 ]]; then
  ok "caps an over-long reason at 400 chars"
else
  bad "caps an over-long reason at 400 chars" "got ${#why} from a reason of ~900"
fi
rm -f "${L}"

# A clean log has no reason to report.
L="$(mklog 'SWEEP: attempted 1 objective(s) in 41.2s')"
why="$(sweep_failure_reason "${L}")"
[[ -z "${why}" ]] && ok "empty on a clean log" || bad "empty on a clean log" "got '${why}'"
rm -f "${L}"

echo "== the marker this depends on still exists in the source =="
# classify_sweep_log reads a log template owned by another file. If that template is reworded the
# tests above keep passing against their own fixtures while production silently returns to
# reporting failures as passes — so assert the real template too.
#
# Match the WHOLE template including its placeholders, not the "; continuing the sweep" fragment.
# The fragment appears in that file's explanatory comment as well as in the log call, so a grep
# for it matches the comment and survives any rewording of the call — which is precisely what it
# did when this assertion was first written: the template was renamed to "; moving on" and this
# stayed green. Only the full literal below is unique to the LogWarning.
SRC="${ROOT}/src/Ashlar.BackgroundAgents/Autonomy/AutonomyLoopService.cs"
TEMPLATE='"Objective {Id} failed ({Path}); continuing the sweep"'
if grep -qF "${TEMPLATE}" "${SRC}"; then
  ok "AutonomyLoopService still logs the exact template classify_sweep_log parses"
else
  bad "AutonomyLoopService still logs the exact template classify_sweep_log parses" \
      "${TEMPLATE} is gone from ${SRC}; failed sweeps will be recorded as passes again"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL — ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early (see the set +e note at the top) or assertions were"
  echo "       added without bumping EXPECTED_ASSERTIONS. A partial run is not a pass."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
