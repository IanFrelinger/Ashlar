#!/usr/bin/env bash
# Tests scripts/release/readiness-verdict-for-sha.sh, the release sign-off's reader of the
# "Readiness verdict" check-run annotation.
#
# WHY A TEST AND NOT JUST THE SCRIPT. Its whole job is to say no. A reader that refuses everything
# looks exactly like a careful one until the day a release needs it, and a reader that accepts too
# much looks exactly like a working one on every green commit. So every branch is driven here from a
# fixture - no network - and each refusal is matched on its REASON, not on a non-zero exit: a python
# traceback also exits non-zero, and would pass any assertion that only checks for "not 0".
#
# The non-vacuity controls come first. If the script cannot be made to say VERIFIED from a clean
# fixture, or cannot be shown to have read the fixture at all, every refusal below proves nothing.
#
# A fixture is never evidence, so a VERIFIED fixture exits 3, not 0: otherwise
# `READINESS_VERDICT_FIXTURE=<file> bash readiness-verdict-for-sha.sh $SHA && git tag ...` would tag
# on a test file. Every fixture-mode VERIFIED below therefore expects 3, and the live stub cases are
# the proof that a VERIFIED read through gh still exits 0.
#
# The live path (gh) is exercised against a stub `gh` placed first on PATH, which serves the same
# fixtures in the shape `gh api --paginate --jq` prints them. That covers the fetching and assembly
# the fixture mode skips, and the unauthenticated case. It does not prove GitHub's API behaves as the
# stub does; that was measured by hand against real check runs when the script was written.
#
# Run:  bash tests/scripts/readiness-verdict-for-sha.test.sh
# Pure bash + python: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/release/readiness-verdict-for-sha.sh"

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
if [[ ! -f "${SCRIPT}" ]]; then
  echo "FAIL - ${SCRIPT} does not exist; nothing to test."
  exit 1
fi

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=43

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

SHA="0123456789abcdef0123456789abcdef01234567"
OTHER_SHA="89abcdef0123456789abcdef0123456789abcdef"
TITLE="Readiness verdict"
FOREIGN_WORKFLOW=".github/workflows/some-other-gate.yml"

# Fixture builder. Items, '|'-separated, defaults filled in:
#   run|<run id>|<event>|<status>|<workflow path>|<head sha>
#   check|<check run id>|<run id>|<status>|<conclusion or ->|<started_at>|<app slug>|<head sha>
#   ann|<check run id>|<title>|<message>
# `split` turns a fixture into the files a stub gh serves, in `gh api --paginate --jq` shape.
# `restatus <file> <status>` rewrites one served run-<id>.json, so that what GET actions/runs/{id}
# answers can differ from what the workflow-runs listing says about the same run.
cat > "${TMP}/fixture.py" <<'PY'
import io, json, os, sys

WF = ".github/workflows/full-platform-readiness-gate.yml"

def field(f, i, default):
    return f[i] if len(f) > i and f[i] != "" else default

def make(out, sha, items):
    doc = {"check_runs": [], "annotations": {}, "workflow_runs": []}
    for item in items:
        f = item.split("|")
        if f[0] == "run":
            rid = int(f[1])
            doc["workflow_runs"].append({
                "id": rid, "event": field(f, 2, "push"), "status": field(f, 3, "completed"),
                "path": field(f, 4, WF), "head_sha": field(f, 5, sha),
                "html_url": "https://github.com/o/r/actions/runs/%d" % rid})
        elif f[0] == "check":
            cid, rid = int(f[1]), int(f[2])
            conclusion = field(f, 4, "success")
            doc["check_runs"].append({
                "id": cid, "name": "Readiness summary", "status": field(f, 3, "completed"),
                "conclusion": None if conclusion == "-" else conclusion,
                "started_at": field(f, 5, "2026-09-30T0100Z"),
                "app": {"slug": field(f, 6, "github-actions")}, "head_sha": field(f, 7, sha),
                "details_url": "https://github.com/o/r/actions/runs/%d/job/%d" % (rid, cid)})
            doc["annotations"].setdefault(str(cid), [])
        elif f[0] == "ann":
            doc["annotations"].setdefault(f[1], []).append(
                {"path": ".github", "annotation_level": "notice", "title": f[2], "message": f[3]})
        else:
            raise SystemExit("unknown fixture item: " + item)
    with io.open(out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh)

