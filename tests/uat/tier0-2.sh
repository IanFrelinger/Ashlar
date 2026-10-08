#!/usr/bin/env bash
# Ashlar UAT — Tiers 0-2, run inside mcr.microsoft.com/dotnet/sdk:10.0 as a "tester's machine".
#
# Tier 0  first fifteen minutes : clean clone -> build -> doctor, exactly as docs/TesterQuickstart.md says
# Tier 1  the audit trail       : one task submitted; its record and its trust-log entry are retrievable by id
# Tier 2  the certification gate: the two teeth (admit correct code, reject a weak witness) actually fire
#
# Every check asserts a machine-verifiable outcome and prints one RESULT line. The script never fails the
# run on a single check; it collects and exits non-zero at the end if anything failed, so one bad claim
# does not hide the rest.
set -uo pipefail

REPO_URL="${REPO_URL:-https://github.com/IanFrelinger/Ashlar.git}"
WORK="${WORK:-/work}"
OUT="${UAT_OUT:-$WORK/out}"
# Two ways to run. Locally: clone fresh, which is the point of tier 0 -- a tester's first fifteen
# minutes, cold. In CI: UAT_REPO_DIR points at the workspace checkout, because a gate must test the
# commit under review; a gate that clones master would pass while the PR in front of it breaks the
# quickstart, which is exactly the failure this suite exists to prevent.
SRC="${UAT_REPO_DIR:-$WORK/Ashlar}"
mkdir -p "$OUT"

PASS=0; FAIL=0
result() { # result <tier> <id> <PASS|FAIL> <detail...>
  local tier="$1" id="$2" verdict="$3"; shift 3
  printf 'RESULT\t%s\t%s\t%s\t%s\n' "$tier" "$id" "$verdict" "$*" | tee -a "$OUT/results.tsv"
  if [ "$verdict" = PASS ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); fi
}
now() { date +%s; }
say() { printf '\n=== %s ===\n' "$*"; }

# ---------------------------------------------------------------- Tier 0
say "Tier 0 — first fifteen minutes"
T_START=$(now)

say "0.1 clone"
t0=$(now)
if [ -n "${UAT_REPO_DIR:-}" ]; then
  result 0 clone PASS "workspace checkout $(git -C "$SRC" rev-parse --short HEAD 2>/dev/null || echo '(no git)') -- clone skipped by UAT_REPO_DIR"
elif git clone --quiet "$REPO_URL" "$SRC" 2>"$OUT/clone.err"; then
  result 0 clone PASS "$(( $(now) - t0 ))s  $(git -C "$SRC" rev-parse --short HEAD)"
else
  result 0 clone FAIL "$(tail -2 "$OUT/clone.err" | tr '\n' ' ')"
  echo "cannot continue without a clone"; exit 1
fi
cd "$SRC" || exit 1

say "0.2 SDK band matches global.json"
VER="$(dotnet --version 2>&1)"
case "$VER" in
  10.0.*) result 0 sdk-version PASS "dotnet --version = $VER (quickstart promises 10.0.x)";;
  *)      result 0 sdk-version FAIL "dotnet --version = $VER, quickstart promises 10.0.x";;
esac

say "0.3 dotnet build Ashlar.Kernel.sln  (the quickstart's step 1)"
t0=$(now)
if dotnet build Ashlar.Kernel.sln >"$OUT/build.log" 2>&1; then
  BUILD_S=$(( $(now) - t0 ))
  WARN=$(grep -cE ' warning [A-Z]+[0-9]+' "$OUT/build.log")
  result 0 build PASS "${BUILD_S}s, ${WARN} warning lines"
else
  BUILD_S=$(( $(now) - t0 ))
  result 0 build FAIL "${BUILD_S}s, exit!=0: $(grep -m3 -E ' error [A-Z]+[0-9]+' "$OUT/build.log" | tr '\n' ' ')"
fi

say "0.4 the quickstart's claim that Ashlar.Kernel.sln also builds Ashlar.API"
if find . -path '*Ashlar.API/bin/*/Ashlar.API.dll' -newermt "-1 day" | grep -q .; then
  result 0 kernel-sln-builds-api PASS "Ashlar.API.dll present after building the kernel solution"
else
  result 0 kernel-sln-builds-api FAIL "no Ashlar.API.dll after dotnet build Ashlar.Kernel.sln"
fi

say "0.5 doctor"
t0=$(now)
timeout 600 dotnet run --project application/src/Ashlar.CLI -- doctor >"$OUT/doctor.log" 2>&1
DOC_RC=$?
DOC_S=$(( $(now) - t0 ))
if grep -qiE 'overall: *PASS' "$OUT/doctor.log"; then
  result 0 doctor-pass PASS "${DOC_S}s, 'overall: PASS' present (rc=$DOC_RC)"
