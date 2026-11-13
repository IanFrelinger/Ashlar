#!/usr/bin/env bash
# Public metadata application contracts only. Never starts a service or native isolation probe.
# The unchanged mutation runner retains one ordinary green baseline for each complete
# Kernel net8/net10 and CLI net10 selection, then red/restored-green TRXs per case.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected_sha="${1:?expected full commit SHA required}"
[[ "$expected_sha" =~ ^[0-9a-f]{40}$ ]]
if [[ "$root" != /repo || ! -e /.dockerenv || ! -d /src-mirror/.git ]]; then
  echo 'Run through scripts/test-in-container.sh in its Linux devtest clone.' >&2
  exit 2
fi
cd "$root"
test "$(git rev-parse HEAD)" = "$expected_sha"
test -z "$(git status --porcelain)"
# Pin both test-host selection and the MSBuild property used by runtimeconfig.
# These projects otherwise inherit the repository's Major fallback.
export DOTNET_ROLL_FORWARD=LatestPatch
export RollForward=LatestPatch
printf 'RUNTIME INVENTORY BEGIN\n'
dotnet --list-runtimes
printf 'RUNTIME INVENTORY END\n'
finish() {
  original_status=$?
  trap - EXIT
  receipt_status=0
  python3 - "$expected_sha" <<'PY' || receipt_status=$?
from pathlib import Path
import json
import sys

selections = (
    ("src/Ashlar.Tests.Kernel", "Ashlar.Tests.Kernel", "net8.0"),
    ("src/Ashlar.Tests.Kernel", "Ashlar.Tests.Kernel", "net10.0"),
    ("application/src/Ashlar.Tests.CLI", "Ashlar.Tests.CLI", "net10.0"),
)
receipts = []
for project, assembly, framework in selections:
    path = Path(project) / "bin/Debug" / framework / f"{assembly}.runtimeconfig.json"
    document = json.loads(path.read_text())
    options = document["runtimeOptions"]
    family = framework.removeprefix("net")
    frameworks = options.get("frameworks", [options.get("framework", {})])
    if options.get("tfm") != framework or options.get("rollForward") != "LatestPatch":
        raise SystemExit(f"{path}: runtime target/roll-forward mismatch")
    if not any(item.get("name") == "Microsoft.NETCore.App" and str(item.get("version", "")).startswith(family + ".") for item in frameworks):
        raise SystemExit(f"{path}: runtime family mismatch")
    receipts.append({"path": str(path), "runtimeconfig": document})
print("PUBLIC_IDENTITY_RUNTIME_RECEIPT " + json.dumps({"source_sha": sys.argv[1], "runtimeconfigs": receipts}, separators=(",", ":")))
PY
  if [[ "$original_status" -ne 0 ]]; then exit "$original_status"; fi
  exit "$receipt_status"
}
trap finish EXIT
python3 scripts/run-spec007-mutation-batch.py \
  --manifest scripts/mutations/public-identity.json --archive-stdout --timeout-seconds 600