def split(doc_path, out_dir):
    doc = json.load(io.open(doc_path, encoding="utf-8"))
    def write(name, lines):
        with io.open(os.path.join(out_dir, name), "w", encoding="utf-8", newline="\n") as fh:
            for line in lines:
                fh.write(line + "\n")
    write("check-runs.ndjson", [json.dumps(c) for c in doc["check_runs"]])
    write("workflow-runs.ndjson", [json.dumps(r) for r in doc["workflow_runs"]])
    for cid, anns in doc["annotations"].items():
        write("annotations-%s.ndjson" % cid, [json.dumps(a) for a in anns])
    for run in doc["workflow_runs"]:
        write("run-%s.json" % run["id"], [json.dumps(run)])

def restatus(path, status):
    with io.open(path, encoding="utf-8") as fh:
        run = json.load(fh)
    if run.get("status") == status:
        raise SystemExit("restatus: %s already has status %s; nothing would change" % (path, status))
    run["status"] = status
    with io.open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(run) + "\n")

if sys.argv[1] == "make":
    make(sys.argv[2], sys.argv[3], sys.argv[4:])
elif sys.argv[1] == "split":
    split(sys.argv[2], sys.argv[3])
elif sys.argv[1] == "restatus":
    restatus(sys.argv[2], sys.argv[3])
else:
    raise SystemExit("unknown fixture command: " + sys.argv[1])
PY

v() { printf 'verdict=%s lanes_ran=%s lanes_skipped=%s' "$1" "$2" "$3"; }

OUT="${TMP}/out.txt"
RC=0
LAST=""

# run_fixture <sha> <items...>: build a fixture and run the script on it in fixture mode.
run_fixture() {
  local sha="$1"; shift
  "${PY_BIN}" "${TMP}/fixture.py" make "${TMP}/fixture.json" "${SHA}" "$@" || {
    echo "FAIL - the fixture builder itself failed; the harness is broken, not the script."; exit 1; }
  READINESS_VERDICT_FIXTURE="${TMP}/fixture.json" bash "${SCRIPT}" "${sha}" >"${OUT}" 2>&1
  RC=$?
  LAST="$(tail -n 1 "${OUT}" | tr -d '\r')"
}

# expect <label> <exit code> <exact last line>
expect() {
  local label="$1" want_rc="$2" want_last="$3"
  if [[ "${RC}" == "${want_rc}" && "${LAST}" == "${want_last}" ]]; then
    ok "${label}"
  else
    bad "${label}" "want exit ${want_rc}, '${want_last}'; got exit ${RC}, '${LAST}'"
  fi
}

FX=" [offline fixture - not evidence]"
# What a VERIFIED fixture exits with. Never 0: a fixture is not evidence (see the header).
FIXTURE_VERIFIED=3
refused() { printf 'readiness-verdict-for-sha: %s REFUSED (%s)%s' "${SHA}" "$1" "${2-${FX}}"; }

RUN="run|501"
CHECK="check|9001|501"

echo "== non-vacuity: the script can say yes, and it is reading the fixture =="
run_fixture "${SHA}" "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "one push run, every lane group ran and passed -> VERIFIED, but exit 3: a fixture is never evidence" \
  "${FIXTURE_VERIFIED}" "readiness-verdict-for-sha: ${SHA} VERIFIED${FX}"
if grep -q "check run 9001 completed/success, verdict=verified" "${OUT}"; then
  ok "the output names the fixture's check run, so the fixture was actually read"
else
  bad "the output names the fixture's check run, so the fixture was actually read" "$(tr -d '\r' < "${OUT}" | head -5)"
fi
if head -n 1 "${OUT}" | grep -q "OFFLINE FIXTURE MODE"; then
  ok "fixture mode announces itself on its first line, so a pasted transcript cannot pass for evidence"
else
  bad "fixture mode announces itself on its first line" "$(head -n 1 "${OUT}")"
fi

echo "== no readiness run at all: the path-filter wrinkle =="
run_fixture "${SHA}"
expect "no check run and no workflow run -> no-readiness-run" 1 "$(refused no-readiness-run)"
if grep -q "PATH-FILTERED" "${OUT}" \
   && grep -q "gh workflow run full-platform-readiness-gate.yml --ref readiness-evidence/${SHA:0:12}" "${OUT}" \
   && grep -q "git push origin ${SHA}:refs/heads/readiness-evidence/${SHA:0:12}" "${OUT}"; then
  ok "the refusal explains the path filter and gives the pinned-ref dispatch route for this SHA"