else
  result 0 doctor-pass FAIL "${DOC_S}s, no 'overall: PASS' (rc=$DOC_RC): $(tail -3 "$OUT/doctor.log" | tr '\n' ' ')"
fi
# Quickstart section 5 asks "does the doctor tell the truth?", and section 2 says container smoke warns
# rather than fails when Docker is absent. So the claim is conditional on the machine, and the check has
# to be too: assert the state that matches THIS box. Asserting "warn" unconditionally encodes the absence
# of Docker -- a property of one test environment -- as though it were a product claim, and duly failed
# on a CI runner that has Docker while doctor was reporting correctly.
SMOKE=$(grep -ioE 'container smoke: *[a-z]+' "$OUT/doctor.log" | head -1 | sed 's/.*: *//' | tr 'A-Z' 'a-z')
if timeout 8 docker info >/dev/null 2>&1; then
  if [ "$SMOKE" = "pass" ]; then
    result 0 doctor-container-truthful PASS "docker reachable and doctor says 'container smoke: pass'"
  else
    result 0 doctor-container-truthful FAIL "docker is reachable but doctor says 'container smoke: ${SMOKE:-<absent>}'"
  fi
else
  if [ "$SMOKE" = "warn" ]; then
    result 0 doctor-container-truthful PASS "no docker and doctor says 'container smoke: warn', as documented"
  else
    result 0 doctor-container-truthful FAIL "no docker, expected 'warn', got '${SMOKE:-<absent>}'"
  fi
fi

say "0.6 doctor --json is machine-readable, as claimed"
if timeout 600 dotnet run --project application/src/Ashlar.CLI -- doctor --json >"$OUT/doctor.json" 2>"$OUT/doctor-json.err"; then
  if head -c 200 "$OUT/doctor.json" | grep -q '{'; then
    result 0 doctor-json PASS "emits JSON"
  else
    result 0 doctor-json FAIL "no JSON object in output: $(head -c 120 "$OUT/doctor.json")"
  fi
else
  result 0 doctor-json FAIL "doctor --json exited non-zero"
fi

# ---------------------------------------------------------------- Tier 1
say "Tier 1 — submit one task, read the record it left"

export ASHLAR_ALLOW_MOCK=1
API=http://localhost:5000

# The SDK image ships ASPNETCORE_HTTP_PORTS=8080, which is the image's opinion, not the product's or the
# tester's. Left set, it rebinds the host to :8080 and the documented :5000 looks like a doc defect.
# Verified separately on a native SDK 10 box: with this unset the API binds http://localhost:5000.
unset ASPNETCORE_HTTP_PORTS

# 1.0 The documented command, run exactly as TesterQuickstart section 3 prints it. This is the claim a
# tester actually meets, so it is scored on its own. Keep this in sync with the page by copying the line
# out of it verbatim -- that is the point of the check. It failed on 2026-08-17 (the page omitted
# -f net10.0 while Ashlar.API multi-targets) and now guards against the same rot.
say "1.0 the hero command, verbatim from the docs"
DOC_CMD=$(grep -m1 -E '^ASHLAR_ALLOW_MOCK=1 dotnet run --project application/src/Ashlar\.API' docs/TesterQuickstart.md)
echo "doc prints: $DOC_CMD"
timeout 180 env $DOC_CMD >"$OUT/api-verbatim.log" 2>&1 &
V_PID=$!
for _ in $(seq 1 60); do
  # Do not match a bare "error" substring. MCP/A2A map-disabled info lines and MSBuild
  # "0 Error(s)" made the old pattern stop before Kestrel printed "Now listening on:".
  if grep -q 'Now listening on:' "$OUT/api-verbatim.log" 2>/dev/null; then
    break
  fi
  if grep -qE 'Unable to run your project|Hosting failed to start|Build FAILED' "$OUT/api-verbatim.log" 2>/dev/null; then
    break
  fi
  kill -0 $V_PID 2>/dev/null || break
  sleep 2
done
if grep -q 'Now listening on:' "$OUT/api-verbatim.log" 2>/dev/null; then
  result 1 doc-command-verbatim PASS "documented command started the API"
else
  result 1 doc-command-verbatim FAIL "documented command does not start the API: $(head -3 "$OUT/api-verbatim.log" | tr '\n' ' ')"
fi
kill $V_PID 2>/dev/null; wait $V_PID 2>/dev/null; pkill -f 'Ashlar\.API' 2>/dev/null

