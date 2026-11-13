#!/usr/bin/env bash
# Ordinary application contracts only; no service start or OS-isolation harness.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
expected_sha="${1:?expected full commit SHA required}"
[[ "$expected_sha" =~ ^[0-9a-f]{40}$ ]]
if [[ "$root" != /repo || ! -e /.dockerenv || ! -d /src-mirror/.git ]]; then
  echo 'Run through scripts/test-in-container.sh in its Linux devtest clone.' >&2
  exit 2
fi
cd "$root"
source_sha="$(git rev-parse HEAD)"
test "$source_sha" = "$expected_sha"
test -z "$(git status --porcelain)"
results="$(mktemp -d)"
finish() {
  original_status=$?
  trap - EXIT
  evidence_status=0
  python3 tools/GovernanceBrokerPrototype/ci-evidence.py pack \
    --results "$results" --expected-sha "$source_sha" || evidence_status=$?
  rm -rf "$results" || true
  if [[ "$original_status" -ne 0 ]]; then exit "$original_status"; fi
  exit "$evidence_status"
}
trap finish EXIT
# Require the selected runtime family instead of rolling net8 tests onto net10.
export DOTNET_ROLL_FORWARD=LatestPatch
dotnet --list-runtimes > "$results/runtimes.txt"
# Each invocation has a distinct TRX and receipt. Run both sequentially even if one
# build/test fails; the verifier retains their separate outcomes and fails closed.
set +e
dotnet test tools/GovernanceBrokerPrototype.Tests/GovernanceBrokerPrototype.Tests.csproj \
  --framework net8.0 -p:RollForward=LatestPatch --nologo --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=contracts-net8.0.trx' --results-directory "$results" \
  --logger 'console;verbosity=normal'
net8_status=$?
printf '%s\n' "$net8_status" > "$results/net8.0.exitcode"
dotnet test tools/GovernanceBrokerPrototype.Tests/GovernanceBrokerPrototype.Tests.csproj \
  --framework net10.0 -p:RollForward=LatestPatch --nologo --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=contracts-net10.0.trx' --results-directory "$results" \
  --logger 'console;verbosity=normal'
net10_status=$?
printf '%s\n' "$net10_status" > "$results/net10.0.exitcode"
set -e
for framework in net8.0 net10.0; do
  runtimeconfig="tools/GovernanceBrokerPrototype.Tests/bin/Debug/$framework/GovernanceBrokerPrototype.Tests.runtimeconfig.json"
  if [[ -f "$runtimeconfig" ]]; then
    cp "$runtimeconfig" "$results/runtimeconfig-$framework.json"
  fi
done
python3 tools/GovernanceBrokerPrototype/ci-evidence.py check \
  --results "$results" --expected-sha "$source_sha"
