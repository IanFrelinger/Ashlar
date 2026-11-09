#!/usr/bin/env bash
# CI-only: the same native Linux .NET + Docker setup as Ashlar's existing CI.
set -euo pipefail

SOURCE=$(realpath "${1:?usage: run-dogfood.sh SOURCE OUTPUT}")
OUTPUT=$(realpath -m "${2:?usage: run-dogfood.sh SOURCE OUTPUT}")
EXPECTED_SHA=7ee90d6b666aadb09105a630b95e8f48311a2b41
test "$(git -C "$SOURCE" rev-parse HEAD)" = "$EXPECTED_SHA"
test -z "$(git -C "$SOURCE" status --porcelain)"
mkdir -p "$OUTPUT"
FIXTURE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
HOST=$(mktemp -d "${RUNNER_TEMP:?CI RUNNER_TEMP must be set}/ashlar-dogfood.XXXXXX")
cp "$FIXTURE/Dogfood.csproj" "$FIXTURE/Program.cs" "$HOST/"

docker version > "$OUTPUT/docker-version.txt"
docker pull python:3.13-slim 2>&1 | tee "$OUTPUT/base-pull.log"
BASE_REF=$(docker image inspect python:3.13-slim --format '{{index .RepoDigests 0}}')
test -n "$BASE_REF"
printf '%s\n' "$BASE_REF" > "$OUTPUT/base-image.txt"
docker build --build-arg "BASE_IMAGE=$BASE_REF" --iidfile "$OUTPUT/image-id.txt" "$FIXTURE" \
  2>&1 | tee "$OUTPUT/image-build.log"
IMAGE_ID=$(cat "$OUTPUT/image-id.txt")
docker image inspect "$IMAGE_ID" > "$OUTPUT/image-inspection.json"
printf '%s\n' "$EXPECTED_SHA" > "$OUTPUT/source-sha.txt"

dotnet build "$HOST/Dogfood.csproj" --nologo -v minimal -p:AshlarSource="$SOURCE" \
  2>&1 | tee "$OUTPUT/build.log"
dotnet "$HOST/bin/Debug/net10.0/Dogfood.dll" "$IMAGE_ID" \
  2> >(tee "$OUTPUT/consumer-stderr.log" >&2) | tee "$OUTPUT/result.json"
python3 - "$OUTPUT/result.json" <<'PY'
import json
import sys
result = json.load(open(sys.argv[1], encoding="utf-8"))
assert result["verdict"] == "passed"
assert result["validInvocations"] == 2
assert result["rejectedInvocations"] == 1
assert result["invalidInputExitCode"] == 2
assert result["maxOutputBytes"] == 4096
assert len(set(result["removedSessionIds"])) == 3
print("DOGFOOD PASSED: two typed JSON results, one parser rejection, three containers removed")
PY
test -z "$(git -C "$SOURCE" status --porcelain)"
