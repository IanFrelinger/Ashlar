#!/usr/bin/env bash
# Fail closed when a pinned `dotnet test --filter` ran nothing.
#
# WHY. VSTest exits 0 when a --filter matches no test: it prints "No test matches the given testcase
# filter", writes a TRX with total="0" executed="0", and the lane is green having asserted nothing.
# Measured on master at 876cc527 in the devtest container: security-gate Tier B selected ZERO tests
# (its API classes compile on net10.0 only and it ran net8.0), compat-gate Tier A's first step
# selected ZERO (its class moved to another project), and application-gate Tier C selected ZERO (same
# net8.0/net10.0 split). Each printed its own "PASS". scripts/cert-gate-zero-test-guard.sh closed this
# for cert-gate alone; this is the same fail-closed check for every other pinned filter, so a filter
# that goes stale turns its lane red instead of quiet.
#
# USAGE
#   bash scripts/ci/zero-test-guard.sh <trx> [<trx> ...]
#       Each TRX is judged on its own, and every one of them must pass. Give each test invocation its
#       own LogFileName: a multi-target invocation (no -f) that reuses one name OVERWRITES the first
#       target's TRX with the second's ("WARNING: Overwriting results file"), so a target that
#       matched nothing is invisible in the file that survives. Measured: the security-gate Tier B
#       filter run without -f left a TRX saying 44 executed while its net8.0 leg had matched nothing.
#       Pin -f per invocation, one TRX each.
#
#   bash scripts/ci/zero-test-guard.sh --solution <trx> [<trx> ...]
#       For ONE invocation over a solution or solution filter, where most projects legitimately match
#       nothing and each writes its own TRX (use LogFilePrefix, not LogFileName). Passes when at least
#       one TRX executed a test; still fails on a missing or unreadable TRX, or when none executed.
#
# A TRX FAILS when
#   - it does not exist: the run wrote nothing, or wrote it somewhere else. A count that cannot be
#     read is not zero and it is not a pass, the same rule cert-gate-zero-test-guard.sh applies when
#     it cannot derive its expected count;
#   - its <Counters> element, or the total/executed attributes on it, cannot be read;
#   - executed == 0;
#   - a RunInfo carries VSTest's own no-selection line ("No test matches the given testcase filter"
#     or "No test is available"). Anchored on <Text> so a test that merely prints the phrase to
#     stdout does not trip it.
#
# SKIPPED TESTS COUNT AS NOT RUN. In a TRX written by VSTest 18.x a skipped test is counted in
# `total` and NOT in `executed`, and `notExecuted` stays 0 - measured: a filter selecting exactly one
# [Fact(Skip = ...)] wrote total="1" executed="0" notExecuted="0". So skips are total - executed, and
# this guard counts executed only:
#   - a selection that is ENTIRELY skipped fails. It asserted exactly as much as a selection of
#     nothing, and "every test I pinned is skipped" is how a pinned lane goes quiet without its filter
#     changing at all;
#   - a selection that is PARTLY skipped passes and prints the skip count. Several pinned lanes select
#     environment-gated tests that skip on a runner without Docker or a model; failing on any skip
#     would make those lanes red by construction rather than by defect.
# This guard answers "did the filter select anything that ran", not "did everything it selected run".
# It is not a floor: a lane that shrinks from 40 tests to 1 still passes here.
#
# Honour the test step's own exit code by calling this AFTER it, under `set -e` (or in a later
# command of the same Make recipe): a failing test run exits before this is reached, and this can
# only ever add a failure, never mask one.
set -euo pipefail

usage() {
  echo "usage: zero-test-guard.sh [--solution] <trx> [<trx> ...]" >&2
  exit 2
}

MODE="each"
if [[ "${1:-}" == "--solution" ]]; then
  MODE="solution"
  shift
fi
[[ $# -ge 1 ]] || usage

FAILED=0
ANY_EXECUTED=0

fail() {
  echo "zero-test-guard: FAIL ${1}: ${2}"
  FAILED=1
}

attr() {
  # Prints the numeric value of attribute $1 in the element text $2, or nothing.
  printf '%s' "$2" | sed -n "s/.*[[:space:]]${1}=\"\([0-9][0-9]*\)\".*/\1/p"
}

for trx in "$@"; do
  if [[ ! -f "${trx}" ]]; then
    fail "${trx}" "no TRX was written. Nothing proves the filter selected a test."
    continue
  fi

  counters="$(grep -o '<Counters [^>]*>' "${trx}" | head -n 1 || true)"
  total="$(attr total "${counters}")"
  executed="$(attr executed "${counters}")"
  if [[ -z "${counters}" || -z "${total}" || -z "${executed}" ]]; then
    fail "${trx}" "cannot read total/executed from its <Counters> element; a count that cannot be read is not a pass."
    continue
  fi
  skipped=$((total - executed))

  no_selection=0
  if grep -qE '<Text>(No test matches the given testcase filter|No test is available)' "${trx}"; then
    no_selection=1
  fi

  if [[ "${executed}" -gt 0 ]]; then
    ANY_EXECUTED=1
  fi

  if [[ "${MODE}" == "solution" ]]; then
    echo "zero-test-guard: ${trx}: executed=${executed} total=${total} skipped=${skipped}"
    continue
  fi

  if [[ "${no_selection}" -eq 1 ]]; then
    fail "${trx}" "VSTest reports the filter selected nothing (executed=${executed}, total=${total})."
  elif [[ "${executed}" -eq 0 && "${total}" -gt 0 ]]; then
    fail "${trx}" "all ${total} selected test(s) were skipped, so the lane asserted nothing."
  elif [[ "${executed}" -eq 0 ]]; then
    fail "${trx}" "the filter selected no test (executed=0, total=0)."
  else
    echo "zero-test-guard: ok ${trx}: executed=${executed} total=${total} skipped=${skipped}"
  fi
done

if [[ "${MODE}" == "solution" && "${ANY_EXECUTED}" -eq 0 && "${FAILED}" -eq 0 ]]; then
  echo "zero-test-guard: FAIL: no project in the solution-wide run executed a test."
  FAILED=1
fi

if [[ "${FAILED}" -ne 0 ]]; then
  echo "zero-test-guard: a pinned filter that selects nothing passes green and proves nothing; fix the"
  echo "                 filter, its target framework or its project - do not delete the guard."
  exit 1
fi
