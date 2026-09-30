#!/usr/bin/env bash
# Tests for scripts/ci/zero-test-guard.sh, driven by TRX files VSTest actually wrote.
#
# WHY A TEST AND NOT JUST THE GUARD. A guard that cannot fail reads exactly like one that has
# nothing to find: both print nothing alarming and exit 0. Every branch below is driven with an
# input that must make it fire, and the passing cases are there so that "it always fails" cannot
# masquerade as "it fails when it should".
#
# The fixtures in tests/scripts/fixtures/zero-test-guard/ were written by VSTest in the devtest
# container (Microsoft.NET.Test.Sdk 18.9.0, xunit.runner.visualstudio 4.0.0), not typed by hand:
#   real-pass.trx.xml     compat-gate Tier A's repaired first step: 1 test, executed and passed
#   mixed-skip.trx.xml    4 passed + 1 [Fact(Skip = ...)]: total=5 executed=4
#   all-skipped.trx.xml   exactly one [Fact(Skip = ...)]: total=1 executed=0 notExecuted=0
#   no-match.trx.xml      a filter matching nothing: total=0 executed=0 plus VSTest's RunInfo line
#   multi-source.trx.xml  ONE run over two assemblies, the filter matching in one of them only:
#                         executed>0 AND a RunInfo "No test matches ... in <the other dll>"
#   console-nomatch.txt   the console output of security-gate Tier B as master ran it (-f net8.0)
# They end in .trx.xml because .gitignore ignores *.trx. The one edit made to them: a test
# dependency's license banner, which it prints to test stdout, was cut from the <StdOut> of real-pass
# and multi-source; every element the guard reads is as VSTest wrote it.
#
# The all-zero TRX WITHOUT the RunInfo line is derived from no-match.trx.xml below, so the
# executed==0 branch is exercised on its own rather than only through the RunInfo check in front of it.
#
# Run:  bash tests/scripts/zero-test-guard.test.sh
# Pure bash: no python, no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GUARD="${ROOT}/scripts/ci/zero-test-guard.sh"
FX="${ROOT}/tests/scripts/fixtures/zero-test-guard"

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=20

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

# Non-vacuity control. A missing guard makes `bash <missing>` exit 127 and every "must fail" case
# below would report ok for the wrong reason; a missing fixture reads as a missing TRX, which the
# guard also refuses, so the "must fail" cases would again pass without testing what they name.
if [[ ! -f "${GUARD}" ]]; then
  echo "FAIL - ${GUARD} does not exist; nothing here can be tested."
  exit 1
fi
for f in real-pass.trx.xml mixed-skip.trx.xml all-skipped.trx.xml no-match.trx.xml multi-source.trx.xml console-nomatch.txt; do
  if [[ ! -s "${FX}/${f}" ]]; then
    echo "FAIL - fixture ${FX}/${f} is missing or empty; the cases that use it would test nothing."
    exit 1
  fi
done

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

