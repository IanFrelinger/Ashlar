#!/usr/bin/env bash
# Tests for actions/pr-blockers/pr_blockers.py, the MCP client inside the free "Ashlar PR blockers"
# GitHub Action. It runs against a stub server (fixtures/pr-blockers/stub_mcp_server.py) that speaks
# the same stdio JSON-RPC as Ashlar.Mcp.Server.Host, so the protocol exchange, the verdict mapping
# and the exit codes are proven without dotnet or network. What it cannot prove: that the real host
# answers this way; that is the dogfood workflow's job (.github/workflows/pr-blockers-action.yml).
#
# The property that matters most: a question that got no answer must never read as "ready".
#
# Run:  bash tests/scripts/pr-blockers-action.test.sh

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CLIENT="${ROOT}/actions/pr-blockers/pr_blockers.py"
STUB="${ROOT}/tests/scripts/fixtures/pr-blockers/stub_mcp_server.py"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }
EXPECTED_ASSERTIONS=30

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

# run <mode> [fail-on-blocked] [token] -> sets RC, OUT, SUMMARY, OUTPUTS, LOG
run() {
  local mode="$1" fob="${2:-false}" token="${3:-}"
  local d="${WORK}/${mode}-${fob}"
  mkdir -p "${d}"
  : > "${d}/summary"; : > "${d}/output"; : > "${d}/log"
  RC=0
  OUT="$(STUB_MODE="${mode}" STUB_LOG="${d}/log" \
    ASHLAR_PR_BLOCKERS_URL="https://github.com/o/r/pull/7" \
    ASHLAR_GITHUB_TOKEN="${token}" ASHLAR_FAIL_ON_BLOCKED="${fob}" \
    ASHLAR_PR_BLOCKERS_TIMEOUT=3 \
    GITHUB_STEP_SUMMARY="${d}/summary" GITHUB_OUTPUT="${d}/output" \
    python3 "${CLIENT}" -- python3 "${STUB}" 2>&1)" || RC=$?
  SUMMARY="$(cat "${d}/summary")"
  OUTPUTS="$(cat "${d}/output")"
  LOG="${d}/log"
}

check() { if eval "$2"; then ok "$1"; else bad "$1" "$3"; fi; }

echo "== protocol exchange =="
run ready false "tok-123"
check "ready verdict exits 0" '[ "${RC}" -eq 0 ]' "rc=${RC}: ${OUT}"
check "the server is started with github.blockers exposed" \
  'grep -q "\"env.ExposedBrickIds__0\": \"github.blockers\"" "${LOG}"' "$(cat "${LOG}")"
check "initialize is sent first, with a protocol version" \
  '[ "$(sed -n 2p "${LOG}" | python3 -c "import json,sys;m=json.load(sys.stdin);print(m[\"method\"],bool(m[\"params\"][\"protocolVersion\"]))")" = "initialize True" ]' \
  "$(sed -n 2p "${LOG}")"
check "notifications/initialized follows initialize" \
  'sed -n 3p "${LOG}" | grep -q "\"notifications/initialized\""' "$(sed -n 3p "${LOG}")"
check "tools/call names github_blockers with the url" \
  'sed -n 4p "${LOG}" | grep -q "\"name\": \"github_blockers\"" && sed -n 4p "${LOG}" | grep -q "\"url\": \"https://github.com/o/r/pull/7\""' \
  "$(sed -n 4p "${LOG}")"
check "the token travels as githubToken" 'sed -n 4p "${LOG}" | grep -q "\"githubToken\": \"tok-123\""' "$(sed -n 4p "${LOG}")"
check "a server notification before the response is skipped" '[[ "${OUTPUTS}" == *"verdict=ready"* ]]' "${OUTPUTS}"
check "summary carries the brick's answer and audit id" \
  '[[ "${SUMMARY}" == *"**Ready** to merge."* && "${SUMMARY}" == *"audit-ready"* ]]' "${SUMMARY}"
check "outputs carry audit id and zero blockers" \
  '[[ "${OUTPUTS}" == *"audit-id=audit-ready"* && "${OUTPUTS}" == *"blocker-count=0"* ]]' "${OUTPUTS}"

run ready false ""
check "no token: githubToken is not sent" '! sed -n 4p "${LOG}" | grep -q githubToken' "$(sed -n 4p "${LOG}")"

echo "== verdicts =="
run blocked false
check "blocked without fail-on-blocked exits 0" '[ "${RC}" -eq 0 ]' "rc=${RC}: ${OUT}"
check "blocked verdict and count are output" \
  '[[ "${OUTPUTS}" == *"verdict=blocked"* && "${OUTPUTS}" == *"blocker-count=2"* ]]' "${OUTPUTS}"
check "summary shows the blocked verdict" '[[ "${SUMMARY}" == *"**blocked**"* ]]' "${SUMMARY}"

run blocked true
check "blocked with fail-on-blocked exits 1" '[ "${RC}" -eq 1 ]' "rc=${RC}: ${OUT}"
check "the failure names the blocker count" '[[ "${OUT}" == *"::error::2 blocker(s)"* ]]' "${OUT}"

run ready true
check "ready with fail-on-blocked still exits 0" '[ "${RC}" -eq 0 ]' "rc=${RC}: ${OUT}"

run structured false
check "structuredContent is read when the text block is not JSON" '[[ "${OUTPUTS}" == *"verdict=blocked"* ]]' "${OUTPUTS}"

echo "== no answer is never ready =="
for mode in tool-error rpc-error garbage exit silent no-verdict; do
  run "${mode}" false
  check "${mode}: exits 2" '[ "${RC}" -eq 2 ]' "rc=${RC}: ${OUT}"
  check "${mode}: verdict=error, not ready" '[[ "${OUTPUTS}" == "verdict=error" ]]' "${OUTPUTS}"
done

echo "== inputs =="
RC=0; OUT="$(ASHLAR_PR_BLOCKERS_URL="" python3 "${CLIENT}" -- python3 "${STUB}" 2>&1)" || RC=$?
check "a missing url exits 2 without starting the server" '[ "${RC}" -eq 2 ] && [[ "${OUT}" == *"No URL"* ]]' "rc=${RC}: ${OUT}"

TOTAL=$((PASS + FAIL))
echo
echo "passed: ${PASS}   failed: ${FAIL}"
if [ "${TOTAL}" -ne "${EXPECTED_ASSERTIONS}" ]; then
  echo "FAIL — ran ${TOTAL} assertions, expected ${EXPECTED_ASSERTIONS}: the file stopped early or the count is stale"
  exit 1
fi
[ "${FAIL}" -eq 0 ]
