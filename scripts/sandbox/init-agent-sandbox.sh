#!/usr/bin/env bash
# Creates the agent sandbox tree as a SIBLING of .ashlar/, never inside it. .ashlar/ is the
# governance directory — the admission ledger, the forge queue, runtime state — and the write
# floor (ToolSandbox.TryResolveWritePath) refuses every path beneath it. A sandbox rooted there
# would either be unwritable or hand a cycle the package cache and the host-app project root.
set -euo pipefail

ROOT="$(pwd)"
SANDBOX_ROOT="${ROOT}/agent-sandbox"
PROFILE="default"
DRY_RUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --project-root)
      ROOT="${2:-}"
      shift 2
      ;;
    --profile)
      PROFILE="${2:-default}"
      shift 2
      ;;
    --dry-run)
      DRY_RUN=1
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      echo "Usage: $0 [--project-root <path>] [--profile <default|host-apps>] [--dry-run]" >&2
      exit 2
      ;;
  esac
done

if [[ -z "${ROOT}" ]]; then
  echo "--project-root must not be empty" >&2
  exit 2
fi

SANDBOX_ROOT="${ROOT}/agent-sandbox"

if [[ "${DRY_RUN}" -eq 0 ]]; then
  mkdir -p "${SANDBOX_ROOT}/agents/workspaces"
  mkdir -p "${SANDBOX_ROOT}/tools/bin"
  mkdir -p "${SANDBOX_ROOT}/tools/cache"
  mkdir -p "${SANDBOX_ROOT}/host_apps/projects"
  mkdir -p "${SANDBOX_ROOT}/host_apps/cache"
  mkdir -p "${SANDBOX_ROOT}/host_apps/runtimes"
  mkdir -p "${SANDBOX_ROOT}/logs"
  mkdir -p "${SANDBOX_ROOT}/tmp"
fi

echo "Initialized agent sandbox at: ${SANDBOX_ROOT}"
echo "Profile: ${PROFILE}"
if [[ "${DRY_RUN}" -eq 1 ]]; then
  echo "Mode: dry-run (no directories created)"
fi
echo
echo "Recommended environment exports:"
echo "  export ASHLAR_SANDBOX_ROOT=\"${SANDBOX_ROOT}\""
echo "  export ASHLAR_TOOL_ROOT=\"${SANDBOX_ROOT}/tools\""
echo "  export ASHLAR_HOST_APP_PROJECT_ROOT=\"${SANDBOX_ROOT}/host_apps/projects\""
echo "  export ASHLAR_HOST_APP_CACHE_ROOT=\"${SANDBOX_ROOT}/host_apps/cache\""
echo
echo "Optional cache/tool relocation (project-local):"
echo "  export XDG_CACHE_HOME=\"${SANDBOX_ROOT}/tools/cache\""
echo "  export NUGET_PACKAGES=\"${SANDBOX_ROOT}/tools/cache/nuget\""
echo "  export NPM_CONFIG_CACHE=\"${SANDBOX_ROOT}/tools/cache/npm\""
