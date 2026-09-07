#!/usr/bin/env bash
# Run a command inside the repo's dev/test container (SDK + ASP.NET 8 live there).
#
#   bash scripts/run-in-devcontainer.sh dotnet --info
#   bash scripts/run-in-devcontainer.sh bash scripts/run-dogfood-campaign.sh
#   bash scripts/run-in-devcontainer.sh          # interactive shell
#
# Already inside that container (or ASHLAR_IN_DEVCONTAINER=1) this is a no-op
# exec of the payload, so Makefile targets can wrap every dogfood recipe
# without nesting Docker.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

already_in_devcontainer() {
  [[ "${ASHLAR_IN_DEVCONTAINER:-}" == "1" ]] && return 0
  [[ -f /.dockerenv ]] && return 0
  [[ -f /run/.containerenv ]] && return 0
  return 1
}

if already_in_devcontainer; then
  if [[ $# -eq 0 ]]; then
    exec bash -l
  fi
  exec "$@"
fi

exec bash "${ROOT}/scripts/handoff/devbox.sh" "$@"
