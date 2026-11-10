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
    "JsonOutputContractTests": 35,
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
