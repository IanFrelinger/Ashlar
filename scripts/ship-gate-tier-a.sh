#!/usr/bin/env bash
# Ship Tier A: Production Readiness Gate v1 (build + pipeline CLI + LiteDB resume).
# Asserts docs/ProductionReadinessGate-v1.md sections 3.C.1-3 and 3.D, which
# .github/workflows/production-readiness-gate-v1.yml also checks: validate succeeds, the unconfigured
# default adapters fail closed (the deterministic one, also under test hooks, and the agentic one on a
# Hybrid-root template), and a resumed run finds its persisted source. Section 3.C.4 is not asserted:
# `pipeline diagnostics` is run, and its output discarded. On top of that it runs the template with
# ASHLAR_ALLOW_MOCK=1 (the CI stub mode of the default adapters), which the workflow does not: a configured
# run, the Hybrid fallback and a LiteDB resume must then complete. Each CLI run sets ASHLAR_ALLOW_MOCK
# itself, so that variable in the caller's environment cannot change the verdict; other ASHLAR_PIPELINE_*
# variables a caller exports still reach the runs. The assertions are scripts/lib/pipeline_gate_assertions.py;
# kernel-gate-tier-b.sh runs the same legs.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLI="application/src/Ashlar.CLI/Ashlar.CLI.csproj"
INFRA_TESTS="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
TRX_DIR="test-results/ship-gate-tier-a"
rm -rf "$TRX_DIR"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required for JSON assertions in ship-gate-tier-a" >&2
  exit 1
fi
ASSERT=(python3 "$ROOT/scripts/lib/pipeline_gate_assertions.py")

echo "== Ship Tier A: build checks =="
dotnet build src/Ashlar.Core.Application/Ashlar.Core.Application.csproj -f netstandard2.0 -v minimal
dotnet build src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj -v minimal
dotnet build "$CLI" -v minimal

echo "== Ship Tier A: host DI smoke =="
dotnet build "$INFRA_TESTS" -f net8.0 -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA_TESTS" -f net8.0 --no-build \
  --filter "FullyQualifiedName~HostingE2ESmokeTests.AddAshlar_RegistersObservationPipeline_ByDefault|FullyQualifiedName~PipelineServiceCollectionExtensionsTests.AddAshlar_RegistersPipelineCompositionLayerByDefault" \
  --logger "trx;LogFileName=host-di-smoke.trx" --results-directory "$TRX_DIR" \
  --blame-hang-timeout 180s --blame-hang-dump-type none
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/host-di-smoke.trx"

TMP="$(mktemp -d)"
export TMP
trap 'rm -rf "$TMP"' EXIT
TEMPLATE_PATH="$TMP/pipeline_gate_demo.json"
cat > "$TEMPLATE_PATH" <<'JSON'
{
  "templateId": "gate-demo",
  "version": "1.0",
  "stages": [
    { "id": "ingest", "name": "Ingest", "mode": "Deterministic" },
    { "id": "hybrid", "name": "Hybrid", "mode": "Hybrid", "fallbackChain": ["Deterministic", "Agentic"] }
  ],
  "edges": [
    { "fromStageId": "ingest", "toStageId": "hybrid" }
  ]
}
JSON

# A Hybrid stage as the root: unconfigured, the deterministic placeholder fails (not retryable), the chain
# falls back, and the agentic placeholder must fail too. gate-demo never gets that far: ingest fails first.
HYBRID_ROOT_TEMPLATE_PATH="$TMP/pipeline_gate_hybrid_root.json"
cat > "$HYBRID_ROOT_TEMPLATE_PATH" <<'JSON'
{
  "templateId": "gate-hybrid-root",
  "version": "1.0",
  "stages": [
    { "id": "hybrid", "name": "Hybrid", "mode": "Hybrid", "fallbackChain": ["Deterministic", "Agentic"] }
  ],
  "edges": []
}
JSON

unconfigured() { env -u ASHLAR_ALLOW_MOCK "$@"; }
configured() { env ASHLAR_ALLOW_MOCK=1 "$@"; }

