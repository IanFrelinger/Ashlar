#!/usr/bin/env bash
# Tests the test and metrics steps of .github/workflows/perf-certification.yml.
#
# WHY A TEST AND NOT JUST THE CHANGE. The test step runs one solution-wide invocation, gathers every
# project's TRX into perf-results/trx, and guards them. The metrics step (if: always()) then sums
# those files into the report and the step summary, and the upload ships perf-results/. If the gather
# sits after a bare `dotnet test` under `set -e`, a FAILING run exits before the copy: the report
# then says 0 tests and 0 failed, and the artifact carries no results - on exactly the run someone
# has to diagnose. Nothing about that is visible on a green run, so this drives the REAL step text -
# extracted from the workflow, not copied - with a stand-in `dotnet` on PATH that writes TRX files
# where VSTest would and exits with a chosen code.
#
# The stand-in writes into TWO projects under the same file name, as measured on a real solution
# run (two projects both wrote perf-certification_net8.0_20260930163958.trx), so the gather's
# "one file per project" claim is exercised too.
#
# Run:  bash tests/scripts/perf-certification-trx.test.sh
# Pure bash + python: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WF="${ROOT}/.github/workflows/perf-certification.yml"
GUARD="${ROOT}/scripts/ci/zero-test-guard.sh"
FX="${ROOT}/tests/scripts/fixtures/zero-test-guard"

# PROBE the interpreter, do not merely locate it. On Windows `command -v python3` finds an App
# Execution Alias stub that exists, is executable, and exits 49 - indistinguishable from a refusal to
# any assertion that only checks a non-zero exit.
PY_BIN=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'print(1)' >/dev/null 2>&1; then
    PY_BIN="${candidate}"
    break
  fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "FAIL - no working python on PATH; this file cannot test anything and will not pretend to."
  exit 1
fi
for f in "${WF}" "${GUARD}" "${FX}/real-pass.trx.xml" "${FX}/no-match.trx.xml"; do
  if [[ ! -s "${f}" ]]; then
    echo "FAIL - ${f} is missing or empty; nothing here can be tested."
    exit 1
  fi
done

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=8

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

# Extract both steps' run: blocks and replace the GitHub expressions with fixed text. Single-quoted
# heredoc so the shell expands nothing in here.
"${PY_BIN}" - "${WF}" "${TMP}" <<'PY'
import io, re, sys
wf, out = sys.argv[1], sys.argv[2]
text = io.open(wf, encoding="utf-8").read().replace("\r\n", "\n")
steps = {
    "test.sh": r"      - name: Run perf / benchmark-scoped tests\n        shell: bash\n        run: \|\n",
    "metrics.sh": r"      - name: Extract perf metrics and check thresholds\n        if: always\(\)\n"
                  r"        shell: bash\n        run: \|\n",
}
for name, head in steps.items():
    m = re.search(head + r"(.*?)(?=\n      - name: |\n      - uses: |\Z)", text, re.S)
    if not m:
        sys.stderr.write(f"EXTRACT-FAILED: {name}: the step did not match. If it was renamed or "
                         "reindented, fix this extractor - do not delete the test.\n")
        sys.exit(3)
    body = "\n".join(l[10:] if l.startswith(" " * 10) else l for l in m.group(1).split("\n"))
    for expr, value in {
        "${{ github.event.inputs.baseline }}": "test-baseline",
        "${{ github.run_id }}": "1",
        "${{ github.sha }}": "0000000",
    }.items():
        body = body.replace(expr, value)
    left = re.findall(r"\$\{\{[^}]*\}\}", body)
    if left:
        sys.stderr.write(f"EXTRACT-FAILED: {name}: unsubstituted expressions {sorted(set(left))}\n")
        sys.exit(3)
    io.open(f"{out}/{name}", "w", encoding="utf-8", newline="\n").write(body + "\n")
PY
if [[ $? -ne 0 ]]; then
  echo "FAIL - could not extract the perf-certification steps; nothing below would test the workflow."
  exit 1
fi
# Non-vacuity: the extracted test step must be the one that runs the solution and gathers its TRX.
if ! grep -q 'Ashlar.sln' "${TMP}/test.sh" || ! grep -q 'perf-results/trx' "${TMP}/test.sh"; then
  echo "FAIL - the extracted test step no longer runs Ashlar.sln or gathers into perf-results/trx."
  exit 1
fi

# A stand-in `dotnet`: records its arguments, writes one TRX into each of two projects under the SAME
# name (as VSTest does with LogFilePrefix in one second), and exits with FAKE_RC.
mkdir -p "${TMP}/bin"
cat > "${TMP}/bin/dotnet" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${FAKE_LOG}"
mkdir -p src/ProjA/TestResults src/ProjB/TestResults
cp "${FAKE_TRX_A}" src/ProjA/TestResults/perf-certification_net8.0_20260930000000.trx
cp "${FAKE_TRX_B}" src/ProjB/TestResults/perf-certification_net8.0_20260930000000.trx
exit "${FAKE_RC}"
SH
chmod +x "${TMP}/bin/dotnet"

# A failing run's TRX, derived from the real one: its single test failed.
sed -e 's/outcome="Passed"/outcome="Failed"/' -e 's/passed="1" failed="0"/passed="0" failed="1"/' \
  "${FX}/real-pass.trx.xml" > "${TMP}/failed.trx"
