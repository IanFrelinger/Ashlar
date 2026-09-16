#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

bash "${ROOT}/scripts/sandbox/init-agent-sandbox.sh" --project-root "${ROOT}" --profile runtime-studio

# The sandbox is a sibling of .ashlar/ (see scripts/sandbox/init-agent-sandbox.sh); .ashlar/ is
# governance state and the write floor refuses it.
mkdir -p "${ROOT}/agent-sandbox/tools/cache/tmp"
mkdir -p "${ROOT}/agent-sandbox/tools/cache/nuget"
mkdir -p "${ROOT}/agent-sandbox/tools/cache/npm"
mkdir -p "${ROOT}/agent-sandbox/agents/workspaces/runtime-studio"

echo
echo "Runtime Studio bootstrap complete."
echo
echo "Next steps:"
echo "  Optimize for your hardware (scaffold spec → benchmark → recommend → optional daemon):"
echo "    bash apps/runtime-studio/scripts/optimize_agent_cluster.sh --objective 'your task' --verbose"
echo
echo "  Or skip optimization and run the agent set directly:"
echo "    bash apps/runtime-studio/scripts/run_agent_set_local.sh --duration 5m --disable-observation"
