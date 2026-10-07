#!/usr/bin/env bash
# Tests the `Readiness summary` verdict in .github/workflows/full-platform-readiness-gate.yml.
#
# WHY A TEST AND NOT JUST THE CHANGE. `Readiness summary` is a REQUIRED status check with
# enforce_admins=true, so it has to exit 0 when the heavy lanes are skipped - failing on a skip would
# leave every PR that touches no core path waiting forever. That means the green tick cannot
# distinguish "every platform passed" from "no platform ran", and for a long time the step printed
# "ALL PLATFORMS PASSED" for both. 17 of 45 successful runs had run no Linux lane at all.
#
# The fix is a verdict that says which happened. A verdict is only worth something if each of its
# branches has been observed firing, so this file drives the REAL step text - extracted from the
# workflow, not copied - through every lane-result combination and checks the verdict, the exit code,
# and, for the case that motivated it, that the headline no longer claims a pass.
#
# The verdict leaves the run only as a check-run annotation, so every case also checks the one
# "Readiness verdict" annotation line the step prints, and two cases hand that line to the release
# sign-off (scripts/release/readiness-verdict-for-sha.sh) to prove writer and reader agree.
#
# Run:  bash tests/scripts/readiness-verdict.test.sh
# Pure bash + python: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WF="${ROOT}/.github/workflows/full-platform-readiness-gate.yml"

# PROBE the interpreter, do not merely locate it. On Windows `command -v python3` finds an App
# Execution Alias stub that exists, is executable, and exits 49 telling you to visit the Microsoft
# Store - indistinguishable from a real refusal to any assertion that only checks a non-zero exit.
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

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=18

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

# Extract the step's run: block from the workflow and turn the GitHub expressions into shell
# variables. Single-quoted heredoc so the shell expands nothing in here.
"${PY_BIN}" - "${WF}" "${TMP}/step.sh" <<'PY'
import io, re, sys
wf, out = sys.argv[1], sys.argv[2]
text = io.open(wf, encoding="utf-8").read()
m = re.search(
    r"      - name: Summarize results\n"
    r"        id: summary\n"
    r"        shell: bash\n"
    r"        run: \|\n"
    r"(.*?)(?=\n      - name: |\n  [a-z-]+:\n|\Z)",
    text, re.S)
if not m:
    sys.stderr.write("EXTRACT-FAILED: the 'Summarize results' step did not match. If the step was "
                     "renamed or reindented, fix this extractor - do not delete the test.\n")
    sys.exit(3)
body = "\n".join(l[10:] if l.startswith(" " * 10) else l for l in m.group(1).split("\n"))
for expr, var in {
    "${{ needs.changes.result }}": "$CHANGES_RESULT",
    "${{ needs.changes.outputs.run_heavy }}": "$RUN_HEAVY",
    "${{ needs.native-platform.result }}": "$NATIVE",
    "${{ needs.container-platform.result }}": "$CONTAINER",
    "${{ needs.docker-cli-image.result }}": "$DOCKER_CLI",
    "${{ needs.docker-all-images.result }}": "$DOCKER_ALL",
}.items():
    body = body.replace(expr, var)
left = re.findall(r"\$\{\{[^}]*\}\}", body)
if left:
    sys.stderr.write(f"EXTRACT-FAILED: unsubstituted expressions {sorted(set(left))}\n")
    sys.exit(3)
if "VERDICT=" not in body:
    sys.stderr.write("EXTRACT-FAILED: extracted text sets no VERDICT; it is not the verdict step.\n")
    sys.exit(3)
io.open(out, "w", encoding="utf-8", newline="\n").write("set -o pipefail\n" + body + "\n")
PY
EXTRACT_RC=$?

# Non-vacuity control. If extraction broke, every case below would run an empty script, report
# verdict "" and exit 0, and three of the seven assertions would accidentally hold.
if [[ "${EXTRACT_RC}" -ne 0 ]]; then
  bad "the step text can be extracted from the workflow" "extractor exited ${EXTRACT_RC}"
  echo
  echo "passed: ${PASS}   failed: ${FAIL}"
  exit 1
fi
ok "the step text can be extracted from the workflow and fully de-templated"

# The verdict annotation: the ONLY copy of the verdict readable from outside the run (job outputs
# stay inside it; the step summary is not exposed by the Checks API). GitHub turns this workflow
# command into an annotation titled "Readiness verdict" on the check run, and
# scripts/release/readiness-verdict-for-sha.sh matches that title and parses the message.
ANNOTATION_PREFIX="::notice title=Readiness verdict::"