# run_cli <log> <command...>: tee stdout to <log> and keep the command's exit code in CLI_RC. A failed
# run is an expected outcome here, so it must not end the script; the assertions judge the exit code.
CLI_RC=0
run_cli() {
  local log="$1"
  shift
  set +e
  "$@" | tee "$log"
  CLI_RC=${PIPESTATUS[0]}
  set -e
}

echo "== Ship Tier A: CLI pipeline validate / fail-closed run / configured run / fallback =="
ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- \
  pipeline validate --template "$TEMPLATE_PATH"

UNCONFIGURED_LOG="$TMP/gate-run-unconfigured.log"
HOOKS_LOG="$TMP/gate-run-hooks.log"
AGENTIC_LOG="$TMP/gate-run-hybrid-root.log"
SUCCESS_LOG="$TMP/gate-run-success.log"
FALLBACK_LOG="$TMP/gate-run-fallback.log"

run_cli "$UNCONFIGURED_LOG" unconfigured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-unconfigured --format-json
"${ASSERT[@]}" fail-closed "$UNCONFIGURED_LOG" "$CLI_RC" unconfigured

# Turning test hooks and the lenient completion policy on must not turn the unconfigured run into a
# success (section 3.C.3). The hook it passes, a failure on hybrid's deterministic worker, never fires:
# ingest fails first and hybrid stays Pending. What this leg proves is the switches, not the hook.
ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 ASHLAR_PIPELINE_COMPLETION_POLICY=AllowNonCriticalStageFailures \
  run_cli "$HOOKS_LOG" unconfigured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-hooks --input "fail:hybrid:deterministic=true" --format-json
"${ASSERT[@]}" fail-closed "$HOOKS_LOG" "$CLI_RC" test-hooks

run_cli "$AGENTIC_LOG" unconfigured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$HYBRID_ROOT_TEMPLATE_PATH" --run-id gate-run-hybrid-root --format-json
"${ASSERT[@]}" fail-closed-agentic "$AGENTIC_LOG" "$CLI_RC" unconfigured-hybrid-root

run_cli "$SUCCESS_LOG" configured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-success --format-json
"${ASSERT[@]}" configured-success "$SUCCESS_LOG" "$CLI_RC" configured

ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 ASHLAR_PIPELINE_COMPLETION_POLICY=AllowNonCriticalStageFailures \
  run_cli "$FALLBACK_LOG" configured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-fallback --input "fail:hybrid:deterministic=true" --format-json
"${ASSERT[@]}" fallback "$FALLBACK_LOG" "$CLI_RC" configured

ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- pipeline diagnostics --format-json >/dev/null

echo "== Ship Tier A: LiteDB cross-process resume =="
RESUME_DB="$TMP/ashlar_pipeline_gate_resume.db"
RESUME_SOURCE_LOG="$TMP/gate-resume-source.log"
RESUME_UNCONFIGURED_LOG="$TMP/gate-resume-unconfigured.log"
RESUME_TARGET_LOG="$TMP/gate-resume-target.log"

# The source fails on a test hook, before any adapter is called, so its failure is the hook's alone.
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 \
  run_cli "$RESUME_SOURCE_LOG" configured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-resume-source --input "fail:ingest:deterministic=true" --format-json
SOURCE_RC=$CLI_RC

# A second process finds the persisted source; without a configured adapter it stays Failed (section 3.D.3).
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" \
  run_cli "$RESUME_UNCONFIGURED_LOG" unconfigured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-resume-unconfigured --resume-run-id gate-resume-source --resume-failed-stages --format-json
"${ASSERT[@]}" resume "$RESUME_SOURCE_LOG" "$SOURCE_RC" "$RESUME_UNCONFIGURED_LOG" "$CLI_RC" failed unconfigured

# A third process resumes the same persisted source with the configured adapter, to completion.
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" \
  run_cli "$RESUME_TARGET_LOG" configured dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-resume-target --resume-run-id gate-resume-source --resume-failed-stages --format-json
"${ASSERT[@]}" resume "$RESUME_SOURCE_LOG" "$SOURCE_RC" "$RESUME_TARGET_LOG" "$CLI_RC" completed configured

echo ""
echo "ship-gate-tier-a: PASS"