else
  bad "the refusal explains the path filter and gives the pinned-ref dispatch route" "$(tr -d '\r' < "${OUT}")"
fi

run_fixture "${SHA}" "run|502|push|in_progress"
expect "a run still in progress with no summary check yet -> in-progress, not no-readiness-run" 1 "$(refused in-progress)"

# A run that completed without ever producing a summary check run is not the path filter: the
# advice is a re-run, and a re-run carries the master concurrency caution.
run_fixture "${SHA}" "run|502|push|completed"
expect "a completed run with no summary check run -> no-readiness-run" 1 "$(refused no-readiness-run)"
if grep -q "completed run(s) of full-platform-readiness-gate.yml exist for this commit (502)" "${OUT}" \
   && grep -q "cancel-in-progress: true" "${OUT}"; then
  ok "that refusal names the run, advises a re-run, and says a master re-run shares master's concurrency group"
else
  bad "that refusal names the run, advises a re-run, and gives the re-run's concurrency caution" "$(tr -d '\r' < "${OUT}")"
fi

echo "== the annotation is missing (a run from before the annotation existed) =="
run_fixture "${SHA}" "${RUN}" "${CHECK}" \
  "ann|9001||Job result was skipped (heavy lane not required for this event/diff); counted as a pass."
expect "a green run with only untitled notices -> missing-annotation" 1 "$(refused missing-annotation)"
if grep -q "cancel-in-progress: true" "${OUT}"; then
  ok "the missing-annotation refusal's re-run advice carries the master concurrency caution"
else
  bad "the missing-annotation refusal's re-run advice carries the master concurrency caution" "$(tr -d '\r' < "${OUT}")"
fi

echo "== each verdict other than verified is refused =="
run_fixture "${SHA}" "run|501|pull_request" "${CHECK}" "ann|9001|${TITLE}|$(v partial 3 1)"
expect "partial -> refused (no override)" 1 "$(refused partial)"

run_fixture "${SHA}" "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v not-verified 0 4)"
expect "not-verified -> refused" 1 "$(refused not-verified)"

run_fixture "${SHA}" "${RUN}" "check|9001|501|completed|failure" "ann|9001|${TITLE}|$(v failed 3 0)"
expect "failed -> refused" 1 "$(refused failed)"

run_fixture "${SHA}" "${RUN}" "check|9001|501|in_progress|-"
expect "the summary check run itself still in progress -> in-progress" 1 "$(refused in-progress)"

# Impossible with today's workflow (pull_request skips docker-all-images), which is exactly why it is
# pinned here: if that ever changes, a PR run still tested refs/pull/N/merge, not this SHA.
run_fixture "${SHA}" "run|501|pull_request" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "verified, but only from a pull_request run -> merge-commit-only" 1 "$(refused merge-commit-only)"

# Control for the one above: the event rule is not "anything but push". A merge-queue run checks out
# the group's own head commit, which is the commit that lands.
run_fixture "${SHA}" "run|501|merge_group" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "verified from a merge_group run -> VERIFIED" "${FIXTURE_VERIFIED}" "readiness-verdict-for-sha: ${SHA} VERIFIED${FX}"

echo "== several runs of one SHA =="
run_fixture "${SHA}" "run|501|push" "check|9001|501" "ann|9001|${TITLE}|$(v verified 4 0)" \
  "run|601|workflow_dispatch" "check|9101|601|completed|failure" "ann|9101|${TITLE}|$(v failed 3 0)"
expect "a verified push run and a failed dispatch run -> conflicting-verdicts" 1 "$(refused conflicting-verdicts)"
# The likeliest failure on a release SHA is a master push run cancelled by the next merge. Its fix
# is a re-run of THAT run, which joins master's cancel-in-progress concurrency group; the refusal
# must say so, and must say that the throwaway-branch dispatch route cannot clear a failure.
if grep -q "A dispatch on a throwaway branch does NOT clear this" "${OUT}" \
   && grep -q "cancel-in-progress: true" "${OUT}" \
   && grep -qF "gh run list --workflow full-platform-readiness-gate.yml --branch master --limit 5" "${OUT}"; then
  ok "the refusal says a throwaway dispatch cannot clear a failure, and that a master re-run shares master's concurrency group"
else
  bad "the refusal says a throwaway dispatch cannot clear a failure, and the re-run's concurrency hazard" "$(tr -d '\r' < "${OUT}")"