# Echoes "<exit>|<output>".
run() {
  local out code
  out="$(bash "${GUARD}" "$@" 2>&1)"; code=$?
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

echo "== a selection that ran passes, and says how much ran =="
expect "a real TRX with one executed test passes" 0 \
  "executed=1 total=1 skipped=0" "$(run "${FX}/real-pass.trx.xml")"
expect "a partly skipped selection passes and reports the skip" 0 \
  "executed=4 total=5 skipped=1" "$(run "${FX}/mixed-skip.trx.xml")"

# The no-match check is anchored on <Text>, where VSTest writes its own RunInfo line. A test that
# merely prints the phrase lands in <StdOut> (XML-escaped, so it can never forge a <Text>); it ran,
# and failing it would be a false red. Derived from the real TRX; the arrange step proves the phrase
# is in the file and the run still executed, so a pass here is the anchor's doing.
sed 's|<StdOut>|<StdOut>No test matches the given testcase filter `FullyQualifiedName~Printed` in /x/Printed.dll |' \
  "${FX}/real-pass.trx.xml" > "${TMP}/stdout-phrase.trx"
if grep -q '<StdOut>No test matches the given testcase filter' "${TMP}/stdout-phrase.trx" \
   && grep -qE '<Counters [^>]*[[:space:]]executed="1"' "${TMP}/stdout-phrase.trx"; then
  expect "a TRX that ran a test which printed VSTest's no-match phrase passes" 0 \
    "executed=1 total=1" "$(run "${TMP}/stdout-phrase.trx")"
else
  bad "a TRX that ran a test which printed VSTest's no-match phrase passes" \
    "arrange failed: the derived file does not carry the phrase in <StdOut>, or lost executed=1"
fi

# .gitattributes pins *.sh/*.py/Makefile to LF but not these fixtures, so a Windows checkout hands
# the guard CRLF. The arrange step asserts the conversion took effect before the act.
tr -d '\r' < "${FX}/real-pass.trx.xml" | awk '{ printf "%s\r\n", $0 }' > "${TMP}/crlf.trx"
if [[ "$(tr -cd '\r' < "${TMP}/crlf.trx" | wc -c)" -gt 0 ]]; then
  expect "the same TRX with CRLF line endings passes" 0 "executed=1 total=1" "$(run "${TMP}/crlf.trx")"
else
  bad "the same TRX with CRLF line endings passes" "arrange failed: the copy carries no CR"
fi

echo "== a selection that ran nothing fails, whichever way it shows =="
expect "no-match TRX (executed=0 and VSTest's RunInfo line) fails" 1 \
  "VSTest reports the filter selected nothing" "$(run "${FX}/no-match.trx.xml")"

sed '/<RunInfos>/,/<\/RunInfos>/d' "${FX}/no-match.trx.xml" > "${TMP}/all-zero.trx"
if grep -q 'No test matches' "${TMP}/all-zero.trx" || ! grep -q 'executed="0"' "${TMP}/all-zero.trx"; then
  bad "an all-zero TRX with no RunInfo fails on executed=0 alone" \
    "arrange failed: the derived file still carries the RunInfo, or lost its counters"
else
  expect "an all-zero TRX with no RunInfo fails on executed=0 alone" 1 \
    "the filter selected no test (executed=0, total=0)" "$(run "${TMP}/all-zero.trx")"
fi

expect "an all-skipped selection fails: skips are not executions" 1 \
  "all 1 selected test(s) were skipped" "$(run "${FX}/all-skipped.trx.xml")"
# Only meaningful if the fixture really did execute tests: otherwise executed==0 would fail it and the
# RunInfo check would never have been the reason.
if grep -qE '<Counters [^>]*[[:space:]]executed="[1-9]' "${FX}/multi-source.trx.xml"; then
  expect "a TRX that ran tests but says one source matched nothing fails" 1 \
    "VSTest reports the filter selected nothing" "$(run "${FX}/multi-source.trx.xml")"
else
  bad "a TRX that ran tests but says one source matched nothing fails" \
    "arrange failed: the multi-source fixture executed nothing, so it cannot isolate the RunInfo check"
fi
expect "a missing TRX fails: no file is not a pass" 1 \
  "no TRX was written" "$(run "${TMP}/never-written.trx")"
expect "a console log handed over as the TRX fails: no counters, no pass" 1 \
  "cannot read total/executed" "$(run "${FX}/console-nomatch.txt")"

# The fixture itself must carry the line the guard would otherwise have had to find in a console log
# - that is the claim that lets the guard read the TRX alone.
if grep -q '<Text>No test matches the given testcase filter' "${FX}/no-match.trx.xml" \
   && grep -q '^No test matches the given testcase filter' "${FX}/console-nomatch.txt"; then
  ok "VSTest writes the no-match line into the TRX as well as the console"
else
  bad "VSTest writes the no-match line into the TRX as well as the console" \
    "one of the two fixtures no longer carries it"
fi

sed 's/executed="[0-9]*"/executed=""/' "${FX}/real-pass.trx.xml" > "${TMP}/no-executed.trx"
expect "counters whose executed value cannot be read fail" 1 \
  "cannot read total/executed" "$(run "${TMP}/no-executed.trx")"

echo "== every TRX named must pass on its own =="
got="$(run "${FX}/real-pass.trx.xml" "${FX}/no-match.trx.xml")"
expect "one good and one empty TRX fail together" 1 "FAIL ${FX}/no-match.trx.xml" "${got}"
expect "...while still reporting the good one" 1 "ok ${FX}/real-pass.trx.xml" "${got}"
expect "a missing second TRX fails the pair" 1 \
  "no TRX was written" "$(run "${FX}/real-pass.trx.xml" "${TMP}/never-written.trx")"

echo "== --solution: one invocation over many projects =="
expect "--solution passes when at least one project executed a test" 0 \
  "executed=1 total=1" "$(run --solution "${FX}/real-pass.trx.xml" "${FX}/no-match.trx.xml")"
expect "--solution fails when no project executed a test" 1 \
  "no project in the solution-wide run executed a test" \
  "$(run --solution "${FX}/no-match.trx.xml" "${FX}/all-skipped.trx.xml")"
expect "--solution still fails on a missing TRX" 1 \
  "no TRX was written" "$(run --solution "${FX}/real-pass.trx.xml" "${TMP}/never-written.trx")"
expect "--solution over an unexpanded glob (nothing written) fails" 1 \
  "no TRX was written" "$(run --solution "${TMP}/*__perf-certification_*.trx")"

echo "== usage =="
expect "no arguments is a usage error, not a pass" 2 "usage:" "$(run)"

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
