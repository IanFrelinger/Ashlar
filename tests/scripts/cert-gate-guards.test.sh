#!/usr/bin/env bash
# Tests the cert-gate guards: scripts/cert-gate-skip-guard.sh, scripts/cert-gate-zero-test-guard.sh,
# scripts/cert-gate-summary.sh, and their wiring in scripts/run-cert-gate.sh.
#
# WHY. cert-gate is a REQUIRED check, and `dotnet test` exits 0 both when its filter matches nothing
# and when tests are SKIPPED. The zero-test guard floors the TRX `total` against --list-tests, and
# both of those count a skipped test, so a [Fact(Skip = "...")] on a merge-blocking test used to
# leave cert-gate green (measured 2026-09-30: exit 0 with total=1288 executed=1287). The skip guard
# pins `total - executed` to scripts/cert-gate-skipped.baseline in both directions. A guard is only
# evidence once each of its branches has been seen firing, so this drives the REAL scripts - copied
# into a sandbox repo root, not restated - through fixture TRX files shaped like the real one
# (including its `notExecuted="0"` beside a skipped result), with a stand-in `dotnet` on PATH that
# exits 0 on a skip exactly as the real one does.
#
# Run:  bash tests/scripts/cert-gate-guards.test.sh
# Pure bash: no network, no dotnet, no container, no python.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=29

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

SANDBOX="${TMP}/repo"
BIN="${TMP}/bin"
OUT="${TMP}/out.txt"
SUMMARY_FILE="${TMP}/summary.md"
mkdir -p "${SANDBOX}/scripts" "${BIN}"

UNDER_TEST=(
  run-cert-gate.sh
  cert-gate-config.sh
  cert-gate-zero-test-guard.sh
  cert-gate-skip-guard.sh
  cert-gate-summary.sh
  cert-gate-skipped.baseline
)

finish() {
  echo
  echo "passed: ${PASS}   failed: ${FAIL}"
  local ran=$((PASS + FAIL))
  if [[ "${ran}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
    echo "FAIL - ran ${ran} assertions, expected ${EXPECTED_ASSERTIONS}."
    echo "       Either this file stopped early or assertions were added without bumping"
    echo "       EXPECTED_ASSERTIONS. A partial run is not a pass."
    exit 1
  fi
  [[ "${FAIL}" -eq 0 ]] || exit 1
  exit 0
}

# ---------------------------------------------------------------------------------------------
# Non-vacuity controls. Without the real files every case below would run nothing, and the
# "must fail" cases would pass on "file not found".
# ---------------------------------------------------------------------------------------------
echo "== controls =="
missing=()
for f in "${UNDER_TEST[@]}"; do
  if [[ -f "${ROOT}/scripts/${f}" ]]; then
    cp "${ROOT}/scripts/${f}" "${SANDBOX}/scripts/${f}"
  else
    missing+=("scripts/${f}")
  fi
done
if [[ "${#missing[@]}" -ne 0 ]]; then
  bad "the cert-gate scripts and baseline under test exist" "missing: ${missing[*]}"
  echo "Cannot test files that are not there; stopping."
  exit 1
fi
ok "the cert-gate scripts and baseline under test exist and are copied into a sandbox root"

# Stand-in for dotnet. restore/build succeed; `test --list-tests` lists FAKE_LIST_COUNT tests; any
# other `test` copies FAKE_TRX (when set) to <--results-directory>/cert-gate.trx and exits
# FAKE_TEST_EXIT (default 0 - what the real dotnet test returns when tests are only skipped).
cat > "${BIN}/dotnet" <<'FAKE'
#!/usr/bin/env bash
case "${1:-}" in
  restore|build) exit 0 ;;
  test)
    for a in "$@"; do
      if [[ "${a}" == "--list-tests" ]]; then
        echo "The following Tests are available:"
        for ((i = 1; i <= ${FAKE_LIST_COUNT:-0}; i++)); do echo "    Ashlar.Tests.Fake.Listed.Test${i}"; done
        exit 0
      fi
    done
    dir="" prev=""
    for a in "$@"; do
      if [[ "${prev}" == "--results-directory" ]]; then dir="${a}"; fi
      prev="${a}"
    done
    if [[ -n "${FAKE_TRX:-}" && -n "${dir}" ]]; then cp "${FAKE_TRX}" "${dir}/cert-gate.trx"; fi
    exit "${FAKE_TEST_EXIT:-0}"
    ;;
esac
echo "fake dotnet: unexpected invocation: $*" >&2
exit 97
FAKE
chmod +x "${BIN}/dotnet"

# sandbox <VAR=value ...> <command...>: run with the fake dotnet first on PATH and the step
# summary sent to a scratch file (never the real one when this runs inside a CI job). Output goes
# to ${OUT}; the exit code is returned.
sandbox() {
  env PATH="${BIN}:${PATH}" GITHUB_STEP_SUMMARY="${SUMMARY_FILE}" "$@" > "${OUT}" 2>&1
}

