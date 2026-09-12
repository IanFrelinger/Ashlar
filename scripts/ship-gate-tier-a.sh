#!/usr/bin/env bash
# Ship Tier A: Production Readiness Gate v1 (build + pipeline CLI + LiteDB resume).
# Mirrors .github/workflows/production-readiness-gate-v1.yml operational steps.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLI="application/src/Ashlar.CLI/Ashlar.CLI.csproj"
INFRA_TESTS="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required for JSON assertions in ship-gate-tier-a" >&2
  exit 1
fi

echo "== Ship Tier A: build checks =="
dotnet build src/Ashlar.Core.Application/Ashlar.Core.Application.csproj -f netstandard2.0 -v minimal
dotnet build src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj -v minimal
dotnet build "$CLI" -v minimal

echo "== Ship Tier A: host DI smoke =="
dotnet build "$INFRA_TESTS" -f net8.0 -v minimal
ASHLAR_ALLOW_MOCK=1 dotnet test "$INFRA_TESTS" -f net8.0 --no-build \
  --filter "FullyQualifiedName~HostingE2ESmokeTests.AddAshlar_RegistersObservationPipeline_ByDefault|FullyQualifiedName~PipelineServiceCollectionExtensionsTests.AddAshlar_RegistersPipelineCompositionLayerByDefault" \
  --blame-hang-timeout 180s --blame-hang-dump-type none

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

echo "== Ship Tier A: CLI pipeline validate / run / fallback =="
ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- \
  pipeline validate --template "$TEMPLATE_PATH"

SUCCESS_LOG="$TMP/gate-run-success.log"
FALLBACK_LOG="$TMP/gate-run-fallback.log"
ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-success --format-json | tee "$SUCCESS_LOG"
ASHLAR_ALLOW_MOCK=1 ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 ASHLAR_PIPELINE_COMPLETION_POLICY=AllowNonCriticalStageFailures \
  dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-run-fallback --input "fail:hybrid:deterministic=true" --format-json | tee "$FALLBACK_LOG"
ASHLAR_ALLOW_MOCK=1 dotnet run --project "$CLI" --no-build -- pipeline diagnostics --format-json >/dev/null

python3 - <<PY
import json, os, sys
tmp = os.environ["TMP"]
def parse_final_json(path):
    with open(path, encoding="utf-8") as fh:
        lines = [line.strip() for line in fh if line.strip()]
    json_lines = [l for l in lines if l.startswith("{") and l.endswith("}")]
    if not json_lines:
        sys.exit(f"No JSON in {path}")
    return json.loads(json_lines[-1])
success = parse_final_json(os.path.join(tmp, "gate-run-success.log"))
fallback = parse_final_json(os.path.join(tmp, "gate-run-fallback.log"))
if not success.get("ok") or success.get("data", {}).get("state") != "Completed":
    sys.exit("Success pipeline run did not complete")
hybrid = next((s for s in fallback.get("data", {}).get("stages", []) if s.get("stageId") == "hybrid"), None)
if hybrid is None or hybrid.get("workerType") != "Agentic":
    sys.exit(f"Fallback did not use Agentic worker: {hybrid}")
PY

echo "== Ship Tier A: LiteDB cross-process resume =="
RESUME_DB="$TMP/ashlar_pipeline_gate_resume.db"
RESUME_SOURCE_LOG="$TMP/gate-resume-source.log"
RESUME_TARGET_LOG="$TMP/gate-resume-target.log"
set +e
ASHLAR_ALLOW_MOCK=1 ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" ASHLAR_PIPELINE_ENABLE_TEST_HOOKS=1 \
  dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-resume-source --input "fail:ingest:deterministic=true" --format-json | tee "$RESUME_SOURCE_LOG"
source_exit=$?
set -e
if [ "$source_exit" -eq 0 ]; then
  echo "Expected source run to fail for resume scenario" >&2
  exit 1
fi
ASHLAR_ALLOW_MOCK=1 ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb ASHLAR_PIPELINE_STORE_PATH="$RESUME_DB" \
  dotnet run --project "$CLI" --no-build -- \
  pipeline run --template "$TEMPLATE_PATH" --run-id gate-resume-target --resume-run-id gate-resume-source --resume-failed-stages --format-json | tee "$RESUME_TARGET_LOG"

python3 - <<PY
import json, os, sys
tmp = os.environ["TMP"]
def parse_final_json(path):
    with open(path, encoding="utf-8") as fh:
        lines = [line.strip() for line in fh if line.strip()]
    json_lines = [l for l in lines if l.startswith("{") and l.endswith("}")]
    return json.loads(json_lines[-1])
source = parse_final_json(os.path.join(tmp, "gate-resume-source.log"))
target = parse_final_json(os.path.join(tmp, "gate-resume-target.log"))
if source.get("data", {}).get("state") != "Failed":
    sys.exit("Source run should be Failed")
if not target.get("ok") or target.get("data", {}).get("state") != "Completed":
    sys.exit("Resume target did not complete")
PY

echo ""
echo "ship-gate-tier-a: PASS"
