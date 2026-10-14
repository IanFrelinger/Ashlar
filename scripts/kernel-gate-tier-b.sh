#!/usr/bin/env bash
# Tier B kernel gate: production-readiness checks (build, CLI pipeline ops, LiteDB cross-process resume).
# See docs/ProductionReadinessGate-v1.md and docs/production-readiness/KernelHardeningPlan-v1.md
#
# Every CLI run below names its own adapter configuration, so the verdict does not depend on whether
# the caller exported ASHLAR_ALLOW_MOCK (kernel-gate.yml does, compat-gate.yml does not):
#   unconfigured  ASHLAR_ALLOW_MOCK removed: the default placeholders must fail closed (section 3.C.2-3),
#                 the deterministic one on the gate-demo template and the agentic one on a template
#                 whose root is a Hybrid stage.
#   configured    ASHLAR_ALLOW_MOCK=1, the CI stub mode of the same adapters: the run, the Hybrid
#                 fallback and the LiteDB resume must complete.
# Other ASHLAR_PIPELINE_* variables a caller exports still reach every run; only ASHLAR_ALLOW_MOCK is pinned.
# The assertions are scripts/lib/pipeline_gate_assertions.py, which says why both are needed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLI_PROJECT="application/src/Ashlar.CLI/Ashlar.CLI.csproj"
ASSERT=(python3 "$ROOT/scripts/lib/pipeline_gate_assertions.py")
TMP_ROOT="${TMPDIR:-/tmp}/ashlar-kernel-gate-$$"
export TMP_ROOT
mkdir -p "$TMP_ROOT"
trap 'rm -rf "$TMP_ROOT"' EXIT

TEMPLATE_PATH="$TMP_ROOT/pipeline_gate_demo.json"
RESUME_DB="$TMP_ROOT/ashlar_pipeline_gate_resume.db"
TRX_DIR="test-results/kernel-gate-tier-b"
rm -rf "$TRX_DIR"

echo "== Tier B: build checks =="
dotnet build src/Ashlar.Core.Application/Ashlar.Core.Application.csproj -f netstandard2.0 -v minimal
dotnet build src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj -v minimal
dotnet build "$CLI_PROJECT" -v minimal

echo "== Tier B: pipeline lifecycle tests (net8) =="
ASHLAR_ALLOW_MOCK=1 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 \
  --filter "FullyQualifiedName~PipelineTemplateValidatorTests|FullyQualifiedName~PipelineLifecycleE2ETests" \
  --blame-hang-timeout 180s --blame-hang-dump-type none \
  --logger "trx;LogFileName=pipeline-lifecycle.trx" --results-directory "$TRX_DIR" \
  --logger "console;verbosity=minimal"
bash scripts/ci/zero-test-guard.sh "$TRX_DIR/pipeline-lifecycle.trx"

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
HYBRID_ROOT_TEMPLATE_PATH="$TMP_ROOT/pipeline_gate_hybrid_root.json"
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

echo "== Tier B: CLI validate / fail-closed run / configured run / fallback / diagnostics =="
VALIDATE_LOG="$TMP_ROOT/gate-validate.log"
UNCONFIGURED_LOG="$TMP_ROOT/gate-run-unconfigured.log"
HOOKS_LOG="$TMP_ROOT/gate-run-hooks.log"
AGENTIC_LOG="$TMP_ROOT/gate-run-hybrid-root.log"
SUCCESS_LOG="$TMP_ROOT/gate-run-success.log"
FALLBACK_LOG="$TMP_ROOT/gate-run-fallback.log"
DIAGNOSTICS_LOG="$TMP_ROOT/gate-diagnostics.log"

dotnet run --project "$CLI_PROJECT" -- pipeline validate --template "$TEMPLATE_PATH" | tee "$VALIDATE_LOG"

run_cli "$UNCONFIGURED_LOG" unconfigured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-unconfigured --format-json
"${ASSERT[@]}" fail-closed "$UNCONFIGURED_LOG" "$CLI_RC" unconfigured

# Turning test hooks and the lenient completion policy on must not turn the unconfigured run into a
# success (section 3.C.3). The hook it passes, a failure on hybrid's deterministic worker, never fires:
# ingest fails first and hybrid stays Pending. What this leg proves is the switches, not the hook.
ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 ASHLAR_PIPELINE_COMPLETION_POLICY=AllowNonCriticalStageFailures \
  run_cli "$HOOKS_LOG" unconfigured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-hooks \
  --input "fail:hybrid:deterministic=true" --format-json
"${ASSERT[@]}" fail-closed "$HOOKS_LOG" "$CLI_RC" test-hooks

run_cli "$AGENTIC_LOG" unconfigured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$HYBRID_ROOT_TEMPLATE_PATH" --run-id gate-run-hybrid-root --format-json
"${ASSERT[@]}" fail-closed-agentic "$AGENTIC_LOG" "$CLI_RC" unconfigured-hybrid-root

run_cli "$SUCCESS_LOG" configured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-success --format-json
"${ASSERT[@]}" configured-success "$SUCCESS_LOG" "$CLI_RC" configured

ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 ASHLAR_PIPELINE_COMPLETION_POLICY=AllowNonCriticalStageFailures \
  run_cli "$FALLBACK_LOG" configured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-fallback \
  --input "fail:hybrid:deterministic=true" --format-json
"${ASSERT[@]}" fallback "$FALLBACK_LOG" "$CLI_RC" configured

dotnet run --project "$CLI_PROJECT" -- pipeline diagnostics --format-json | tee "$DIAGNOSTICS_LOG"
echo "CLI operational checks: PASS"

echo "== Tier B: cross-process durable resume (LiteDB) =="
RESUME_SOURCE_LOG="$TMP_ROOT/gate-resume-source.log"
RESUME_UNCONFIGURED_LOG="$TMP_ROOT/gate-resume-unconfigured.log"
RESUME_TARGET_LOG="$TMP_ROOT/gate-resume-target.log"
rm -f "$RESUME_DB"

# The source fails on a test hook, before any adapter is called, so its failure is the hook's alone.
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 \
  run_cli "$RESUME_SOURCE_LOG" configured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" \
  --run-id gate-resume-source --input "fail:ingest:deterministic=true" --format-json
SOURCE_RC=$CLI_RC

# A second process finds the persisted source; without a configured adapter it stays Failed (section 3.D.3).
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" \
  run_cli "$RESUME_UNCONFIGURED_LOG" unconfigured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" \
  --run-id gate-resume-unconfigured --resume-run-id gate-resume-source --resume-failed-stages --format-json
"${ASSERT[@]}" resume "$RESUME_SOURCE_LOG" "$SOURCE_RC" "$RESUME_UNCONFIGURED_LOG" "$CLI_RC" failed unconfigured

# A third process resumes the same persisted source with the configured adapter, to completion.
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" \
  run_cli "$RESUME_TARGET_LOG" configured \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" \
  --run-id gate-resume-target --resume-run-id gate-resume-source --resume-failed-stages --format-json
"${ASSERT[@]}" resume "$RESUME_SOURCE_LOG" "$SOURCE_RC" "$RESUME_TARGET_LOG" "$CLI_RC" completed configured
echo "Cross-process durable resume: PASS"

echo ""
echo "kernel-gate-tier-b: PASS"
