#!/usr/bin/env bash
# Host orchestrator. It never calls dotnet; all pack/build/test commands run in devtest.
# Usage: <source-clone> <new-evidence-dir> <reconciliation.json> <semantic-reviews.json> <unique-version>
set -euo pipefail
[[ $# -eq 5 ]] || { echo "Expected source, evidence, reconciliation, semantic reviews, version" >&2; exit 2; }
CONTROL="$(cd "$(dirname "$0")" && pwd)"
SOURCE_REPO="$(realpath "$1")"
EVIDENCE="$(realpath -m "$2")"
RECONCILIATION="$(realpath "$3")"
SEMANTIC_REVIEWS="$(realpath "$4")"
PACKAGE_VERSION="$5"
EXPECTED_SHA=792446d09b59f98b47e08279907cb3d4df314caa
[[ "$PACKAGE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+-[a-zA-Z0-9.]+$ ]]
[[ -d "$SOURCE_REPO/.git" ]]
[[ "$(git -C "$SOURCE_REPO" rev-parse HEAD)" == "$EXPECTED_SHA" ]]
[[ -z "$(git -C "$SOURCE_REPO" status --porcelain)" ]]
python3 - "$SOURCE_REPO" "$EVIDENCE" <<'PY'
from pathlib import Path
import sys
source, evidence = map(lambda p: Path(p).resolve(), sys.argv[1:])
if evidence == source or evidence.is_relative_to(source):
    raise SystemExit("Evidence must remain outside the clean source clone")
if any(c in str(evidence) for c in (",", "\n", "\r")):
    raise SystemExit("Evidence path cannot contain Docker mount delimiters")
PY
mkdir "$EVIDENCE"
# Preserve prerequisite failures and image-build output as well as container output.
exec 3>&1 4>&2
exec > >(tee "$EVIDENCE/host.log") 2>&1
host_log_pid="$!"
host_finish() {
  host_result="$?"
  trap - EXIT
  printf 'HOST_BODY_EXIT_STATUS=%s\n' "$host_result"
  exec 1>&3 2>&4
  if wait "$host_log_pid"; then
    host_log_result=0
  else
    host_log_result="$?"
  fi
  if [[ "$host_result" -eq 0 && "$host_log_result" -ne 0 ]]; then
    host_result="$host_log_result"
  fi
  printf '%s\n' "$host_result" > "$EVIDENCE/host-exit-code.txt"
  printf 'HOST_EXIT_STATUS=%s LOG_EXIT_STATUS=%s\n' "$host_result" "$host_log_result"
  exit "$host_result"
}
trap host_finish EXIT
cp "$RECONCILIATION" "$EVIDENCE/reconciliation.json"
cp "$SEMANTIC_REVIEWS" "$EVIDENCE/semantic-reviews.json"
python3 "$CONTROL/verify-rehearsal-prerequisites.py" \
  --source-repo "$SOURCE_REPO" \
  --reconciliation "$EVIDENCE/reconciliation.json" \
  --semantic-reviews "$EVIDENCE/semantic-reviews.json" \
  --output "$EVIDENCE/prerequisites.json"
cp "$CONTROL/rehearse-packages.sh" "$EVIDENCE/rehearse-packages.sh"
cp "$CONTROL/verify-rehearsal-prerequisites.py" "$EVIDENCE/verify-rehearsal-prerequisites.py"
cp "$CONTROL/run-package-rehearsal.sh" "$EVIDENCE/run-package-rehearsal.sh"
cp "$CONTROL/campaign_evidence.py" "$EVIDENCE/campaign_evidence.py"
sha256sum "$EVIDENCE/"*.sh "$EVIDENCE/"*.py > "$EVIDENCE/controller-sha256.txt"

# Only after evidence reconciliation and all semantic reviews have passed may a container start.
command docker version > "$EVIDENCE/docker-version.txt"
IMAGE="$(bash "$SOURCE_REPO/scripts/ensure-devtest-image.sh")"
command docker image inspect "$IMAGE" > "$EVIDENCE/image.json"
export ASHLAR_DEVTEST_IMAGE="$IMAGE"
export ASHLAR_PACKAGE_EVIDENCE_MOUNT="$EVIDENCE"
docker() {
  if [[ $# -gt 0 && "$1" == run ]]; then
    shift
    command docker run \
      --mount "type=bind,src=$ASHLAR_PACKAGE_EVIDENCE_MOUNT,dst=/evidence" "$@"
  else
    command docker "$@"
  fi
}
export -f docker

set +e
bash "$SOURCE_REPO/scripts/test-in-container.sh" \
  --repo "$SOURCE_REPO" --ref "$EXPECTED_SHA" -- \
  "bash /evidence/rehearse-packages.sh '$EXPECTED_SHA' '$PACKAGE_VERSION'" \
  2>&1 | tee "$EVIDENCE/container.log"
rc="$?"
set -e
printf '%s\n' "$rc" > "$EVIDENCE/container-exit-code.txt"
[[ "$(git -C "$SOURCE_REPO" rev-parse HEAD)" == "$EXPECTED_SHA" ]]
git -C "$SOURCE_REPO" status --porcelain > "$EVIDENCE/host-final-status.txt"
[[ ! -s "$EVIDENCE/host-final-status.txt" ]]
if [[ "$rc" -eq 0 ]]; then
  [[ "$(cat "$EVIDENCE/package-rehearsal/exit-status.txt")" == 0 ]]
  grep -Fxq CLEAN_PACKAGE_REHEARSAL=OK "$EVIDENCE/package-rehearsal/rehearsal.log"
fi
exit "$rc"