resolved="$(env PATH="${BIN}:${PATH}" bash -c 'command -v dotnet')"
if [[ "${resolved}" == "${BIN}/dotnet" ]]; then
  ok "the sandbox resolves dotnet to the stand-in, not a real SDK"
else
  bad "the sandbox resolves dotnet to the stand-in, not a real SDK" "resolved: ${resolved:-<nothing>}"
  echo "Every case below would run a real dotnet; stopping."
  finish
fi

# make_trx <file> <passed> <skipped> [<text a test printed>]
# Shaped like the TRX a real cert-gate run writes: a UTF-8 BOM, results BEFORE the summary, a
# skipped result as outcome="NotExecuted", and - as measured on 2026-09-30 - executed excluding the
# skip while notExecuted still reads 0.
make_trx() {
  local file="$1" passed="$2" skipped="$3" noise="${4:-}"
  local total=$((passed + skipped)) i
  {
    printf '\xEF\xBB\xBF<?xml version="1.0" encoding="utf-8"?>\n'
    echo '<TestRun id="00000000-0000-0000-0000-000000000001" name="fixture" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
    echo '  <Results>'
    for ((i = 1; i <= passed; i++)); do
      echo "    <UnitTestResult executionId=\"p-${i}\" testId=\"tp-${i}\" testName=\"Ashlar.Tests.Infrastructure.Tests.Certification.FixturePassingTests.Fact_${i}\" computerName=\"fixture\" duration=\"00:00:00.0010000\" testType=\"13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b\" outcome=\"Passed\" testListId=\"8c84fa94-04c1-424b-9868-57a2d4851a1d\" relativeResultsDirectory=\"p-${i}\">"
      if [[ "${i}" -eq 1 && -n "${noise}" ]]; then
        echo "      <Output><StdOut>${noise}</StdOut></Output>"
      fi
      echo "    </UnitTestResult>"
    done
    for ((i = 1; i <= skipped; i++)); do
      echo "    <UnitTestResult executionId=\"s-${i}\" testId=\"ts-${i}\" testName=\"Ashlar.Tests.Infrastructure.Tests.Certification.FixtureSkippedTests.Skipped_fact_${i}\" computerName=\"fixture\" duration=\"00:00:00\" testType=\"13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b\" outcome=\"NotExecuted\" testListId=\"8c84fa94-04c1-424b-9868-57a2d4851a1d\" relativeResultsDirectory=\"s-${i}\">"
      echo "      <Output><StdOut>mutation: cert-skip audit</StdOut></Output>"
      echo "    </UnitTestResult>"
    done
    echo '  </Results>'
    echo '  <ResultSummary outcome="Completed">'
    echo "    <Counters total=\"${total}\" executed=\"${passed}\" passed=\"${passed}\" failed=\"0\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" notExecuted=\"0\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\" />"
    echo '  </ResultSummary>'
    echo '</TestRun>'
  } > "${file}"
}

set_baseline() { printf '%b' "$1" > "${SANDBOX}/scripts/cert-gate-skipped.baseline"; }
restore_committed_baseline() { cp "${ROOT}/scripts/cert-gate-skipped.baseline" "${SANDBOX}/scripts/cert-gate-skipped.baseline"; }

SKIP_GUARD="${SANDBOX}/scripts/cert-gate-skip-guard.sh"
ZERO_GUARD="${SANDBOX}/scripts/cert-gate-zero-test-guard.sh"
SUMMARY_SH="${SANDBOX}/scripts/cert-gate-summary.sh"
RUN_GATE="${SANDBOX}/scripts/run-cert-gate.sh"

# expect_rc <label> <want: 0|nonzero> <got>
expect_rc() {
  local label="$1" want="$2" got="$3"
  if [[ "${want}" == "0" && "${got}" -eq 0 ]] || [[ "${want}" == "nonzero" && "${got}" -ne 0 ]]; then
    ok "${label}"
  else
    bad "${label}" "wanted exit ${want}, got ${got}. Output: $(head -c 600 "${OUT}" | tr '\n' ' ')"
  fi
}

# expect_out <label> <fixed string the last output must contain>
expect_out() {
  local label="$1" needle="$2"
  if grep -qF -- "${needle}" "${OUT}"; then
    ok "${label}"
  else
    bad "${label}" "output lacks '${needle}': $(head -c 600 "${OUT}" | tr '\n' ' ')"
  fi
}

