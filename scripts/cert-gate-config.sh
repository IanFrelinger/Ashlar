#!/usr/bin/env bash
# Single source of truth for cert-gate test filter and expected count, and for reading TRX counters.
set -euo pipefail

# Must match tests exercised by scripts/run-cert-gate.sh and .github/workflows/cert-gate.yml
readonly CERT_GATE_FILTER='FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification|FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Adaptation.GenerationSafety|FullyQualifiedName~AstMutationEngineTests'

# The expected test count is derived at RUNTIME from `dotnet test --list-tests` (see
# cert_gate_expected_count below); there is no hardcoded total to keep in sync. A previous
# per-class enumeration here summed to 99 while the gate actually ran 178 — it had drifted
# by 79 and read as authoritative, so it was removed rather than re-pinned. Do not re-add a
# static count: the zero-test guard fails loudly if discovery ever returns nothing.
#
# Excluded from cert-gate filter: LocalFixtures.CompositionAcceptanceRateBatchFixtureGeneratorTests (local fixture regen only)

cert_gate_list_tests() {
  local root="${1:?repo root required}"
  dotnet test "${root}/src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj" \
    -f net8.0 \
    --no-build \
    --list-tests \
    --filter "${CERT_GATE_FILTER}" 2>/dev/null \
    | grep -E '^[[:space:]]*Ashlar\.Tests\.' || true
}

cert_gate_expected_count() {
  local root="${1:?repo root required}"
  cert_gate_list_tests "${root}" | wc -l | tr -d '[:space:]'
}

# Prints one numeric attribute (total, executed, passed, ...) of the TRX's <Counters> element, or
# fails. It reads ONLY that element. Test output is written into the TRX before the run summary,
# so the first `total="N"` in the file is not necessarily the counter - but XML escapes '<' in text
# and in attribute values alike, so a literal '<Counters ' can only be the element itself.
# The whitespace required before the attribute name is defence, not a fix for a known collision:
# it reads a name only as a whole attribute, never as the tail of a longer one. No attribute the
# TRX writes today is such a tail (grep is case-sensitive, so `notExecuted` never matches
# `executed=`); without it, a future one such as `subtotal` would make every read of `total`
# find two values and fail closed.
#
# Returns 1 (after saying why on stderr) unless there is exactly one <Counters> element carrying
# exactly one numeric value for the attribute. Callers assign with a plain `var="$(...)"`, never
# `local var="$(...)"`, so the failure is not masked and `set -e` stops them.
cert_gate_trx_counter() {
  local trx="${1:?trx path required}"
  local name="${2:?counter name required}"
  local counters found value
  counters="$(tr '\r\n' '  ' < "${trx}" | grep -oE '<Counters [^>]*>' || true)"
  found="$(printf '%s\n' "${counters}" | grep -c '<Counters ' || true)"
  if [[ "${found}" != "1" ]]; then
    echo "cert-gate: expected exactly one <Counters> element in ${trx}, found ${found}" >&2
    return 1
  fi
  value="$(printf '%s\n' "${counters}" | grep -oE "[[:space:]]${name}=\"[0-9]+\"" | sed -E 's/.*="([0-9]+)"$/\1/' || true)"
  if [[ ! "${value}" =~ ^[0-9]+$ ]]; then
    echo "cert-gate: the <Counters> element in ${trx} has no single numeric '${name}' attribute" >&2
    return 1
  fi
  printf '%s\n' "${value}"
}
