#!/usr/bin/env bash
# Run the automated dogfood campaign inside the repo's dev/test container.
# The .NET SDK is the container's SDK — do not install one on the host.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [[ "${ASHLAR_IN_DEVCONTAINER:-}" != "1" && ! -f /.dockerenv && ! -f /run/.containerenv ]]; then
  exec bash "$root/scripts/run-in-devcontainer.sh" bash "$root/scripts/run-dogfood-campaign.sh" "$@"
fi

full=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --full) full=1 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
  shift
done

echo "dogfood campaign: using container SDK"
dotnet --list-sdks
if [[ "$full" -eq 1 ]]; then
  exec dotnet run --project application/src/Ashlar.CLI -- dogfood campaign --full --verbose
fi
exec dotnet run --project application/src/Ashlar.CLI -- dogfood campaign --verbose