# The committed baseline must be a value the guard can read. It is read, never restated here, so
# changing it deliberately does not break this file.
restore_committed_baseline
COMMITTED="$(tr -d '\r' < "${SANDBOX}/scripts/cert-gate-skipped.baseline" | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' | grep -vE '^(#|$)' || true)"
if [[ "${COMMITTED}" =~ ^[0-9]+$ ]]; then
  make_trx "${TMP}/committed.trx" 5 "${COMMITTED}"
  sandbox bash "${SKIP_GUARD}" "${TMP}/committed.trx"; rc=$?
  expect_rc "the committed baseline (${COMMITTED}) accepts a run skipping exactly that many" 0 "${rc}"
  make_trx "${TMP}/committed-plus-one.trx" 5 "$((COMMITTED + 1))"
  sandbox bash "${SKIP_GUARD}" "${TMP}/committed-plus-one.trx"; rc=$?
  expect_rc "the committed baseline refuses one skip more than it records" nonzero "${rc}"
else
  bad "the committed baseline holds one integer" "read: '${COMMITTED}'"
  bad "the committed baseline refuses one skip more than it records" "not reached: baseline unreadable"
fi

# ---------------------------------------------------------------------------------------------
echo "== skip guard: a skip is a recorded decision, in both directions =="
make_trx "${TMP}/clean.trx" 5 0
make_trx "${TMP}/one-skip.trx" 4 1

set_baseline '# comment\n0\n'
sandbox bash "${SKIP_GUARD}" "${TMP}/clean.trx"; rc=$?
expect_rc "no skips against a baseline of 0 passes" 0 "${rc}"

sandbox bash "${SKIP_GUARD}" "${TMP}/one-skip.trx"; rc=$?
expect_rc "one skip against a baseline of 0 fails, although dotnet test would exit 0" nonzero "${rc}"
expect_out "the failure names the skipped test" "FixtureSkippedTests.Skipped_fact_1"

set_baseline '1\n'
sandbox bash "${SKIP_GUARD}" "${TMP}/clean.trx"; rc=$?
expect_rc "no skips against a baseline of 1 fails (slack is refused)" nonzero "${rc}"
expect_out "that failure says to lower the baseline" "Lower scripts/cert-gate-skipped.baseline to 0"

sandbox bash "${SKIP_GUARD}" "${TMP}/one-skip.trx"; rc=$?
expect_rc "one skip against a baseline of 1 passes (the recorded decision)" 0 "${rc}"

set_baseline '0\r\n'
sandbox bash "${SKIP_GUARD}" "${TMP}/clean.trx"; rc=$?
expect_rc "a CRLF baseline, as a Windows checkout writes it, still reads as 0" 0 "${rc}"

echo "== skip guard: fails closed =="
set_baseline '0\n'
sandbox bash "${SKIP_GUARD}" "${TMP}/does-not-exist.trx"; rc=$?
expect_rc "a missing TRX fails" nonzero "${rc}"

grep -v '<Counters ' "${TMP}/clean.trx" > "${TMP}/no-counters.trx"
sandbox bash "${SKIP_GUARD}" "${TMP}/no-counters.trx"; rc=$?
expect_rc "a TRX with no <Counters> element fails" nonzero "${rc}"

rm -f "${SANDBOX}/scripts/cert-gate-skipped.baseline"
sandbox bash "${SKIP_GUARD}" "${TMP}/clean.trx"; rc=$?
expect_rc "a missing baseline file fails" nonzero "${rc}"

set_baseline '0\n1\n'
sandbox bash "${SKIP_GUARD}" "${TMP}/clean.trx"; rc=$?
expect_rc "a baseline holding two numbers fails" nonzero "${rc}"

# Results precede the summary in a TRX, and a test's printed output lands inside them. XML escapes
# '<' in text, so an escaped lookalike element must not be read as the counters.
set_baseline '0\n'
make_trx "${TMP}/noisy-skip.trx" 4 1 '&lt;Counters total="5" executed="5" /&gt; total="5" executed="5"'
sandbox bash "${SKIP_GUARD}" "${TMP}/noisy-skip.trx"; rc=$?
expect_rc "counters a test printed before the summary cannot hide a real skip" nonzero "${rc}"

# The reader needs whitespace before an attribute name, so a counter is read only as a whole
# attribute. No TRX attribute ends in a counter's name today; this pins that defence so it is not
# an untested line. Without it the read below finds two totals and fails closed.
sed 's/<Counters /<Counters subtotal="999" /' "${TMP}/clean.trx" > "${TMP}/lookalike-attr.trx"
if grep -qF '<Counters subtotal="999" total="5"' "${TMP}/lookalike-attr.trx"; then
  sandbox bash "${SKIP_GUARD}" "${TMP}/lookalike-attr.trx"; rc=$?
  expect_rc "an attribute whose name ends in a counter's name is not read as that counter" 0 "${rc}"
else
  bad "an attribute whose name ends in a counter's name is not read as that counter" \
    "the fixture did not get the lookalike attribute, so nothing was tested"
fi