# Run the step with one set of lane results. Echoes "<exit>|<verdict>|<annotation>|<headline>".
# <annotation> is the one verdict annotation line the step printed, or "COUNT=<n>" when it printed
# some other number of them - zero and two are both wrong, and must not look like the right line.
run_case() {
  local changes="$1" heavy="$2" native="$3" container="$4" cli="$5" dall="$6"
  local sum="${TMP}/summary.md" outf="${TMP}/output.txt" stdout="${TMP}/stdout.txt"
  : > "${sum}"; : > "${outf}"; : > "${stdout}"
  local code verdict headline annotation count
  CHANGES_RESULT="${changes}" RUN_HEAVY="${heavy}" NATIVE="${native}" \
  CONTAINER="${container}" DOCKER_CLI="${cli}" DOCKER_ALL="${dall}" \
  GITHUB_STEP_SUMMARY="${sum}" GITHUB_OUTPUT="${outf}" \
    bash "${TMP}/step.sh" >"${stdout}" 2>&1
  code=$?
  verdict="$(sed -n 's/^verdict=//p' "${outf}" | tail -1)"
  headline="$(grep -m1 '^\*\*Result:' "${sum}" || true)"
  count="$(grep -cF "${ANNOTATION_PREFIX}" "${stdout}")"
  if [[ "${count}" == "1" ]]; then
    annotation="$(grep -F "${ANNOTATION_PREFIX}" "${stdout}")"
  else
    annotation="COUNT=${count}"
  fi
  printf '%s|%s|%s|%s' "${code}" "${verdict}" "${annotation}" "${headline}"
}

# expect <label> <verdict> <exit> <lanes ran> <lanes skipped> <run_case output>
# Two assertions: the verdict and exit code, then the annotation that carries them out of the run.
expect() {
  local label="$1" want_verdict="$2" want_rc="$3" want_ran="$4" want_skipped="$5" got="$6"
  local code="${got%%|*}" rest="${got#*|}"
  local verdict="${rest%%|*}"
  rest="${rest#*|}"
  local annotation="${rest%%|*}"
  if [[ "${verdict}" == "${want_verdict}" && "${code}" == "${want_rc}" ]]; then
    ok "${label} -> ${want_verdict}, exit ${want_rc}"
  else
    bad "${label} -> ${want_verdict}, exit ${want_rc}" "got verdict '${verdict}', exit ${code}"
  fi
  local want_annotation="${ANNOTATION_PREFIX}verdict=${want_verdict} lanes_ran=${want_ran} lanes_skipped=${want_skipped}"
  if [[ "${annotation}" == "${want_annotation}" ]]; then
    ok "${label} -> publishes exactly one '${want_annotation#::notice title=}'"
  else
    bad "${label} -> publishes exactly one verdict annotation" "want '${want_annotation}', got '${annotation}'"
  fi
}

echo "== a green tick means different things, and the verdict has to say which =="
expect "all four lane groups succeeded" verified 0 4 0 \
  "$(run_case success true success success success success)"

expect "some lanes ran, some were skipped" partial 0 2 2 \
  "$(run_case success true success success skipped skipped)"

expect "every lane was skipped" not-verified 0 0 4 \
  "$(run_case success false skipped skipped skipped skipped)"

echo "== a failure is still a failure, and still blocks =="
expect "one lane failed" failed 1 3 0 \
  "$(run_case success true failure success success success)"

expect "one lane was cancelled" failed 1 3 0 \
  "$(run_case success true cancelled success success success)"

# A broken change detector means the lanes were skipped for the WRONG reason, so the skip decision
# cannot be trusted and the gate must go red rather than report not-verified.
expect "the change detector itself failed" failed 1 0 4 \
  "$(run_case failure false skipped skipped skipped skipped)"

echo "== the regression this file exists to prevent =="
GOT="$(run_case success false skipped skipped skipped skipped)"
HEADLINE="${GOT##*|}"
if [[ "${HEADLINE}" == *"ALL PLATFORMS PASSED"* ]]; then
  bad "an all-skipped run must not claim every platform passed" "headline was: ${HEADLINE}"
else
  ok "an all-skipped run does not claim every platform passed"
fi

if [[ "${HEADLINE}" == *"NOT VERIFIED"* ]]; then
  ok "an all-skipped run says NOT VERIFIED in its headline"