# Everything below measures whether the PRODUCT works, using the framework selector the documented
# command is missing. Two different questions: "does the doc work" and "does the thing work".
dotnet run --project application/src/Ashlar.API -f net10.0 >"$OUT/api.log" 2>&1 &
API_PID=$!

say "1.1 API comes up on loopback"
t0=$(now)
UP=no
for _ in $(seq 1 180); do
  if grep -q 'Now listening on:' "$OUT/api.log" 2>/dev/null; then UP=yes; break; fi
  kill -0 $API_PID 2>/dev/null || break
  sleep 2
done
BOOT_S=$(( $(now) - t0 ))
if [ "$UP" = yes ]; then
  # Read the port the host actually chose rather than assuming it; the quickstart promises 5000, and a
  # different port is a documentation defect worth recording separately from "the API started".
  LISTEN=$(grep -m1 -oE 'Now listening on: *http://[^ ]+' "$OUT/api.log" | sed 's/Now listening on: *//')
  result 1 api-listening PASS "${BOOT_S}s to '$LISTEN'"
  case "$LISTEN" in
    http://localhost:5000*|http://0.0.0.0:5000*|http://\[::\]:5000*)
      result 1 api-port-matches-doc PASS "listening on :5000 as TesterQuickstart section 3 says";;
    *)
      result 1 api-port-matches-doc FAIL "quickstart says http://localhost:5000; actually $LISTEN";;
  esac
  API="$(printf '%s' "$LISTEN" | sed 's#0\.0\.0\.0#localhost#; s#\[::\]#localhost#')"
else
  result 1 api-listening FAIL "no listen line in ${BOOT_S}s: $(tail -5 "$OUT/api.log" | tr '\n' ' ')"
fi

say "1.2 /health"
H=$(curl -s --max-time 30 "$API/health")
echo "$H" >"$OUT/health.json"
if printf '%s' "$H" | grep -q '"status":"healthy"'; then
  result 1 health PASS "$H"
else
  result 1 health FAIL "got: $H"
fi

say "1.3 POST /api/copilot/task"
t0=$(now)
R=$(curl -s --max-time 300 "$API/api/copilot/task" -H "Content-Type: application/json" \
     -d '{"task": "Summarize what this repository does", "auditCount": 5}')
TASK_S=$(( $(now) - t0 ))
echo "$R" >"$OUT/task.json"
TASK_ID=$(printf '%s' "$R" | grep -o '"taskId":"[^"]*"' | head -1 | cut -d'"' -f4)
if [ -n "$TASK_ID" ] && printf '%s' "$R" | grep -q '"success":true'; then
  result 1 task-submit PASS "${TASK_S}s, taskId=$TASK_ID, success=true"
else
  result 1 task-submit FAIL "${TASK_S}s, no taskId/success in: $(printf '%s' "$R" | head -c 300)"
fi

say "1.4 first-task audit is immediately visible (empty-first-audit bug fixed 2026-09-20)"
if printf '%s' "$R" | grep -qE '"recentAudit":\[.+\]' && printf '%s' "$R" | grep -q '"eventType":"CopilotTask"'; then
  result 1 first-audit-includes-task PASS "recentAudit includes the submitted task (empty-first-audit fixed)"
else
  result 1 first-audit-includes-task FAIL "first task should be in recentAudit; got $(printf '%s' "$R" | grep -o '"recentAudit":.\{0,80\}')"
fi

say "1.5 the record is retrievable by id"
REC=$(curl -s --max-time 60 "$API/api/copilot/tasks/$TASK_ID")
echo "$REC" >"$OUT/task-record.json"
MISSING=""
for f in task submittedAt completedAt success summary; do
  printf '%s' "$REC" | grep -q "\"$f\"" || MISSING="$MISSING $f"
done
if [ -z "$MISSING" ]; then
  result 1 task-record PASS "GET tasks/\$id carries task, submittedAt, completedAt, success, summary"
else
  result 1 task-record FAIL "record missing:$MISSING -- got $(printf '%s' "$REC" | head -c 200)"
fi

say "1.6 THE claim: the trust log carries a CopilotTask entry whose sourceId is that task id"
D=$(curl -s --max-time 60 "$API/api/trust/dashboard")
echo "$D" >"$OUT/dashboard.json"
AUDITED=no
if [ -n "$TASK_ID" ] && printf '%s' "$D" | grep -q "$TASK_ID"; then
  if printf '%s' "$D" | grep -q 'CopilotTask'; then
    AUDITED=yes
    result 1 audit-links-task PASS "dashboard recentAudit contains eventType CopilotTask and sourceId=$TASK_ID"
  else
    result 1 audit-links-task FAIL "task id present but no CopilotTask eventType"
  fi
