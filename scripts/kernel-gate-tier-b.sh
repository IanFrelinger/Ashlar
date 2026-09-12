#!/usr/bin/env bash
# Tier B kernel gate: production-readiness checks (build, CLI pipeline ops, LiteDB cross-process resume).
# See docs/ProductionReadinessGate-v1.md and docs/production-readiness/KernelHardeningPlan-v1.md
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLI_PROJECT="application/src/Ashlar.CLI/Ashlar.CLI.csproj"
TMP_ROOT="${TMPDIR:-/tmp}/ashlar-kernel-gate-$$"
export TMP_ROOT
mkdir -p "$TMP_ROOT"
trap 'rm -rf "$TMP_ROOT"' EXIT

TEMPLATE_PATH="$TMP_ROOT/pipeline_gate_demo.json"
RESUME_DB="$TMP_ROOT/ashlar_pipeline_gate_resume.db"

echo "== Tier B: build checks =="
dotnet build src/Ashlar.Core.Application/Ashlar.Core.Application.csproj -f netstandard2.0 -v minimal
dotnet build src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj -v minimal
dotnet build "$CLI_PROJECT" -v minimal

echo "== Tier B: pipeline lifecycle tests (net8) =="
ASHLAR_ALLOW_MOCK=1 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 \
  --filter "FullyQualifiedName~PipelineTemplateValidatorTests|FullyQualifiedName~PipelineLifecycleE2ETests" \
  --blame-hang-timeout 180s --blame-hang-dump-type none \
  --logger "console;verbosity=minimal"

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

parse_final_json() {
  python3 - "$1" <<'PY'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8") as fh:
    lines = [line.strip() for line in fh.readlines()]
json_lines = [line for line in lines if line.startswith("{") and line.endswith("}")]
if not json_lines:
    raise SystemExit(f"No JSON payload found in {path}")
print(json_lines[-1])
PY
}

echo "== Tier B: CLI validate / run / fallback =="
VALIDATE_LOG="$TMP_ROOT/gate-validate.log"
SUCCESS_LOG="$TMP_ROOT/gate-run-success.log"
FALLBACK_LOG="$TMP_ROOT/gate-run-fallback.log"
DIAGNOSTICS_LOG="$TMP_ROOT/gate-diagnostics.log"

dotnet run --project "$CLI_PROJECT" -- pipeline validate --template "$TEMPLATE_PATH" | tee "$VALIDATE_LOG"
dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-success --format-json | tee "$SUCCESS_LOG"
ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 ASHLAR_PIPELINE_COMPLETION_POLICY=AllowNonCriticalStageFailures \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-fallback \
  --input "fail:hybrid:deterministic=true" --format-json | tee "$FALLBACK_LOG"
dotnet run --project "$CLI_PROJECT" -- pipeline diagnostics --format-json | tee "$DIAGNOSTICS_LOG"

python3 - <<PY
import json, os
tmp = os.environ["TMP_ROOT"]
def load(name):
    with open(os.path.join(tmp, name), encoding="utf-8") as fh:
        lines = [l.strip() for l in fh if l.strip().startswith("{")]
    return json.loads(lines[-1])
success = load("gate-run-success.log")
fallback = load("gate-run-fallback.log")
if not success.get("ok"):
    raise SystemExit("Success run did not return ok=true")
if success.get("data", {}).get("state") != "Completed":
    raise SystemExit("Success run did not complete")
stages = fallback.get("data", {}).get("stages", [])
hybrid = next((s for s in stages if s.get("stageId") == "hybrid"), None)
if hybrid is None or hybrid.get("workerType") != "Agentic":
    raise SystemExit("Fallback run did not switch hybrid stage to Agentic worker")
print("CLI operational checks: PASS")
PY

echo "== Tier B: cross-process durable resume (LiteDB) =="
RESUME_SOURCE_LOG="$TMP_ROOT/gate-resume-source.log"
RESUME_TARGET_LOG="$TMP_ROOT/gate-resume-target.log"
rm -f "$RESUME_DB"

set +e
ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" \
  --run-id gate-resume-source --input "fail:ingest:deterministic=true" --format-json | tee "$RESUME_SOURCE_LOG"
source_exit=$?
set -e

if [ "$source_exit" -eq 0 ]; then
  echo "Expected source run to fail for resume scenario" >&2
  exit 1
fi

ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" \
  dotnet run --project "$CLI_PROJECT" -- pipeline run --template "$TEMPLATE_PATH" \
  --run-id gate-resume-target --resume-run-id gate-resume-source --resume-failed-stages --format-json | tee "$RESUME_TARGET_LOG"

python3 - <<PY
import json, os
tmp = os.environ["TMP_ROOT"]
def load(name):
    with open(os.path.join(tmp, name), encoding="utf-8") as fh:
        lines = [l.strip() for l in fh if l.strip().startswith("{")]
    return json.loads(lines[-1])
source = load("gate-resume-source.log")
target = load("gate-resume-target.log")
if source.get("data", {}).get("state") != "Failed":
    raise SystemExit("Source run expected Failed state")
if not target.get("ok") or target.get("data", {}).get("state") != "Completed":
    raise SystemExit("Resumed target run did not complete successfully")
print("Cross-process durable resume: PASS")
PY

echo ""
echo "kernel-gate-tier-b: PASS"