fi

# A run with no verdict annotation is unread evidence, and unread evidence could have been
# 'failed'. Another run's verified does not stand in for it.
run_fixture "${SHA}" "run|501|push" "check|9001|501" "ann|9001|${TITLE}|$(v verified 4 0)" \
  "run|601|workflow_dispatch" "check|9101|601" "ann|9101||an untitled notice, no verdict"
expect "a verified run beside a completed run with no verdict annotation -> missing-annotation" 1 \
  "$(refused missing-annotation)"

# Control for the one above: several runs are not refused merely for being several.
run_fixture "${SHA}" "run|501|push" "check|9001|501" "ann|9001|${TITLE}|$(v verified 4 0)" \
  "run|601|workflow_dispatch" "check|9101|601" "ann|9101|${TITLE}|$(v verified 4 0)"
expect "two runs that agree on verified -> VERIFIED" "${FIXTURE_VERIFIED}" "readiness-verdict-for-sha: ${SHA} VERIFIED${FX}"

run_fixture "${SHA}" "run|501|push" "check|9001|501" "ann|9001|${TITLE}|$(v verified 4 0)" \
  "run|601|pull_request" "check|9101|601" "ann|9101|${TITLE}|$(v partial 3 1)"
expect "verified beside a partial pull_request run -> VERIFIED (weaker, not contradictory)" "${FIXTURE_VERIFIED}" \
  "readiness-verdict-for-sha: ${SHA} VERIFIED${FX}"

run_fixture "${SHA}" "run|501|push" "check|9001|501" "ann|9001|${TITLE}|$(v verified 4 0)" \
  "run|601|workflow_dispatch|in_progress"
expect "verified beside a run still in progress -> in-progress (it could still fail)" 1 "$(refused in-progress)"

echo "== re-run attempts of ONE run: the latest attempt counts, in both directions =="
# The newer attempt is listed FIRST, so a reader that takes the last element gets this wrong.
run_fixture "${SHA}" "${RUN}" \
  "check|9002|501|completed|success|2026-09-30T0300Z" "ann|9002|${TITLE}|$(v verified 4 0)" \
  "check|9001|501|completed|failure|2026-09-30T0100Z" "ann|9001|${TITLE}|$(v failed 3 0)"
expect "failed, then re-run to verified -> VERIFIED" "${FIXTURE_VERIFIED}" "readiness-verdict-for-sha: ${SHA} VERIFIED${FX}"

run_fixture "${SHA}" "${RUN}" \
  "check|9002|501|completed|failure|2026-09-30T0300Z" "ann|9002|${TITLE}|$(v failed 3 0)" \
  "check|9001|501|completed|success|2026-09-30T0100Z" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "verified, then re-run to failed -> failed" 1 "$(refused failed)"

run_fixture "${SHA}" "run|501|push|in_progress" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "a verified attempt whose run is being re-run right now -> in-progress" 1 "$(refused in-progress)"

echo "== provenance: a check run of the right name is not enough =="
run_fixture "${SHA}" "${RUN}" "check|9001|501|completed|success||some-other-app" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "'Readiness summary' created by another app -> foreign-check-run" 1 "$(refused foreign-check-run)"

run_fixture "${SHA}" "run|501|push|completed|${FOREIGN_WORKFLOW}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "'Readiness summary' from another workflow file -> foreign-check-run" 1 "$(refused foreign-check-run)"

run_fixture "${SHA}" "${RUN}" "check|9001|501|completed|success|||${OTHER_SHA}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "a check run attached to another commit -> sha-mismatch" 1 "$(refused sha-mismatch)"

# The check run says this SHA, but the Actions run behind it evaluated another: the run, not the
# check's label, is what was tested.
run_fixture "${SHA}" "run|501|push|completed||${OTHER_SHA}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "the Actions run evaluated another commit while its check run names this one -> sha-mismatch" 1 \
  "$(refused sha-mismatch)"

echo "== an annotation that cannot be trusted is refused, not read around =="
run_fixture "${SHA}" "${RUN}" "${CHECK}" "ann|9001|${TITLE}|verdict=green lanes_ran=4 lanes_skipped=0"
expect "an unknown verdict word -> malformed-annotation" 1 "$(refused malformed-annotation)"

run_fixture "${SHA}" "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)" "ann|9001|${TITLE}|$(v failed 3 0)"
expect "two verdict annotations on one check run -> malformed-annotation" 1 "$(refused malformed-annotation)"

