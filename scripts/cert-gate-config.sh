#!/usr/bin/env bash
# Single source of truth for cert-gate test filter and expected count.
set -euo pipefail

# Must match tests exercised by scripts/run-cert-gate.sh and .github/workflows/cert-gate.yml
readonly CERT_GATE_FILTER='FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification|FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Adaptation.GenerationSafety|FullyQualifiedName~AstMutationEngineTests'

# The fast tier: the same selection minus the tests that spawn a real `dotnet msbuild` (loader-driven
# certification, the shipped samples) or a shell script — each costs a restore and a build where the
# rest of the gate costs milliseconds. Those classes carry [Trait("Category", "SlowTier")], and
# SlowTierConventionTests fails naming any class that reaches a build without the trait, and fails
# if this line stops being "the full filter minus the trait". Composed from CERT_GATE_FILTER, never
# restated, so a namespace added to the gate is in both tiers. `scripts/run-cert-gate.sh --fast`
# runs it; CI and the pre-merge gate run CERT_GATE_FILTER.
readonly CERT_GATE_FAST_FILTER="(${CERT_GATE_FILTER})&Category!=SlowTier"

# The expected test count is derived at RUNTIME from `dotnet test --list-tests` (see
# cert_gate_expected_count below); there is no hardcoded total to keep in sync. A previous
# per-class enumeration here summed to 99 while the gate actually ran 178 — it had drifted
# by 79 and read as authoritative, so it was removed rather than re-pinned. Do not re-add a
# static count: the zero-test guard fails loudly if discovery ever returns nothing.
#
# Excluded from cert-gate filter: LocalFixtures.CompositionAcceptanceRateBatchFixtureGeneratorTests (local fixture regen only)

# The filter the expected count is derived from: CERT_GATE_FILTER unless the caller ran a tier.
# run-cert-gate.sh --fast exports CERT_GATE_COUNT_FILTER=<fast filter> so the zero-test guard sizes
# itself to what actually ran instead of failing every fast run for being smaller than the full gate.
cert_gate_list_tests() {
  local root="${1:?repo root required}"
  local filter="${CERT_GATE_COUNT_FILTER:-${CERT_GATE_FILTER}}"
  dotnet test "${root}/src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj" \
    -f net8.0 \
    --no-build \
    --list-tests \
    --filter "${filter}" 2>/dev/null \
    | grep -E '^[[:space:]]*Ashlar\.Tests\.' || true
}

cert_gate_expected_count() {
  local root="${1:?repo root required}"
  cert_gate_list_tests "${root}" | wc -l | tr -d '[:space:]'
}
