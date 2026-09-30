#!/usr/bin/env bash
# Local reproduction of the cert-gate CI job (hermetic certification + generation tests).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

# shellcheck source=scripts/cert-gate-config.sh
source "${ROOT}/scripts/cert-gate-config.sh"

FILTER="${CERT_GATE_FILTER}"
RESULTS_DIR="${ROOT}/test-results"
TRX="${RESULTS_DIR}/cert-gate.trx"

# Hermetic tests: do not use portability NuGet config (forces Roslyn path in GeneratedBrickBuilder).
unset ASHLAR_CERT_NUGET_CONFIG

mkdir -p "${RESULTS_DIR}"

echo "== cert-gate: restore =="
dotnet restore src/Ashlar.Tests.Infrastructure/scripts/copy-assemblies.csproj
dotnet restore src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj

echo "== cert-gate: build =="
dotnet build src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --no-restore -v minimal

echo "== cert-gate: test =="
set +e
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  -f net8.0 \
  --no-build \
  --filter "${FILTER}" \
  --logger "trx;LogFileName=cert-gate.trx" \
  --logger "console;verbosity=normal" \
  --results-directory "${RESULTS_DIR}"
TEST_EXIT=$?
set -e

# Both guards run whatever the tests returned: dotnet test exits 0 when the filter matches nothing
# (zero-test guard) and when tests are skipped (skip guard), so its exit status alone proves neither.
bash scripts/cert-gate-zero-test-guard.sh "${TRX}"
bash scripts/cert-gate-skip-guard.sh "${TRX}"

# The job summary is written by the workflow's own `if: always()` step
# (.github/workflows/cert-gate.yml), which also covers a failed run. It used to be written here as
# well, which put the same table in the CI job summary twice.

exit "${TEST_EXIT}"