run_fixture "${SHA}" "${RUN}" "check|9001|501|completed|failure" "ann|9001|${TITLE}|$(v verified 4 0)"
expect "verified on a check run that concluded failure -> malformed-annotation" 1 "$(refused malformed-annotation)"

# 'verified' means no lane group was skipped. A verified word over skipped lanes is the workflow
# and the reader disagreeing about the format, and exactly the reading that would certify a partial.
run_fixture "${SHA}" "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 2 2)"
expect "verified with skipped lanes on a green check run -> malformed-annotation" 1 "$(refused malformed-annotation)"

echo "== it cannot read the evidence at all =="
READINESS_VERDICT_FIXTURE="${TMP}/does-not-exist.json" bash "${SCRIPT}" "${SHA}" >"${OUT}" 2>&1
RC=$?; LAST="$(tail -n 1 "${OUT}" | tr -d '\r')"
expect "an unreadable fixture -> UNDETERMINED, exit 2" 2 \
  "readiness-verdict-for-sha: ${SHA} UNDETERMINED (fixture-unreadable)${FX}"

bash "${SCRIPT}" "${SHA:0:12}" >"${OUT}" 2>&1
RC=$?; LAST="$(tail -n 1 "${OUT}" | tr -d '\r')"
expect "a short SHA is refused as usage, exit 2" 2 "readiness-verdict-for-sha: <no sha> UNDETERMINED (usage)"

echo "== the live path, against a stub gh =="
mkdir -p "${TMP}/bin" "${TMP}/served"
cat > "${TMP}/bin/gh" <<'STUB'
#!/usr/bin/env bash
# Stub gh for tests/scripts/readiness-verdict-for-sha.test.sh. Logs every call; serves files.
echo "$*" >> "${STUB_LOG}"
if [[ "$1" == "auth" ]]; then
  if [[ "${STUB_AUTH:-ok}" == "ok" ]]; then echo "Logged in to github.com (stub)"; exit 0; fi
  echo "You are not logged into any GitHub hosts. To log in, run: gh auth login" >&2
  exit 1
fi
endpoint=""
skip=0
for a in "${@:2}"; do
  if [[ "${skip}" == 1 ]]; then skip=0; continue; fi
  case "${a}" in
    --jq) skip=1 ;;
    --*) ;;
    *) endpoint="${a}"; break ;;
  esac
done
case "${endpoint}" in
  */commits/*/check-runs\?*)
    if [[ "${STUB_FAIL:-}" == "check-runs" ]]; then echo "gh: No commit found for SHA (HTTP 422)" >&2; exit 1; fi
    cat "${STUB_DIR}/check-runs.ndjson" ;;
  */actions/workflows/*/runs\?*) cat "${STUB_DIR}/workflow-runs.ndjson" ;;
  */check-runs/*/annotations\?*) id="${endpoint#*check-runs/}"; cat "${STUB_DIR}/annotations-${id%%/*}.ndjson" ;;
  */actions/runs/*) cat "${STUB_DIR}/run-${endpoint##*/}.json" ;;
  *) echo "gh: Not Found (HTTP 404) ${endpoint}" >&2; exit 1 ;;
esac
STUB
chmod +x "${TMP}/bin/gh"

# run_live [STUB env assignments...]: run the script in live mode against the stub.
run_live() {
  : > "${TMP}/calls.log"
  env PATH="${TMP}/bin:${PATH}" STUB_DIR="${TMP}/served" STUB_LOG="${TMP}/calls.log" "$@" \
    bash "${SCRIPT}" "${SHA}" >"${OUT}" 2>&1
  RC=$?
  LAST="$(tail -n 1 "${OUT}" | tr -d '\r')"
}

# serve <items...>: build a fixture and lay it out as the files the stub gh serves, replacing the
# previous case's files so none of them can leak into the next case.
serve() {
  rm -rf "${TMP}/served" && mkdir -p "${TMP}/served"
  if ! "${PY_BIN}" "${TMP}/fixture.py" make "${TMP}/fixture.json" "${SHA}" "$@" \
     || ! "${PY_BIN}" "${TMP}/fixture.py" split "${TMP}/fixture.json" "${TMP}/served"; then
    echo "FAIL - the fixture builder itself failed; the harness is broken, not the script."
    exit 1
  fi
}

