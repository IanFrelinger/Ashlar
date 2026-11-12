#!/usr/bin/env bash
# Controller-only orchestrator: no host dotnet, publication, release, or deployment.
set -euo pipefail
[[ $# -eq 3 ]] || { echo 'Usage: run-host.sh source-clone new-evidence-dir plan.json' >&2; exit 2; }
CONTROL="$(cd "$(dirname "$0")" && pwd)"
SOURCE_REPO="$(realpath "$1")"
EVIDENCE="$(realpath -m "$2")"
PLAN="$(realpath "$3")"
EXPECTED_SHA="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["source_sha"])' "$PLAN")"
[[ "$EXPECTED_SHA" =~ ^[0-9a-f]{40}$ && -d "$SOURCE_REPO/.git" ]]
[[ "$(git -C "$SOURCE_REPO" rev-parse HEAD)" == "$EXPECTED_SHA" ]]
[[ -z "$(git -C "$SOURCE_REPO" status --porcelain --untracked-files=all)" ]]
python3 - "$SOURCE_REPO" "$EVIDENCE" <<'PY'
from pathlib import Path
import sys
source, evidence = map(lambda value: Path(value).resolve(), sys.argv[1:])
if evidence == source or evidence.is_relative_to(source):
    raise SystemExit('Evidence must remain outside source checkout')
if any(value in str(evidence) for value in (',', '\n', '\r')):
    raise SystemExit('Invalid Docker mount delimiter in evidence path')
PY
mkdir "$EVIDENCE"
exec 3>&1 4>&2
exec > >(tee "$EVIDENCE/host.log") 2>&1
host_log_pid="$!"
finish() {
  result="$?"
  trap - EXIT
  exec 1>&3 2>&4
  if wait "$host_log_pid"; then log_result=0; else log_result="$?"; fi
  if [[ "$result" -eq 0 && "$log_result" -ne 0 ]]; then result="$log_result"; fi
  printf '%s\n' "$result" > "$EVIDENCE/host-exit-code.txt"
  exit "$result"
}
trap finish EXIT
cp "$PLAN" "$EVIDENCE/plan.json"
cp "$CONTROL/run-exact-tests.py" "$CONTROL/run-host.sh" "$EVIDENCE/"
sha256sum "$EVIDENCE/plan.json" "$EVIDENCE/run-exact-tests.py" "$EVIDENCE/run-host.sh" > "$EVIDENCE/controller-sha256.txt"
command docker version > "$EVIDENCE/docker-version.txt"
IMAGE="$(bash "$SOURCE_REPO/scripts/ensure-devtest-image.sh")"
command docker image inspect "$IMAGE" > "$EVIDENCE/image.json"
export ASHLAR_DEVTEST_IMAGE="$IMAGE"
export ASHLAR_HARDENING_EVIDENCE_MOUNT="$EVIDENCE"
docker() {
  if [[ $# -gt 0 && "$1" == run ]]; then
    shift
    command docker run --mount "type=bind,src=$ASHLAR_HARDENING_EVIDENCE_MOUNT,dst=/evidence" "$@"
  else
    command docker "$@"
  fi
}
export -f docker
set +e
bash "$SOURCE_REPO/scripts/test-in-container.sh" --repo "$SOURCE_REPO" --ref "$EXPECTED_SHA" -- \
  'python3 /evidence/run-exact-tests.py' 2>&1 | tee "$EVIDENCE/container.log"
result="$?"
set -e
printf '%s\n' "$result" > "$EVIDENCE/container-exit-code.txt"
[[ "$(git -C "$SOURCE_REPO" rev-parse HEAD)" == "$EXPECTED_SHA" ]]
git -C "$SOURCE_REPO" status --porcelain --untracked-files=all > "$EVIDENCE/host-final-status.txt"
[[ ! -s "$EVIDENCE/host-final-status.txt" ]]
exit "$result"
