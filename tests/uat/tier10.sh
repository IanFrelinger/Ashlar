#!/usr/bin/env bash
# Ashlar UAT — Tier 10 (resilience under concurrency).
#
# The product's central claim is not throughput, it is the record: "every artifact certified, every
# action on the record". A load test that only measured latency would miss the failure that would
# actually matter here -- a task that ran but whose audit entry was lost, duplicated, or attributed to
# the wrong id under concurrency. So this tier submits work in parallel and then audits the AUDIT:
# every task must appear exactly once, under its own id.
#
# It is a correctness-under-concurrency check, not a benchmark. Timings are recorded for information
# and nothing passes or fails on them, because a shared CI runner cannot support a latency claim.
set -uo pipefail

SRC="${UAT_REPO_DIR:-$PWD}"
OUT="${UAT_OUT:-${WORK:-/work}/out}"
# Eight is enough to have caught the defect this tier was written for (two was, in fact) and small
# enough to stay quick on a shared runner. This is a correctness check, so more parallelism buys
# nothing once the race is reachable.
CONCURRENCY="${UAT_CONCURRENCY:-8}"
mkdir -p "$OUT"
: >"$OUT/results-tier10.tsv"

PASS=0; FAIL=0
result() {
  local tier="$1" id="$2" verdict="$3"; shift 3
  printf 'RESULT\t%s\t%s\t%s\t%s\n' "$tier" "$id" "$verdict" "$*" | tee -a "$OUT/results-tier10.tsv"
  if [ "$verdict" = PASS ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); fi
}
say() { printf '\n=== %s ===\n' "$*"; }
cd "$SRC" || exit 1

unset ASPNETCORE_HTTP_PORTS
export ASHLAR_ALLOW_MOCK=1

pkill -f 'Ashlar\.API' 2>/dev/null
PORT_FREE=no
for _ in $(seq 1 30); do
  curl -s -o /dev/null --max-time 2 "http://localhost:5000/health" 2>/dev/null || { PORT_FREE=yes; break; }
  sleep 1
done
if [ "$PORT_FREE" != yes ]; then
  echo "ABORT: something is already serving http://localhost:5000; this tier starts the API itself." >&2
  exit 2
fi

dotnet run --project application/src/Ashlar.API -f net10.0 >"$OUT/api-load.log" 2>&1 &
API_PID=$!
for _ in $(seq 1 90); do
  grep -q 'Now listening on:' "$OUT/api-load.log" 2>/dev/null && break
  kill -0 $API_PID 2>/dev/null || break
  sleep 2
done
if ! grep -q 'Now listening on:' "$OUT/api-load.log"; then
  result 10 api-start FAIL "API did not start: $(tail -3 "$OUT/api-load.log" | tr '\n' ' ')"
  printf 'PASS=%d FAIL=%d\n' "$PASS" "$FAIL"; exit 1
fi
API=$(grep -m1 -oE 'Now listening on: *http://[^ ]+' "$OUT/api-load.log" \
      | sed 's/Now listening on: *//; s#0\.0\.0\.0#localhost#; s#\[::\]#localhost#')

say "10.1 submit $CONCURRENCY tasks at once"
T0=$(date +%s)
rm -f "$OUT/ids.txt" "$OUT/codes.txt"
SUBMIT_PIDS=""
for i in $(seq 1 "$CONCURRENCY"); do
  (
    body=$(curl -s --max-time 120 "$API/api/copilot/task" -H 'Content-Type: application/json' \
             -d "{\"task\": \"uat concurrent probe $i\", \"auditCount\": 1}")
    printf '%s\n' "$body" | grep -o '"taskId":"[^"]*"' | head -1 | cut -d'"' -f4 >>"$OUT/ids.txt"
    # success true is the silent placeholder receipt. A completed run reports the
    # no-work placeholder. A shared-agent crash does not: it says the container
    # cannot execute from state Executing, and it has no placeholder summary.
    if printf '%s\n' "$body" | grep -q '"success":false' \
       && printf '%s\n' "$body" | grep -q 'No specialized agent performed work' \
       && ! printf '%s\n' "$body" | grep -q 'cannot execute from state Executing'; then
      echo ok >>"$OUT/codes.txt"
    else
      echo bad >>"$OUT/codes.txt"
    fi
  ) &
  SUBMIT_PIDS="$SUBMIT_PIDS $!"
done
# Wait for the SUBMISSIONS, never a bare `wait`: the API is a background job of this same shell, so
# `wait` with no arguments waits for a server that is designed never to exit. That hung the CI job for
# twenty minutes and read as a product defect until the API's own log showed it idle and healthy.
for p in $SUBMIT_PIDS; do wait "$p" 2>/dev/null; done
ELAPSED=$(( $(date +%s) - T0 ))

SUBMITTED=$(grep -c . "$OUT/ids.txt" 2>/dev/null || echo 0)
OKS=$(grep -c '^ok$' "$OUT/codes.txt" 2>/dev/null || echo 0)