serve "${RUN}" "${CHECK}" "ann|9001||an untitled notice first" "ann|9001|${TITLE}|$(v verified 4 0)"

# Guard the stub, not the script: if PATH lookup does not reach it, the live cases below would
# talk to the real gh and the network.
if [[ "$(env PATH="${TMP}/bin:${PATH}" bash -c 'command -v gh')" == "${TMP}/bin/gh" ]]; then
  ok "the stub gh is the one the script will find on PATH"
else
  bad "the stub gh is the one the script will find on PATH" "$(env PATH="${TMP}/bin:${PATH}" bash -c 'command -v gh')"
fi

run_live STUB_AUTH=no
expect "gh unauthenticated -> UNDETERMINED (gh-unauthenticated), exit 2" 2 \
  "readiness-verdict-for-sha: ${SHA} UNDETERMINED (gh-unauthenticated)"

run_live STUB_AUTH=ok
expect "live fetch and assembly of a verified run -> VERIFIED, exit 0, with no fixture caveat" 0 \
  "readiness-verdict-for-sha: ${SHA} VERIFIED"
if grep -q "api --paginate repos/{owner}/{repo}/commits/${SHA}/check-runs?check_name=Readiness%20summary&filter=all&per_page=100 --jq .check_runs\[\]" "${TMP}/calls.log" \
   && grep -qF "api --paginate repos/{owner}/{repo}/actions/workflows/full-platform-readiness-gate.yml/runs?head_sha=${SHA}&per_page=100 --jq .workflow_runs[]" "${TMP}/calls.log" \
   && grep -q "api --paginate repos/{owner}/{repo}/check-runs/9001/annotations?per_page=100 --jq .\[\]" "${TMP}/calls.log" \
   && grep -q "api repos/{owner}/{repo}/actions/runs/501$" "${TMP}/calls.log"; then
  ok "the live path asks for this SHA's 'Readiness summary' runs with filter=all, the workflow's runs of this SHA, their annotations, and each run"
else
  bad "the live path asks for this SHA's runs with filter=all, the workflow's runs of this SHA, their annotations, and each run" "$(cat "${TMP}/calls.log")"
fi

# A second run of this SHA that has not reached its summary job has no check run, so only the
# workflow-runs listing can show it. The fixture-mode case of this skips the fetch that finds it;
# this one goes through it.
serve "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)" "run|601|workflow_dispatch|in_progress"
run_live STUB_AUTH=ok
expect "live: a verified push run beside a dispatch still running with no summary check yet -> in-progress" 1 \
  "readiness-verdict-for-sha: ${SHA} REFUSED (in-progress)"

# The listing above only finds runs that have no summary check run yet. What the Actions run behind
# a check run IS - its workflow file, its commit, whether it has finished - is read from
# GET actions/runs/{id}, fetched per run, because the listing is fetched first and can be stale or
# transiently omit a run. The call-log assertion above proves that GET is issued; these two make the
# listing and the per-run answer disagree, so a reader that issues the GET but reads the listing
# instead gets both wrong: (a) comes out foreign-check-run, and (b) certifies a run whose re-run is
# still going.
serve "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
: > "${TMP}/served/workflow-runs.ndjson"
if [[ -s "${TMP}/served/workflow-runs.ndjson" ]]; then
  echo "FAIL - could not empty the served workflow-runs listing; the harness is broken, not the script."
  exit 1
fi
run_live STUB_AUTH=ok
expect "live: run 501 missing from the listing but fetched by id as completed -> VERIFIED, exit 0" 0 \
  "readiness-verdict-for-sha: ${SHA} VERIFIED"

serve "${RUN}" "${CHECK}" "ann|9001|${TITLE}|$(v verified 4 0)"
"${PY_BIN}" "${TMP}/fixture.py" restatus "${TMP}/served/run-501.json" in_progress || {
  echo "FAIL - could not restatus the served run-501.json; the harness is broken, not the script."; exit 1; }
run_live STUB_AUTH=ok
expect "live: the listing says run 501 completed, its own fetch says in_progress (a re-run started) -> in-progress" 1 \
  "readiness-verdict-for-sha: ${SHA} REFUSED (in-progress)"

run_live STUB_AUTH=ok STUB_FAIL=check-runs
expect "an API error -> UNDETERMINED (api-error), exit 2" 2 \
  "readiness-verdict-for-sha: ${SHA} UNDETERMINED (api-error)"

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