else
  bad "an all-skipped run says NOT VERIFIED in its headline" "headline was: ${HEADLINE}"
fi

# The verdict is written before `exit $OVERALL`. If that ordering is ever reversed, a failing run
# publishes no verdict and anything reading it sees an empty string rather than "failed".
echo "== a failing run still publishes its verdict =="
GOT="$(run_case success true failure success success success)"
VERDICT="${GOT#*|}"; VERDICT="${VERDICT%%|*}"
[[ -n "${VERDICT}" ]] && ok "a failing run writes its verdict to GITHUB_OUTPUT before exiting" \
  || bad "a failing run writes its verdict to GITHUB_OUTPUT before exiting" "verdict was empty"

# The writer and the reader are two files that must agree on a title and a message format. Checking
# each against its own copy of the format would let them drift apart together, so the line the REAL
# step prints is turned into the annotation GitHub would make of it and handed to the REAL reader.
echo "== the line the step prints is the line the release sign-off accepts =="
SIGNOFF="${ROOT}/scripts/release/readiness-verdict-for-sha.sh"
RT_SHA="fedcba9876543210fedcba9876543210fedcba98"

# round_trip <run_case output>: run the reader on a one-run fixture carrying that annotation, with
# the check conclusion the step's exit code implies. Echoes "<exit>|<reader's last line>".
round_trip() {
  local got="$1"
  local code="${got%%|*}" rest="${got#*|}"
  rest="${rest#*|}"
  local annotation="${rest%%|*}" conclusion=success
  [[ "${code}" == "0" ]] || conclusion=failure
  "${PY_BIN}" - "${TMP}/round-trip.json" "${RT_SHA}" "${conclusion}" "${annotation}" <<'PY'
import io, json, sys
out, sha, conclusion, line = sys.argv[1:5]
# "::notice title=<title>::<message>" -> {"title": <title>, "message": <message>}. Anything that is
# not that shape yields an empty title, which the reader must then refuse.
command, _, message = line[2:].partition("::") if line.startswith("::") else ("", "", "")
props = dict(p.split("=", 1) for p in command.partition(" ")[2].split(",") if "=" in p)
doc = {
    "check_runs": [{
        "id": 1, "name": "Readiness summary", "status": "completed", "conclusion": conclusion,
        "head_sha": sha, "started_at": "2026-09-30T0100Z", "app": {"slug": "github-actions"},
        "details_url": "https://github.com/o/r/actions/runs/2/job/1"}],
    "annotations": {"1": [{"annotation_level": command.partition(" ")[0], "title": props.get("title", ""),
                           "message": message}]},
    "workflow_runs": [{"id": 2, "event": "push", "status": "completed", "head_sha": sha,
                       "path": ".github/workflows/full-platform-readiness-gate.yml"}],
}
with io.open(out, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(doc, fh)
PY
  local rc last
  READINESS_VERDICT_FIXTURE="${TMP}/round-trip.json" bash "${SIGNOFF}" "${RT_SHA}" >"${TMP}/signoff.txt" 2>&1
  rc=$?
  last="$(tail -n 1 "${TMP}/signoff.txt" | tr -d '\r')"
  printf '%s|%s' "${rc}" "${last}"
}

# Exit 3, not 0: the reader is fed a fixture here, and a fixture is never evidence, so even a
# VERIFIED reading of one must not pass a `... && git tag` (scripts/release/readiness-verdict-for-sha.sh).
GOT="$(round_trip "$(run_case success true success success success success)")"
WANT="3|readiness-verdict-for-sha: ${RT_SHA} VERIFIED [offline fixture - not evidence]"
if [[ "${GOT}" == "${WANT}" ]]; then
  ok "an all-passed run's annotation is read by the sign-off as VERIFIED"
else
  bad "an all-passed run's annotation is read by the sign-off as VERIFIED" "want '${WANT}', got '${GOT}'"
fi

GOT="$(round_trip "$(run_case success true failure success success success)")"
WANT="1|readiness-verdict-for-sha: ${RT_SHA} REFUSED (failed) [offline fixture - not evidence]"
if [[ "${GOT}" == "${WANT}" ]]; then
  ok "a failed run's annotation is read by the sign-off as REFUSED (failed)"
else
  bad "a failed run's annotation is read by the sign-off as REFUSED (failed)" "want '${WANT}', got '${GOT}'"
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
