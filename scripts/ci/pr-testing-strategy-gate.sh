#!/usr/bin/env bash
# Enforces testing strategy pivot rules on PR diffs (see docs/architecture/TestingStrategyPivot-v1.md).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

BASE_REF="${1:-${GITHUB_BASE_SHA:-origin/master}}"
PR_BODY="${PR_BODY:-}"

if ! git rev-parse --verify "$BASE_REF" >/dev/null 2>&1; then
  git fetch origin master main 2>/dev/null || true
  BASE_REF="origin/master"
fi

mapfile -t ADDED < <(git diff --diff-filter=A --name-only "$BASE_REF"...HEAD 2>/dev/null || git diff --diff-filter=A --name-only "$BASE_REF" HEAD)
mapfile -t CHANGED < <(git diff --name-only "$BASE_REF"...HEAD 2>/dev/null || git diff --name-only "$BASE_REF" HEAD)
ALL_CHANGED=("${CHANGED[@]}" "${ADDED[@]}")

fail=0
warn=0

note() { echo "testing-strategy: $*"; }
warn_note() { echo "testing-strategy: WARNING: $*"; ((warn++)) || true; }
fail_note() { echo "testing-strategy: ERROR: $*"; fail=1; }

body_contains() {
  local token="$1"
  [[ "$PR_BODY" == *"$token"* ]]
}

# --- Phase 4: no new *GapCoverageTests.cs without justification ---
for f in "${ADDED[@]}"; do
  [[ "$f" == *GapCoverageTests.cs ]] || continue
  if body_contains "gap-coverage-justify:" || [[ "${ALLOW_NEW_GAP_COVERAGE:-}" == "1" ]]; then
    note "New gap file allowed (justified): $f"
  else
    fail_note "New gap coverage file '$f'. Add ProdStyle/wiring tests instead, or document in PR body: gap-coverage-justify: <reason>"
  fi
done

