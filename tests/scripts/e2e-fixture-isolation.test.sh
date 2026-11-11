#!/usr/bin/env bash
# Execute the real fresh() helper, not a test reimplementation. No host dotnet.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${E2E_LOOP_SCRIPT:-$ROOT/scripts/e2e-loop.sh}"
TMP=$(mktemp -d) || exit 1
trap 'rm -rf "$TMP"' EXIT
PASS=0; FAIL=0
ok() { PASS=$((PASS + 1)); printf 'PASS %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf 'FAIL %s\n' "$1"; }
FUNC=$(sed -n '/^fresh() {/,/^}/p' "$SCRIPT")
if [[ -z "$FUNC" ]]; then echo 'FAIL fresh() helper not found' >&2; exit 1; fi
eval "$FUNC"

WORK="$TMP/fixtures with spaces"
mkdir -p "$WORK" || exit 1
# Resetting RANDOM makes the old allocator collide deterministically. The new
# allocator must remain unique even when two callers have identical shell RNG state.
first=$(RANDOM=7; fresh)
printf 'mode: proposing\n' > "$first/ashlar.policy.yaml"
second=$(RANDOM=7; fresh)
if [[ "$first" != "$second" && -d "$second" && ! -e "$second/ashlar.policy.yaml" ]]; then
  ok 'identical shell RNG state cannot reuse existing project state'
else
  bad 'identical shell RNG state cannot reuse existing project state'
fi
if [[ "$(cat "$first/ashlar.policy.yaml")" == 'mode: proposing' ]]; then
  ok 'existing policy is untouched'
else
  bad 'existing policy is untouched'
fi

# A file cannot contain fixture directories, independent of permissions/root UID.
WORK="$TMP/not-a-directory"
printf 'occupied\n' > "$WORK"
output=$(fresh 2>"$TMP/allocation.err"); rc=$?
if [[ "$rc" -ne 0 ]]; then ok 'allocation error propagates'; else bad 'allocation error propagates'; fi
if [[ -z "$output" ]]; then ok 'allocation error publishes no path'; else bad 'allocation error publishes no path'; fi

if [[ "${1:-}" != --helper-only ]]; then
  # Exercise the real suite's first allocation boundary with failure injection.
  # The only executable named dotnet is a fixture shim: builds are no-ops and any
  # CLI invocation writes a marker. No installed SDK or CLI is executed.
  mkdir -p "$TMP/bin" "$TMP/run" "$TMP/fallback" || exit 1
  REAL_MKTEMP=$(command -v mktemp)
  export REAL_MKTEMP
  export E2E_FIXTURE_TEST_TMP="$TMP"
  cat > "$TMP/bin/mktemp" <<'EOF'
#!/usr/bin/env bash
template="${!#}"
case "${template##*/}" in
  ashlar-e2e.*)
    if [[ "${E2E_FAIL_WORK:-0}" == 1 ]]; then
      printf '%s\n' "$E2E_FIXTURE_TEST_TMP/fallback"
      exit 42
    fi
    exec "$REAL_MKTEMP" "$@"
    ;;
  *) printf '%s\n' "$E2E_FIXTURE_TEST_TMP/fallback"; exit 42 ;;
esac
EOF
  cat > "$TMP/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf 'dotnet invocation\n' >> "$E2E_FIXTURE_TEST_TMP/dotnet-invoked"
if [[ "${1:-}" != build ]]; then
  printf 'unexpected invocation\n' >> "$E2E_FIXTURE_TEST_TMP/cli-invoked"
fi
exit 0
EOF
  chmod +x "$TMP/bin/mktemp" "$TMP/bin/dotnet"
  PATH="$TMP/bin:$PATH" RUNNER_TEMP="$TMP/run" E2E_FAIL_WORK=0 bash "$SCRIPT" >"$TMP/suite.log" 2>&1
  rc=$?
  if [[ "$rc" -ne 0 ]]; then ok 'suite stops on allocation error without errexit'; else bad 'suite stops on allocation error without errexit'; fi
  if [[ ! -e "$TMP/cli-invoked" ]]; then ok 'failed allocation never reaches a CLI invocation'; else bad 'failed allocation never reaches a CLI invocation'; fi
  # WORK failure must stop even before the build. Return an existing path alongside
  # the failure so removing the guard cannot be masked by results.tsv creation failing.
  rm -f "$TMP/dotnet-invoked"
  PATH="$TMP/bin:$PATH" RUNNER_TEMP="$TMP/run" E2E_FAIL_WORK=1 bash "$SCRIPT" >"$TMP/work-failure.log" 2>&1
  rc=$?
  if [[ "$rc" -ne 0 ]]; then ok 'suite stops on WORK allocation error'; else bad 'suite stops on WORK allocation error'; fi
  if [[ ! -e "$TMP/dotnet-invoked" ]]; then ok 'failed WORK allocation never reaches build or CLI'; else bad 'failed WORK allocation never reaches build or CLI'; fi
  EXPECTED=8
else
  EXPECTED=4
fi
printf 'e2e fixture isolation: %s passed, %s failed\n' "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 && "$PASS" -eq "$EXPECTED" ]]
