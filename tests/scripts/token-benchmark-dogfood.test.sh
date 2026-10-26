#!/usr/bin/env bash
# Tests for the dogfood tooling in tools/token-benchmark: capture_brick.py --targets, which records one
# github.blockers answer per URL through an MCP host, and dogfood_summarize.py's check(), which decides
# whether a model's summary kept the brick's verdict and cited only URLs the brick returned.
# The host is the stub server the PR blockers Action tests use, so no dotnet, network or API key is needed.
#
# The properties that matter: a URL the host did not answer is recorded as "no-answer", never dropped or
# reported as an answer; a summary that drops the verdict, invents a link or truncates one is flagged.
#
# Run:  bash tests/scripts/token-benchmark-dogfood.test.sh

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TOOLS="${ROOT}/tools/token-benchmark"
STUB="${ROOT}/tests/scripts/fixtures/pr-blockers/stub_mcp_server.py"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }
check() { if eval "$2"; then ok "$1"; else bad "$1" "$3"; fi; }
EXPECTED_ASSERTIONS=12

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

cat > "${WORK}/targets.json" <<'EOF'
{"workloads": [{"id": "t", "urls": ["https://github.com/o/r/pull/7", "https://github.com/o/r/releases/tag/v1"]}]}
EOF

echo "capture_brick.py --targets"
STUB_LOG="${WORK}/stub-answered.log" STUB_MODE=blocked python3 "${TOOLS}/capture_brick.py" "${WORK}/answered" --targets "${WORK}/targets.json" \
  -- python3 "${STUB}" > "${WORK}/answered.log" 2>&1
RC=$?
check "exits 0 when every URL is answered" '[[ ${RC} -eq 0 ]]' "rc=${RC}: $(tail -3 "${WORK}/answered.log")"
check "writes one payload per URL in the targets file" \
  '[[ -f "${WORK}/answered/o_r_pull_7.json" && -f "${WORK}/answered/o_r_releases_tag_v1.json" ]]' \
  "$(ls "${WORK}/answered" 2>&1)"
V="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["outputs"]["verdict"])' "${WORK}/answered/o_r_pull_7.json" 2>&1)"
check "records the host's verdict" '[[ "${V}" == "blocked" ]]' "verdict=${V}"

STUB_LOG="${WORK}/stub-unanswered.log" STUB_MODE=tool-error python3 "${TOOLS}/capture_brick.py" "${WORK}/unanswered" --targets "${WORK}/targets.json" \
  -- python3 "${STUB}" > "${WORK}/unanswered.log" 2>&1
RC=$?
V="$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["outputs"]["verdict"], "no_answer" in d)' "${WORK}/unanswered/o_r_pull_7.json" 2>&1)"
R="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["no_answer"])' "${WORK}/unanswered/o_r_pull_7.json" 2>&1)"
check "a URL the host did not answer is recorded as no-answer" \
  '[[ ${RC} -eq 0 && "${V}" == "no-answer True" ]]' "rc=${RC} recorded=${V}"
check "the no-answer record keeps the host's reason" '[[ "${R}" == *"tool error"* ]]' "reason=${R}"

echo "dogfood_summarize.check"
probe() {
  (cd "${TOOLS}" && python3 -c '
import json, sys
from dogfood_summarize import check
outputs = {"verdict": "blocked",
           "evidence": ["https://github.com/o/r/pull/7"],
           "blockers": [{"reason": "x", "evidence": "https://github.com/o/r/actions/runs/1/job/2"}],
           "notes": [{"text": "n", "evidence": "https://github.com/o/r/commit/abc"}]}
r = check(sys.argv[1], outputs)
print(r["states_verdict"], len(r["urls"]), len(r["invented_urls"]))' "$1" 2>&1)
}
R="$(probe 'PR #7 is blocked by a failing check (https://github.com/o/r/actions/runs/1/job/2).')"
check "accepts a summary with the verdict and a returned URL in parentheses" '[[ "${R}" == "True 1 0" ]]' "${R}"
R="$(probe 'PR #7 is BLOCKED; see https://github.com/o/r/pull/7.')"
check "matches the verdict case-insensitively and strips trailing punctuation" '[[ "${R}" == "True 1 0" ]]' "${R}"
R="$(probe 'PR #7 cannot merge yet; see https://github.com/o/r/commit/abc')"
check "flags a summary that drops the verdict word" '[[ "${R}" == "False 1 0" ]]' "${R}"
R="$(probe 'PR #7 is blocked; see https://github.com/o/r/actions/runs/99')"
check "flags a URL the brick did not return" '[[ "${R}" == "True 1 1" ]]' "${R}"
R="$(probe 'PR #7 is blocked (https://github.com/o/r/act')"
check "flags a URL cut off mid-way" '[[ "${R}" == "True 1 1" ]]' "${R}"
R="$(probe 'PR #7 is unblocked now.')"
check "does not take a longer word for the verdict" '[[ "${R}" == "False 0 0" ]]' "${R}"
R="$(probe '')"
check "flags an empty summary" '[[ "${R}" == "False 0 0" ]]' "${R}"

TOTAL=$((PASS + FAIL))
echo
echo "passed: ${PASS}   failed: ${FAIL}"
if [[ ${TOTAL} -ne ${EXPECTED_ASSERTIONS} ]]; then
  echo "expected ${EXPECTED_ASSERTIONS} assertions, ran ${TOTAL}"
  exit 1
fi
[[ ${FAIL} -eq 0 ]]