if ! grep -q 'outcome="Failed"' "${TMP}/failed.trx"; then
  echo "FAIL - arrange failed: the derived TRX carries no failed result."
  exit 1
fi

# Runs the extracted test step (and, with a 5th argument, the metrics step after it) in a fresh
# workspace. Echoes "<test exit>|<metrics exit>|<output>".
run_steps() {  # trx_a trx_b rc workspace [metrics]
  local ws="$4" out code mcode="-"
  mkdir -p "${ws}/scripts/ci"
  cp "${GUARD}" "${ws}/scripts/ci/zero-test-guard.sh"
  out="$(cd "${ws}" && PATH="${TMP}/bin:${PATH}" FAKE_LOG="${ws}/dotnet-args.log" \
    FAKE_TRX_A="$1" FAKE_TRX_B="$2" FAKE_RC="$3" bash "${TMP}/test.sh" 2>&1)"; code=$?
  if [[ -n "${5:-}" ]]; then
    out+=$'\n'"$(cd "${ws}" && GITHUB_STEP_SUMMARY="${ws}/summary.md" bash "${TMP}/metrics.sh" 2>&1)"; mcode=$?
  fi
  printf '%s|%s|%s' "${code}" "${mcode}" "${out}"
}

trx_count() { find "$1/perf-results/trx" -name '*.trx' 2>/dev/null | wc -l | tr -d ' '; }

echo "== a failing run keeps its results =="
WS="${TMP}/failing"
got="$(run_steps "${TMP}/failed.trx" "${FX}/no-match.trx.xml" 3 "${WS}" metrics)"
code="${got%%|*}"; rest="${got#*|}"; mcode="${rest%%|*}"; out="${rest#*|}"
if grep -q -- '--filter' "${WS}/dotnet-args.log" 2>/dev/null \
   && grep -q 'LogFilePrefix=perf-certification' "${WS}/dotnet-args.log"; then
  ok "the step ran the stand-in dotnet with the workflow's filter and LogFilePrefix"
else
  bad "the step ran the stand-in dotnet with the workflow's filter and LogFilePrefix" \
    "args log: $(cat "${WS}/dotnet-args.log" 2>/dev/null || echo '<none>'); output: ${out}"
fi
if [[ "${code}" == "3" ]]; then
  ok "the step exits with dotnet test's own code (3), not the guard's"
else
  bad "the step exits with dotnet test's own code (3), not the guard's" "got exit ${code}: ${out}"
fi
n="$(trx_count "${WS}")"
if [[ "${n}" == "2" ]]; then
  ok "both projects' TRX files are gathered even though the run failed"
else
  bad "both projects' TRX files are gathered even though the run failed" \
    "found ${n} in perf-results/trx: $(ls "${WS}/perf-results/trx" 2>&1)"
fi
report="$(cat "${WS}/perf-results/perf-certification-report.json" 2>/dev/null)"
if [[ "${mcode}" == "1" ]] && grep -q '"failed": 1' <<<"${report}" && grep -q '"totalTests": 1' <<<"${report}"; then
  ok "the metrics step reports the failed test and fails"
else
  bad "the metrics step reports the failed test and fails" "metrics exit ${mcode}; report: ${report}"
fi

echo "== a passing run is guarded and counted =="
WS="${TMP}/passing"
got="$(run_steps "${FX}/real-pass.trx.xml" "${FX}/no-match.trx.xml" 0 "${WS}" metrics)"
code="${got%%|*}"; rest="${got#*|}"; mcode="${rest%%|*}"; out="${rest#*|}"
if [[ "${code}" == "0" ]] && grep -q 'ProjA__perf-certification_net8.0_20260930000000.trx: executed=1' <<<"${out}"; then
  ok "the step passes and the --solution guard reads the gathered TRX"
else
  bad "the step passes and the --solution guard reads the gathered TRX" "got exit ${code}: ${out}"
fi
n="$(trx_count "${WS}")"
report="$(cat "${WS}/perf-results/perf-certification-report.json" 2>/dev/null)"
if [[ "${n}" == "2" ]]; then
  ok "two projects writing the same TRX name are both kept"
else
  bad "two projects writing the same TRX name are both kept" "found ${n}: $(ls "${WS}/perf-results/trx" 2>&1)"
fi
if [[ "${mcode}" == "0" ]] && grep -q '"passed": 1' <<<"${report}" && grep -q '"verdict": "PASS"' <<<"${report}"; then
  ok "the metrics step counts the passed test and passes"
else
  bad "the metrics step counts the passed test and passes" "metrics exit ${mcode}; report: ${report}"
fi

echo "== a run where no project executed a test fails =="
WS="${TMP}/nothing"
got="$(run_steps "${FX}/no-match.trx.xml" "${FX}/no-match.trx.xml" 0 "${WS}")"
code="${got%%|*}"; rest="${got#*|}"; out="${rest#*|}"
if [[ "${code}" == "1" ]] && grep -q 'no project in the solution-wide run executed a test' <<<"${out}"; then
  ok "a solution run that matched nothing anywhere fails on the guard"
else
  bad "a solution run that matched nothing anywhere fails on the guard" "got exit ${code}: ${out}"
fi

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
