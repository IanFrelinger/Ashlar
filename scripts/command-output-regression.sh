#!/usr/bin/env bash
# Run inside scripts/test-in-container.sh against the committed candidate.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
RESULTS="$(mktemp -d)"
trap 'rm -rf "$RESULTS"' EXIT

ASHLAR_ALLOW_MOCK=1 dotnet test application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj \
  --framework net10.0 \
  --filter 'FullyQualifiedName~CommandExitCodeContractTests|FullyQualifiedName~JsonOutputContractTests|FullyQualifiedName~EgressCliExitCodeTests|FullyQualifiedName~RootCommandRegistrationTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=command-contracts.trx' --results-directory "$RESULTS" \
  --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/command-contracts.trx"

# Pin every selected class, so an empty/stale filter or a skipped case cannot look green.
python3 - "$RESULTS/command-contracts.trx" <<'PY'
from collections import Counter
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
expected = {
    "CommandExitCodeContractTests": 27,
    "JsonOutputContractTests": 38,
    "DockerJsonOutputContractTests": 4,
    "EgressCliExitCodeTests": 7,
    "RootCommandRegistrationTests": 11,
}
identities = {}
for test in root.findall(".//{*}UnitTest"):
    method = test.find("{*}TestMethod")
    identities[test.get("id")] = method.get("className", "").split(",")[0].rsplit(".", 1)[-1]
observed = Counter()
for result in root.findall(".//{*}UnitTestResult"):
    assert result.get("outcome") == "Passed", result.attrib
    observed[identities[result.get("testId")]] += 1
assert dict(observed) == expected, (dict(observed), expected)
print(f"PASS command contracts: {sum(observed.values())} executed and passed; no skipped cases")
PY

# These net8 test hosts launch the already-built net10 CLI through CliRunner. Never
# rebuild the CLI graph from inside a running Infrastructure test host.
CLI_DLL="application/src/Ashlar.CLI/bin/Debug/net10.0/Ashlar.CLI.dll"
if [[ ! -f "$CLI_DLL" ]]; then
  echo "Missing prebuilt net10 CLI: $CLI_DLL" >&2
  exit 1
fi
ASHLAR_ALLOW_MOCK=1 ASHLAR_CLI_FORCE_REBUILD=0 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  --framework net8.0 \
  --filter 'FullyQualifiedName=Ashlar.Tests.Infrastructure.Tests.CLI.Phases14CliE2ETests.Phases14_FullPipeline_ObserveAnalyzeAdaptImprove|FullyQualifiedName=Ashlar.Tests.Infrastructure.Tests.CLI.FullPipelineE2ETests.ObserveAnalyzeAdaptImproveSelfContext_Sequential_NoCrash|FullyQualifiedName=Ashlar.Tests.Infrastructure.Tests.Certification.LaneBlameWindowConventionTests.Every_lane_gives_its_slowest_selected_test_room_to_time_out' \
  --blame-hang-timeout 180s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=command-pipelines.trx' --results-directory "$RESULTS" \
  --nologo -v minimal
bash scripts/ci/zero-test-guard.sh "$RESULTS/command-pipelines.trx"

python3 - "$RESULTS/command-pipelines.trx" <<'PY'
from collections import Counter
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
expected = {
    "Phases14CliE2ETests.Phases14_FullPipeline_ObserveAnalyzeAdaptImprove": 1,
    "FullPipelineE2ETests.ObserveAnalyzeAdaptImproveSelfContext_Sequential_NoCrash": 1,
    "LaneBlameWindowConventionTests.Every_lane_gives_its_slowest_selected_test_room_to_time_out": 1,
}
identities = {}
for test in root.findall(".//{*}UnitTest"):
    method = test.find("{*}TestMethod")
    class_name = method.get("className", "").split(",")[0].rsplit(".", 1)[-1]
    identities[test.get("id")] = class_name + "." + method.get("name", "")
observed = Counter()
for result in root.findall(".//{*}UnitTestResult"):
    assert result.get("outcome") == "Passed", result.attrib
    observed[identities[result.get("testId")]] += 1
assert dict(observed) == expected, (dict(observed), expected)
print("PASS command pipelines and deadline convention: 3 executed and passed; no skipped cases")
PY
