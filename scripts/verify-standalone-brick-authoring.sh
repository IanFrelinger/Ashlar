#!/usr/bin/env bash
# Verifies `ashlar new brick` works from a tool install outside a Ashlar checkout.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${ASHLAR_AUTHORING_VERIFY_VERSION:-9.9.9-local}"
WORK="${ASHLAR_AUTHORING_VERIFY_WORK:-$(mktemp -d)}"
FEED="${WORK}/feed"
TOOL_PATH="${WORK}/tools"
BRICK_OUT="${WORK}/standalone"
RESULTS="${ASHLAR_AUTHORING_VERIFY_RESULTS:-${WORK}/results}"

mkdir -p "${FEED}" "${TOOL_PATH}" "${BRICK_OUT}" "${RESULTS}"

pack() {
  local project="$1"
  dotnet pack "${ROOT}/${project}" \
    -c Release \
    -o "${FEED}" \
    -p:PackageVersion="${VERSION}" \
    -p:IncludeTestProjectReferences=false \
    -v minimal
}

bash "${ROOT}/scripts/pack-ashlar-hosting-graph.sh" "${VERSION}" "${FEED}"
pack src/Ashlar.Adapters.Models/Ashlar.Adapters.Models.csproj
pack src/Ashlar.Bricks.Owasp/Ashlar.Bricks.Owasp.csproj
pack src/Ashlar.BackgroundAgents.HostRunners/Ashlar.BackgroundAgents.HostRunners.csproj
pack src/Ashlar.Policies.Dev/Ashlar.Policies.Dev.csproj
pack src/Ashlar.Authoring/Ashlar.Authoring.csproj
pack application/src/Ashlar.CLI/Ashlar.CLI.csproj

dotnet tool install \
  --tool-path "${TOOL_PATH}" \
  Ashlar.CLI \
  --version "${VERSION}" \
  --add-source "${FEED}" \
  --ignore-failed-sources

"${TOOL_PATH}/ashlar" new brick SampleThing \
  --output "${BRICK_OUT}" \
  --ashlar-version "${VERSION}" \
  --json

bash "${ROOT}/scripts/verify-consumer-repo-paths.sh" "${BRICK_OUT}" \
  2>&1 | tee "${RESULTS}/repo-relative-paths.log"

dotnet restore "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  --source "${FEED}" \
  --source https://api.nuget.org/v3/index.json

dotnet build "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  --no-restore \
  -v minimal

dotnet test "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  --no-build \
  -f net8.0 --logger 'trx;LogFileName=scaffold.trx' --results-directory "${RESULTS}" \
  --blame-hang-timeout 120s \
  --blame-hang-dump-type none
bash "${ROOT}/scripts/ci/zero-test-guard.sh" "${RESULTS}/scaffold.trx"

# Develop something useful through the installed public packages, outside this repository.
# These are reviewed fixtures, never generated/model-supplied commands or patches.
cp "${ROOT}/tools/ci-agents/fixtures/SampleThingBrick.cs" "${BRICK_OUT}/SampleThingBrick/SampleThingBrick.cs"
cp "${ROOT}/tools/ci-agents/fixtures/SampleThingBrickTests.cs" "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrickTests.cs"
dotnet test "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  -f net8.0 --logger 'trx;LogFileName=developer-edit.trx' --results-directory "${RESULTS}" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
bash "${ROOT}/scripts/ci/zero-test-guard.sh" "${RESULTS}/developer-edit.trx"

echo "verify-standalone-brick-authoring: OK (${WORK})"
