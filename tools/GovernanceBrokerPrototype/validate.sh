#!/usr/bin/env bash
# Contract tests only. No service installation, privilege changes or isolation harness.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if ! command -v docker >/dev/null 2>&1; then
  echo "BLOCKED: Docker is unavailable; no .NET build or test was executed." >&2
  exit 127
fi
for framework in net8.0 net10.0; do
  bash "$root/scripts/test-in-container.sh" --repo "$root" --dirty --     "dotnet test tools/GovernanceBrokerPrototype.Tests/GovernanceBrokerPrototype.Tests.csproj --framework $framework --nologo --logger 'console;verbosity=normal'"
done