# --- Phase 2: ProdStyle paths need wiring tests or explicit skip ---
needs_prod_style=0
for f in "${ALL_CHANGED[@]}"; do
  case "$f" in
    src/Ashlar.Hosting/*|application/src/Ashlar.API/*|src/Ashlar.Runtime/Barriers/*|src/Ashlar.Runtime/Identity/*)
      needs_prod_style=1 ;;
    src/Ashlar.Infrastructure/Execution/Routing/*|src/Ashlar.Infrastructure/Barriers/*|src/Ashlar.Infrastructure/Pipelines/*)
      needs_prod_style=1 ;;
    src/Ashlar.Infrastructure/Tests/VirtualProduction/*|src/Ashlar.Infrastructure/Tests/Hosting/*)
      : ;;
  esac
done

has_prod_style_test_delta=0
for f in "${ALL_CHANGED[@]}"; do
  case "$f" in
    *ProdStyle*|*VirtualProduction/*|*HostingDeploymentProfile*|*HostingE2ESmoke*|*FrameworkVirtualProd*|*PipelineLifecycleE2E*|*ProviderFactoryEdgeCase*|*MiddlewareIngressIntegration*|*AirGapped*|*KernelPhaseResolution*)
      has_prod_style_test_delta=1 ;;
  esac
done

if [[ "$needs_prod_style" -eq 1 ]]; then
  if body_contains "[skip-prod-style]" || [[ "${SKIP_PROD_STYLE_CHECK:-}" == "1" ]]; then
    note "ProdStyle check skipped (token or env)."
  elif [[ "$has_prod_style_test_delta" -eq 1 ]]; then
    note "ProdStyle-related test files updated."
  else
    fail_note "Production-wiring paths changed without ProdStyle/virtual-host test updates. Run 'make test-prod-style' and add tests, or add [skip-prod-style] to the PR description with rationale."
  fi
fi

# --- New Core.Application source must arrive with Ashlar.Tests.Application coverage ---
# kernel-coverage measures Ashlar.Core.Application against a LINE FLOOR using only the
# Ashlar.Tests.Application run. So a new application type whose tests were parked in the
# infrastructure suite counts as uncovered, and the floor is reached from below by arithmetic:
# enough new lines with no matching test delta and the gate goes red on master AFTER the merge,
# where nothing was blocking. That has happened — 120 added lines with a full test suite sitting
# in the wrong project, and the first red run was on master.
#
# Added files only. Editing an existing file rarely moves the ratio; adding one always can.
# docs/architecture/TestingStrategyTracking-v1.md is what assigns src/Ashlar.Core.Application/**
# to Ashlar.Tests.Application; this is that assignment with teeth.
added_core_app=0
for f in "${ADDED[@]}"; do
  case "$f" in
    src/Ashlar.Core.Application/*.cs) added_core_app=1 ;;
  esac
done

has_app_test_delta=0
for f in "${ALL_CHANGED[@]}"; do
  case "$f" in
    src/Ashlar.Tests.Application/*) has_app_test_delta=1 ;;
  esac
done

if [[ "$added_core_app" -eq 1 ]]; then
  if body_contains "[skip-app-coverage]" || [[ "${SKIP_APP_COVERAGE_CHECK:-}" == "1" ]]; then
    note "New Core.Application source allowed without an Ashlar.Tests.Application delta (token or env)."
  elif [[ "$has_app_test_delta" -eq 1 ]]; then
    note "New Core.Application source arrives with Ashlar.Tests.Application changes."
  else
    fail_note "New source under src/Ashlar.Core.Application/ with no change under src/Ashlar.Tests.Application/. kernel-coverage measures that assembly using only that project's run, so tests placed elsewhere leave the new type counted as uncovered. Add them there, or add [skip-app-coverage] to the PR description with rationale."
  fi
fi

# --- Recommend local commands ---
note "Suggested commands for this diff:"
needs_domain=0
needs_kernel_cov=0
needs_kernel_gate=0
needs_mesh=0
needs_app_gate=0
needs_dependency_boundary=0
docs_only=1

for f in "${ALL_CHANGED[@]}"; do
  [[ -n "$f" ]] || continue
  case "$f" in
    docs/*|*.md)
      ;;
    *)
      docs_only=0 ;;
  esac
  case "$f" in
    src/Ashlar.Core.Domain/*) needs_domain=1; needs_kernel_cov=1 ;;
    src/Ashlar.Core.Application/*|src/Ashlar.Infrastructure/*|src/Ashlar.Runtime/*) needs_kernel_cov=1 ;;
    src/Ashlar.Hosting/*|src/Ashlar.Infrastructure/Pipelines/*) needs_kernel_gate=1 ;;
    src/Ashlar.Infrastructure/Mesh/*|src/Ashlar.Infrastructure/MeshLab/*|commercial/src/Ashlar.Commercial.Fleet.*|docker-compose*fleet*|scripts/mesh-lab*)
      needs_mesh=1 ;;
    application/*) needs_app_gate=1 ;;
    commercial/*) needs_dependency_boundary=1 ;;
    Directory.Build.props|Directory.Build.targets|LICENSING.md)
      needs_dependency_boundary=1 ;;
  esac
  [[ "$f" == *.csproj ]] && needs_dependency_boundary=1
done

if [[ "$docs_only" -eq 1 ]]; then
  note "  (docs-only) docs-link-check workflow is sufficient."
else
  [[ "$needs_kernel_cov" -eq 1 ]] && note "  make kernel-coverage-gate"
  [[ "$needs_kernel_gate" -eq 1 ]] && note "  make kernel-gate && make test-prod-style"
  [[ "$needs_domain" -eq 1 ]] && note "  dotnet test src/Ashlar.Tests.Domain/Ashlar.Tests.Domain.csproj"
  [[ "$needs_app_gate" -eq 1 ]] && note "  make application-gate-tier-a (or tier-c for API)"
  [[ "$needs_mesh" -eq 1 ]] && note "  make composition-mesh-gate-tier-c (or mesh-lab-e2e with Docker)"
  [[ "$needs_dependency_boundary" -eq 1 ]] && note "  make dependency-boundary-gate"
fi

if [[ "$fail" -ne 0 ]]; then
  echo ""
  echo "testing-strategy-gate: FAILED (see docs/architecture/TestingStrategyPivot-v1.md)"
  exit 1
fi

if [[ "$warn" -gt 0 ]]; then
  note "completed with $warn warning(s)."
else
  note "PASS"
fi
