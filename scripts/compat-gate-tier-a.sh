#!/usr/bin/env bash
# Compat Tier A: schema / migration / composition compatibility tests.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

INFRA="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
FLEET_TESTS="commercial/tests/Ashlar.Commercial.Tests.Fleet/Ashlar.Commercial.Tests.Fleet.csproj"
TRX_DIR="test-results/compat-gate-tier-a"
rm -rf "$TRX_DIR"

echo "== Compat Tier A: mesh checkpoint migration =="
# This step ran against Tests.Infrastructure and selected ZERO tests from 2026-06-08 until this change.
# The filter was written on 2026-05-21 (bec2a6ed) when MeshTaskExecutionServiceTests lived in
# src/Nexo.Tests.Infrastructure/Tests/Fleet; the commercial extraction (678d0718, #151) moved it to
# commercial/tests/Nexo.Commercial.Tests.Fleet, and the step kept pointing at the old project. The
# filter string is unchanged - it is the project that was wrong. Measured in the dev container at
# 876cc527: Tests.Infrastructure selects 0, Commercial.Tests.Fleet selects 1
# (MigrateForCheckpointAsync_clears_assignment_and_sets_checkpoint). The GapCoverage class's own
# MigrateForCheckpointAsync_* facts (3 more) postdate this filter and are deliberately not added here.
dotnet build "$FLEET_TESTS" -f net8.0 -v minimal
dotnet test "$FLEET_TESTS" -f net8.0 --no-build \
  --filter "FullyQualifiedName~MeshTaskExecutionServiceTests.MigrateForCheckpointAsync" \
  --logger "trx;LogFileName=mesh-checkpoint-migration.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 60s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/mesh-checkpoint-migration.trx"

echo "== Compat Tier A: LiteDB persistence registration =="
dotnet build "$INFRA" -v minimal
dotnet test "$INFRA" -f net8.0 --no-build \
  --filter "FullyQualifiedName~PipelineServiceCollectionExtensionsTests.AddPipelineCompositionLayer_WithLiteDbPersistence" \
  --logger "trx;LogFileName=litedb-persistence-registration.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 60s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/litedb-persistence-registration.trx"

echo "== Compat Tier A: composition registry validation =="
dotnet test "$INFRA" -f net8.0 --no-build \
  --filter "FullyQualifiedName~CompositionRegistryValidationTests" \
  --logger "trx;LogFileName=composition-registry-validation.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 60s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/composition-registry-validation.trx"

echo ""
echo "compat-gate-tier-a: PASS"
