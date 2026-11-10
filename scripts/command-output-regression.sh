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
