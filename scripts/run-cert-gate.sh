#!/usr/bin/env bash
# Local reproduction of the cert-gate CI job (hermetic certification + generation tests).
#
# Usage: bash scripts/run-cert-gate.sh [--fast]
#   (none)  the full gate, CERT_GATE_FILTER — what CI runs and what must pass before a merge.
#   --fast  the fast tier, CERT_GATE_FAST_FILTER: everything in the gate minus the classes marked
#           [Trait("Category", "SlowTier")], which spawn a real `dotnet msbuild` or a shell script.
#           For the inner loop; a green --fast is not a green gate. Results go to
#           test-results/cert-gate-fast.trx so they never overwrite a full run's.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

# shellcheck source=scripts/cert-gate-config.sh
source "${ROOT}/scripts/cert-gate-config.sh"

FILTER="${CERT_GATE_FILTER}"
TIER="full"
for arg in "$@"; do
  case "${arg}" in
    --fast) FILTER="${CERT_GATE_FAST_FILTER}"; TIER="fast" ;;
    *) echo "usage: bash scripts/run-cert-gate.sh [--fast]" >&2; exit 2 ;;
  esac
done

# The zero-test guard derives its expected count from this (cert-gate-config.sh), so a fast run is
# measured against the fast filter and a full run against the full one.
export CERT_GATE_COUNT_FILTER="${FILTER}"

RESULTS_DIR="${ROOT}/test-results"
if [[ "${TIER}" == "fast" ]]; then
  TRX_NAME="cert-gate-fast.trx"
else
  TRX_NAME="cert-gate.trx"
fi
TRX="${RESULTS_DIR}/${TRX_NAME}"

# Hermetic tests: do not use portability NuGet config (forces Roslyn path in GeneratedBrickBuilder).
unset ASHLAR_CERT_NUGET_CONFIG

mkdir -p "${RESULTS_DIR}"

echo "== cert-gate: restore =="
dotnet restore src/Ashlar.Tests.Infrastructure/scripts/copy-assemblies.csproj
dotnet restore src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj

echo "== cert-gate: build =="
dotnet build src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --no-restore -v minimal

echo "== cert-gate: test (${TIER} tier) =="
set +e
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  -f net8.0 \
  --no-build \
  --filter "${FILTER}" \
  --logger "trx;LogFileName=${TRX_NAME}" \
  --logger "console;verbosity=normal" \
  --results-directory "${RESULTS_DIR}"
TEST_EXIT=$?
set -e

bash scripts/cert-gate-zero-test-guard.sh "${TRX}"

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  bash scripts/cert-gate-summary.sh "${TRX}"
fi

exit "${TEST_EXIT}"