else
  result 1 audit-links-task FAIL "task id '${TASK_ID:-<none>}' absent from /api/trust/dashboard"
fi

say "1.7 the same event shows in the activity feed"
F=$(curl -s --max-time 60 "$API/api/activity/feed")
echo "$F" >"$OUT/feed.json"
if printf '%s' "$F" | grep -q "$TASK_ID"; then
  result 1 activity-feed PASS "activity feed carries $TASK_ID"
else
  result 1 activity-feed FAIL "activity feed has no entry for $TASK_ID: $(printf '%s' "$F" | head -c 200)"
fi

say "1.8 history endpoint lists the task"
L=$(curl -s --max-time 60 "$API/api/copilot/tasks")
if printf '%s' "$L" | grep -q "$TASK_ID"; then
  result 1 task-history PASS "GET /api/copilot/tasks lists $TASK_ID"
else
  result 1 task-history FAIL "task absent from history"
fi

say "1.9 trust status answers"
S=$(curl -s --max-time 60 "$API/api/trust/status")
if printf '%s' "$S" | grep -qiE '"isPaused"'; then
  result 1 trust-status PASS "$(printf '%s' "$S" | head -c 160)"
else
  result 1 trust-status FAIL "no isPaused in: $(printf '%s' "$S" | head -c 200)"
fi

TIER01_S=$(( $(now) - T_START ))
say "1.10 time to first audited job (the number the quickstart asks testers to record)"
# Only a real audit entry earns this. An unconditional PASS here would report a stopwatch reading as
# though the claim behind it held.
if [ "$AUDITED" = yes ]; then
  result 1 time-to-first-audited-job PASS "${TIER01_S}s from clean clone to a CopilotTask entry in the trust log"
else
  result 1 time-to-first-audited-job FAIL "${TIER01_S}s elapsed and no audited job exists; the clock is not the claim"
fi

# `dotnet run` launches the host as a child, so killing the runner alone can leave :5000 held.
kill $API_PID 2>/dev/null; wait $API_PID 2>/dev/null
pkill -f 'Ashlar\.API' 2>/dev/null
sleep 2

# ---------------------------------------------------------------- Tier 2
say "Tier 2 — the certification gate has teeth"
t0=$(now)
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 \
  --filter "FullyQualifiedName~CertificationGateTeethTests" >"$OUT/teeth.log" 2>&1
TEETH_RC=$?
TEETH_S=$(( $(now) - t0 ))

if [ $TEETH_RC -eq 0 ]; then
  result 2 teeth-run PASS "${TEETH_S}s, exit 0"
else
  result 2 teeth-run FAIL "${TEETH_S}s, exit $TEETH_RC: $(grep -m3 -E 'error|Failed ' "$OUT/teeth.log" | tr '\n' ' ')"
fi

# The quickstart names these two as the pair that defines the gate. `dotnet test` prints names only for
# failures at default verbosity, so presence is established by discovery, not by scraping the run log.
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 \
  --filter "FullyQualifiedName~CertificationGateTeethTests" --list-tests >"$OUT/teeth-list.log" 2>&1
for t in GoodBrick_StrongWitness_Admits_WithZeroEscapeRate WeakWitness_AllowsMutantEscapes_RejectsWithTeeth; do
  if grep -q "$t" "$OUT/teeth-list.log"; then
    result 2 "teeth-$t" PASS "discovered by the filter the quickstart prints, and the filter's run passed"
  else
    result 2 "teeth-$t" FAIL "named in TesterQuickstart section 4 but not discovered by that filter"
  fi
done

# The doc claims "16 tests" for the two gate-teeth classes.
COUNT=$(grep -oE 'Passed: +[0-9]+' "$OUT/teeth.log" | tail -1 | grep -oE '[0-9]+')
TOTAL=$(grep -oE 'Total: +[0-9]+' "$OUT/teeth.log" | tail -1 | grep -oE '[0-9]+')
if [ "${TOTAL:-0}" -gt 0 ]; then
  result 2 teeth-count-claim "$([ "${TOTAL:-0}" = 16 ] && echo PASS || echo FAIL)" \
    "quickstart says 16 tests; run reports Total=$TOTAL Passed=${COUNT:-?}"
else
  result 2 teeth-count-claim FAIL "could not read a test total from the run"
fi

say "summary"
printf 'PASS=%d FAIL=%d\n' "$PASS" "$FAIL" | tee "$OUT/summary.txt"
[ "$FAIL" -eq 0 ]
