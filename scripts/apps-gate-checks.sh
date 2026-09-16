#!/usr/bin/env bash
# Local mirror of .github/workflows/optimize-agent-cluster-gate.yml — the only
# CI workflow that owns apps/ paths — for the readiness gate's apps layer.
# One subcommand per CI job. Run from the repo root (readiness-gate-local.sh
# cd's there). CI's workflow-level env is reproduced below. The optimizer's
# LLM preflight is EXPECTED to fail (no Ollama) exactly as in CI; the checks
# assert the scaffold/bootstrap/daemon contract, not optimizer success.
set -uo pipefail

export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
export ASHLAR_STRICT_MODE=1 ASHLAR_ALLOW_MOCK=1

SCRIPT=apps/runtime-studio/scripts/optimize_agent_cluster.sh
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

fail() { echo "apps-gate-checks[$1]: FAIL — $2" >&2; exit 1; }

case "${1:?usage: apps-gate-checks.sh interface|bootstrap|scaffold|daemon|combos}" in
  interface)
    OUT=$(bash "$SCRIPT" --help) || fail interface "--help exited non-zero"
    echo "$OUT" | grep -q "Unified workflow" || fail interface "--help missing usage header"
    for flag in --objective --duration --spec --budget-runs; do
      echo "$OUT" | grep -q -- "$flag" || fail interface "--help missing '$flag'"
    done
    bash "$SCRIPT" --not-a-real-flag >/dev/null 2>&1
    [ $? -eq 2 ] || fail interface "unknown argument should exit 2"
    bash "$SCRIPT" -h | grep -q "Unified workflow" || fail interface "-h not accepted as short help"
    echo "interface: OK"
    ;;
  bootstrap)
    OUT=$(bash "$SCRIPT" --skip-optimize 2>&1) || fail bootstrap "--skip-optimize exited non-zero"
    echo "$OUT" | grep -q "Bootstrap Runtime Studio" || fail bootstrap "no bootstrap banner"
    echo "$OUT" | grep -q "Optimize.*SKIPPED" || fail bootstrap "optimize not skipped"
    echo "$OUT" | grep -q "Daemon.*SKIPPED" || fail bootstrap "daemon not auto-skipped"
    for d in agent-sandbox/agents/workspaces/runtime-studio agent-sandbox/tools/cache/tmp agent-sandbox/tools/cache/nuget agent-sandbox/tools/cache/npm; do
      test -d "$d" || fail bootstrap "missing sandbox dir $d"
    done
    echo "bootstrap: OK"
    ;;
  scaffold)
    # Optimizer exit is allowed to be non-zero (no Ollama) — CI does the same.
    bash "$SCRIPT" --skip-daemon --objective "readiness validation" --verbose >"$TMP/o1.txt" 2>&1
    test -f .ashlar/workflow/workflow_lab.runtime.json || fail scaffold "spec not scaffolded"
    grep -q "Optimize agent cluster for local hardware" "$TMP/o1.txt" || fail scaffold "optimize step not attempted"
    M1=$(stat -c %Y .ashlar/workflow/workflow_lab.runtime.json)
    bash "$SCRIPT" --skip-daemon --objective "readiness validation 2" >"$TMP/o2.txt" 2>&1
    M2=$(stat -c %Y .ashlar/workflow/workflow_lab.runtime.json)
    [ "$M1" = "$M2" ] || fail scaffold "spec re-scaffolded (mtime changed)"
    grep -q "Scaffolding default workflow lab spec" "$TMP/o2.txt" && fail scaffold "scaffold message on re-run"
    mkdir -p "$TMP/custom"
    bash "$SCRIPT" --skip-daemon --spec "$TMP/custom/my_spec.json" >"$TMP/o3.txt" 2>&1
    test -f "$TMP/custom/my_spec.json" || fail scaffold "custom --spec not honoured"
    echo "scaffold: OK"
    ;;
  daemon)
    bash "$SCRIPT" --skip-optimize --duration 2s --disable-observation >"$TMP/d.txt" 2>&1 \
      || fail daemon "daemon run exited non-zero"
    grep -q "Start background agent daemon" "$TMP/d.txt" || fail daemon "daemon step not reached"
    grep -q "Optimize.*SKIPPED" "$TMP/d.txt" || fail daemon "optimize not skipped"
    echo "daemon: OK"
    ;;
  combos)
    run_combo() {
      local label="$1"; shift
      bash "$SCRIPT" "$@" >"$TMP/c-$label.txt" 2>&1
      test -f .ashlar/workflow/workflow_lab.runtime.json || fail combos "$label: spec missing"
      grep -q "Optimize agent cluster for local hardware" "$TMP/c-$label.txt" || fail combos "$label: optimize not attempted"
      echo "combo $label: OK"
    }
    run_combo exhaustive --skip-daemon --search-strategy exhaustive --max-candidates 2 --objective test
    run_combo objective-first --skip-daemon --search-strategy objective-first --max-candidates 2 --objective test
    run_combo report-json --skip-daemon --report-output "$TMP/report.json" --objective test
    run_combo provider-override --skip-daemon --provider ollama --prefer agentic --objective test
    run_combo model-override --skip-daemon --ollama-model qwen2.5:7b --objective test
    ;;
  *)
    echo "apps-gate-checks: unknown check: $1" >&2
    exit 64
    ;;
esac