# Answered and recorded is what this tier gates on. Whether every orchestration SUCCEEDS is tracked
# separately below, because it currently does not -- see the known issue there.
if [ "$SUBMITTED" -eq "$CONCURRENCY" ]; then
  result 10 concurrent-submissions-all-answered PASS "$SUBMITTED/$CONCURRENCY returned a taskId (${ELAPSED}s wall clock, informational)"
else
  result 10 concurrent-submissions-all-answered FAIL "only $SUBMITTED/$CONCURRENCY returned a taskId"
fi

say "10.1b every one of those orchestrations must finish, not just be recorded"
# This gates now. It did not always: concurrent runs registered different containers under the same
# decomposition id (the parser's fallback emits "fallback-1" every time), so a run executed the other
# run's container and failed with "cannot execute from state Executing", while ShutdownAllAsync tore
# down both. Roughly half of every burst failed. LifecycleManager now scopes its agent map to the run
# via BeginRunScope. The mock fallback still performs no work, so each finished run is success false
# with the no-work summary. That is not the shared-agent crash. A regression here is that crash, or
# a body that is not the placeholder receipt.
if grep -q 'cannot execute from state Executing' "$OUT/api-load.log"; then
  result 10 concurrent-orchestrations-succeed FAIL "shared-agent regression: cannot execute from state Executing (api-load.log)"
elif [ "$OKS" -eq "$CONCURRENCY" ]; then
  result 10 concurrent-orchestrations-succeed PASS "$OKS/$CONCURRENCY finished as the no-work placeholder"
else
  result 10 concurrent-orchestrations-succeed FAIL "only $OKS/$CONCURRENCY were no-work placeholder receipts -- concurrent runs may be sharing agents again (look for 'cannot execute from state Executing' in api-load.log)"
fi

say "10.2 every id is distinct — no two concurrent tasks share an identity"
UNIQ=$(sort -u "$OUT/ids.txt" 2>/dev/null | grep -c .)
if [ "$UNIQ" -eq "$SUBMITTED" ] && [ "$SUBMITTED" -gt 0 ]; then
  result 10 task-ids-unique PASS "$UNIQ distinct ids from $SUBMITTED submissions"
else
  result 10 task-ids-unique FAIL "id collision under concurrency: $UNIQ distinct from $SUBMITTED submissions"
fi

say "10.3 THE claim under load: every task is on the record, exactly once"
# The audit trail is the product. A task that ran without a record, or with two, is the failure that
# matters most here -- and it is invisible to any check that only counts HTTP 200s.
HIST=$(curl -s --max-time 120 "$API/api/copilot/tasks")
printf '%s' "$HIST" >"$OUT/history.json"
MISSING=0; DUPED=0
while read -r id; do
  [ -n "$id" ] || continue
  n=$(printf '%s' "$HIST" | grep -o "$id" | grep -c .)
  [ "$n" -eq 0 ] && MISSING=$((MISSING+1))
  [ "$n" -gt 1 ] && DUPED=$((DUPED+1))
done <"$OUT/ids.txt"
if [ "$SUBMITTED" -gt 0 ] && [ "$MISSING" -eq 0 ] && [ "$DUPED" -eq 0 ]; then
  result 10 every-task-recorded-once PASS "all $SUBMITTED concurrent tasks appear exactly once in the history"
else
  result 10 every-task-recorded-once FAIL "audit trail damaged under concurrency: $MISSING missing, $DUPED duplicated"
fi

say "10.4 each record is retrievable under its own id"
BAD=0
while read -r id; do
  [ -n "$id" ] || continue
  c=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "$API/api/copilot/tasks/$id")
  [ "$c" = "200" ] || BAD=$((BAD+1))
done <"$OUT/ids.txt"
if [ "$SUBMITTED" -gt 0 ] && [ "$BAD" -eq 0 ]; then
  result 10 records-retrievable PASS "every one of $SUBMITTED ids resolves to its own stored record"
else
  result 10 records-retrievable FAIL "$BAD of $SUBMITTED ids did not resolve to a record"
fi

say "10.5 the host is still healthy after the burst"
H=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "$API/health")
R=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "$API/ready")
if [ "$H" = "200" ] && [ "$R" = "200" ]; then
  result 10 healthy-after-load PASS "/health=$H /ready=$R after $CONCURRENCY concurrent submissions"
else
  result 10 healthy-after-load FAIL "host degraded after the burst: /health=$H /ready=$R"
fi

kill $API_PID 2>/dev/null; wait $API_PID 2>/dev/null; pkill -f 'Ashlar\.API' 2>/dev/null

say "summary"
printf 'PASS=%d FAIL=%d\n' "$PASS" "$FAIL" | tee "$OUT/summary-tier10.txt"
[ "$FAIL" -eq 0 ]