# ---------------------------------------------------------------------------------------------
echo "== zero-test guard: the floor still fails closed =="
make_trx "${TMP}/three.trx" 3 0
sandbox FAKE_LIST_COUNT=0 bash "${ZERO_GUARD}" "${TMP}/three.trx"; rc=$?
expect_rc "no tests listed (a dead probe) fails rather than passing" nonzero "${rc}"

sandbox FAKE_LIST_COUNT=5 bash "${ZERO_GUARD}" "${TMP}/three.trx"; rc=$?
expect_rc "fewer tests reported than listed fails" nonzero "${rc}"

sandbox FAKE_LIST_COUNT=3 bash "${ZERO_GUARD}" "${TMP}/three.trx"; rc=$?
expect_rc "as many tests reported as listed passes" 0 "${rc}"

# The old read took the FIRST total="N" in the file, which can be a test's printed output.
make_trx "${TMP}/three-noisy.trx" 3 0 'summary: total="999"'
sandbox FAKE_LIST_COUNT=5 bash "${ZERO_GUARD}" "${TMP}/three-noisy.trx"; rc=$?
expect_rc "a total=\"999\" printed by a test does not satisfy the floor" nonzero "${rc}"

sandbox FAKE_LIST_COUNT=3 bash "${ZERO_GUARD}" "${TMP}/does-not-exist.trx"; rc=$?
expect_rc "a missing TRX fails" nonzero "${rc}"

# ---------------------------------------------------------------------------------------------
echo "== run-cert-gate.sh: the guards are wired into the required check =="
set_baseline '0\n'
rm -rf "${SANDBOX}/test-results"
sandbox FAKE_LIST_COUNT=5 FAKE_TRX="${TMP}/clean.trx" FAKE_TEST_EXIT=0 bash "${RUN_GATE}"; rc=$?
expect_rc "a clean run passes" 0 "${rc}"

rm -rf "${SANDBOX}/test-results"
sandbox FAKE_LIST_COUNT=5 FAKE_TRX="${TMP}/one-skip.trx" FAKE_TEST_EXIT=0 bash "${RUN_GATE}"; rc=$?
if [[ "${rc}" -ne 0 ]] && grep -qF "were SKIPPED" "${OUT}"; then
  ok "a run with one skipped test fails at the skip guard although dotnet test exited 0"
else
  bad "a run with one skipped test fails at the skip guard although dotnet test exited 0" \
    "exit ${rc}; output: $(tail -c 600 "${OUT}" | tr '\n' ' ')"
fi

rm -rf "${SANDBOX}/test-results"
sandbox FAKE_LIST_COUNT=5 FAKE_TRX= FAKE_TEST_EXIT=0 bash "${RUN_GATE}"; rc=$?
expect_rc "a run whose dotnet test wrote no TRX fails" nonzero "${rc}"

# ---------------------------------------------------------------------------------------------
echo "== summary: says what ran, and does not go green without results =="
: > "${SUMMARY_FILE}"
sandbox bash "${SUMMARY_SH}" "${TMP}/does-not-exist.trx"; rc=$?
expect_rc "the summary exits non-zero when there is no TRX" nonzero "${rc}"
if grep -qF "TRX not found" "${SUMMARY_FILE}"; then
  ok "it still writes a note into the step summary"
else
  bad "it still writes a note into the step summary" "summary: $(head -c 300 "${SUMMARY_FILE}" | tr '\n' ' ')"
fi

# The header's second exit-1 case. The counter reads are plain assignments so `set -e` stops the
# script; `|| echo 0` on them would report 0 of 0 and exit 0. The output check pins WHICH exit 1
# this is: were no-counters.trx ever not built, the no-TRX branch would exit 1 and pass for it.
sandbox bash "${SUMMARY_SH}" "${TMP}/no-counters.trx"; rc=$?
if [[ "${rc}" -ne 0 ]] && grep -qF "expected exactly one <Counters> element" "${OUT}"; then
  ok "the summary exits non-zero on a TRX whose <Counters> cannot be read"
else
  bad "the summary exits non-zero on a TRX whose <Counters> cannot be read" \
    "exit ${rc}; output: $(head -c 600 "${OUT}" | tr '\n' ' ')"
fi

: > "${SUMMARY_FILE}"
sandbox bash "${SUMMARY_SH}" "${TMP}/one-skip.trx"; rc=$?
if [[ "${rc}" -eq 0 ]] && grep -qF "**4** executed of **5** reported, **1** skipped" "${SUMMARY_FILE}"; then
  ok "with a TRX it exits 0 and reports executed, reported and skipped from <Counters>"
else
  bad "with a TRX it exits 0 and reports executed, reported and skipped from <Counters>" \
    "exit ${rc}; summary: $(tail -c 400 "${SUMMARY_FILE}" | tr '\n' ' ')"
fi

finish
