#!/usr/bin/env bash
# Fail closed when the cert-gate filter matches too few tests (dotnet test exits 0 on zero matches).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/cert-gate-config.sh
source "${ROOT}/scripts/cert-gate-config.sh"

TRX="${1:-test-results/cert-gate.trx}"
MIN_EXPECTED="$(cert_gate_expected_count "${ROOT}")"

if [[ ! -f "${TRX}" ]]; then
  echo "cert-gate TRX not found: ${TRX}"
  exit 1
fi

if [[ -z "${MIN_EXPECTED}" || "${MIN_EXPECTED}" -lt 1 ]]; then
  echo "cert-gate could not derive expected test count (got=${MIN_EXPECTED:-0}) — build test project first."
  exit 1
fi

# `total` counts every result the TRX holds, SKIPPED ones included, and --list-tests lists a skipped
# test too - so a skip moves both sides of this floor together and it cannot see one. That is not
# this guard's job: cert-gate-skip-guard.sh pins total - executed to a committed baseline, and
# run-cert-gate.sh runs it straight after this one. Read from the <Counters> element, not the first
# `total="N"` in the file, which can be text a test printed (see cert_gate_trx_counter).
REPORTED="$(cert_gate_trx_counter "${TRX}" total)"

if [[ "${REPORTED}" -lt "${MIN_EXPECTED}" ]]; then
  echo "cert-gate matched fewer tests than expected (reported=${REPORTED}, expected>=${MIN_EXPECTED}) — filter is stale."
  exit 1
fi

echo "cert-gate reported ${REPORTED} tests (expected>=${MIN_EXPECTED}, derived from --list-tests)."
