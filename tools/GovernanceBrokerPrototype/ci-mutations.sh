#!/usr/bin/env bash
# Ordinary application/metadata mutations only; no service or isolation harness.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
expected_sha="${1:?expected full commit SHA required}"
[[ "$expected_sha" =~ ^[0-9a-f]{40}$ ]]
if [[ "$root" != /repo || ! -e /.dockerenv || ! -d /src-mirror/.git ]]; then
  echo 'Run through scripts/test-in-container.sh in its Linux devtest clone.' >&2
  exit 2
fi
cd "$root"
test "$(git rev-parse HEAD)" = "$expected_sha"
test -z "$(git status --porcelain)"
# The project also pins LatestPatch so the unchanged generic runner cannot use
# Directory.Build.targets' fallback to roll net8 tests onto net10.
export DOTNET_ROLL_FORWARD=LatestPatch
exec python3 scripts/run-spec007-mutation-batch.py \
  --manifest tools/GovernanceBrokerPrototype/mutations.json --archive-stdout --timeout-seconds 300
